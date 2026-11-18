namespace Orkka.Modules.Agents.Features.TaskChat;

using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Usage.Contracts;
using Orkka.SharedKernel.Ai;
using Orkka.SharedKernel.Utilities;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>The task, its project's agents and the agent this request talks to.</summary>
public sealed record TaskChatTarget(TaskSnapshot Task, IReadOnlyList<ProjectAgent> Agents, ProjectAgent Agent, ProjectAgent DefaultAgent);

/// <summary>What applying a piece of the discussion did to the task.</summary>
public sealed record TaskChatApplyResult(Lane Lane, TaskStatus Status, string Action, Ulid MessageId, string Content);

/// <summary>
/// The task's Discuss chat outside the turn itself: which task and agent a request means, the
/// lazily created conversation per (task, agent), and "Apply to task" — turning a reply (or a
/// summary of the whole discussion) into a human message on the task thread, through the same
/// board operations the thread and the plan card use.
/// </summary>
public sealed class TaskChatService(
    AgentsDbContext db,
    ITasksApi tasksApi,
    IUserRunnerUtility runnerUtility,
    IUsageRecorder usageRecorder,
    IProjectsApi projectsApi,
    IOptions<AnthropicOptions> anthropicOptions,
    ILogger<TaskChatService> logger)
{
    public const string AppliedPrefix = "From discussion:";
    public const string ActionAnswered = "answered";
    public const string ActionPlanChangesRequested = "planChangesRequested";
    public const string ActionPosted = "posted";

    internal const int SummarySourceCap = 16_000;
    private const int SummaryMessageCap = 2_500;

    /// <summary>Null when the task does not exist in this workspace, or the project has no
    /// agent; <paramref name="requestedAgentId"/> outside the project is treated as absent.</summary>
    public async Task<TaskChatTarget?> ResolveAsync(Ulid orgId, Ulid taskId, Ulid? requestedAgentId, CancellationToken ct)
    {
        var task = await tasksApi.GetTaskSnapshotAsync(taskId, ct);
        if (task is null || task.OrgId != orgId)
            return null;

        var agents = await db.ProjectAgents.AsNoTracking()
            .Where(a => a.OrgId == orgId && a.ProjectId == task.ProjectId)
            .OrderBy(a => a.Name)
            .ToListAsync(ct);

        var defaultAgent = TaskChatAgentSelector.Select(task, agents);
        if (defaultAgent is null)
            return null;

        var agent = requestedAgentId is { } requested
            ? agents.FirstOrDefault(a => a.Id == requested) ?? defaultAgent
            : defaultAgent;
        return new TaskChatTarget(task, agents, agent, defaultAgent);
    }

    /// <summary>The (task, agent) conversation, created on first use. A concurrent first use
    /// loses the unique-index race and reads the winner's row.</summary>
    public async Task<AgentConversation> GetOrCreateConversationAsync(TaskChatTarget target, CancellationToken ct)
    {
        var task = target.Task;
        var existing = await FindConversationAsync(task, target.Agent.Id, ct);
        if (existing is not null)
            return existing;

        var conversation = new AgentConversation
        {
            OrgId = task.OrgId,
            ProjectId = task.ProjectId,
            AgentId = target.Agent.Id,
            TaskId = task.Id,
            // Titled up front: the Discuss tab needs no generated title (and no utility call).
            Title = Truncate($"#{task.Number} {task.Title}", 500),
        };
        db.AgentConversations.Add(conversation);
        try
        {
            await db.SaveChangesAsync(ct);
            return conversation;
        }
        catch (DbUpdateException)
        {
            db.Entry(conversation).State = EntityState.Detached;
            return await FindConversationAsync(task, target.Agent.Id, ct)
                ?? throw new InvalidOperationException("The task's conversation could not be created.");
        }
    }

    private Task<AgentConversation?> FindConversationAsync(TaskSnapshot task, Ulid agentId, CancellationToken ct)
        => db.AgentConversations
            .FirstOrDefaultAsync(c => c.OrgId == task.OrgId && c.TaskId == task.Id && c.AgentId == agentId, ct);

    /// <summary>Thrown for an apply request the caller has to fix (no text, a message that is
    /// not this task's assistant reply) — 400.</summary>
    public sealed class InvalidApplyException(string message) : Exception(message);

    /// <summary>The runner could not produce the summary — 503 with the actionable reason.</summary>
    public sealed class SummaryUnavailableException(string message) : Exception(message);

    /// <summary>
    /// Posts the chosen text into the task thread as a human message attributed to the user,
    /// prefixed "From discussion:", then lets the lane decide what it means: in Pending Questions
    /// it is the answer (the task resumes); in Plan Ready it is "request changes" (the pending plan
    /// is rejected with it, so the task re-plans); anywhere else it is a thread message the next
    /// run reads. Failures of the board operations come back as a <see cref="BoardOpOutcome"/>.
    /// </summary>
    public async Task<(TaskChatApplyResult? Result, BoardOpOutcome? Failure)> ApplyAsync(
        TaskChatTarget target, Ulid? messageId, string? text, bool summarize,
        string userId, string? locale, CancellationToken ct)
    {
        var task = target.Task;
        var body = await ResolveApplyTextAsync(target, messageId, text, summarize, userId, locale, ct);
        var content = $"{AppliedPrefix}\n\n{body.Trim()}";

        string action;
        Ulid postedId;
        switch (task.CurrentLane)
        {
            case Lane.PendingQuestions:
            {
                var answer = await tasksApi.AnswerTaskAsync(task.OrgId, task.Id, content, nameof(MessageType.Answer), userId, locale, ct);
                if (!answer.Ok) return (null, answer);
                postedId = answer.EntityId!.Value;
                action = ActionAnswered;
                break;
            }
            case Lane.PlanReady:
            {
                var posted = await tasksApi.AnswerTaskAsync(task.OrgId, task.Id, content, nameof(MessageType.Text), userId, locale, ct);
                if (!posted.Ok) return (null, posted);
                postedId = posted.EntityId!.Value;
                action = ActionPosted;
                if (await tasksApi.GetPendingPlanAsync(task.OrgId, task.Id, ct) is { } plan)
                {
                    // The change request itself is the message above (the planner reads the whole
                    // thread); the rejection points at it instead of repeating it.
                    var review = await tasksApi.ReviewPlanAsync(task.OrgId, task.Id, plan.PlanId, approve: false,
                        "Changes requested from the Discuss chat — see the developer's \"From discussion:\" message.", ct);
                    if (!review.Ok) return (null, review);
                    action = ActionPlanChangesRequested;
                }
                break;
            }
            default:
            {
                var posted = await tasksApi.AnswerTaskAsync(task.OrgId, task.Id, content, nameof(MessageType.Text), userId, locale, ct);
                if (!posted.Ok) return (null, posted);
                postedId = posted.EntityId!.Value;
                action = ActionPosted;
                break;
            }
        }

        var after = await tasksApi.GetTaskSnapshotAsync(task.Id, ct) ?? task;
        logger.LogInformation("Discuss chat applied to task {TaskId}: {Action} → {Lane}", task.Id, action, after.CurrentLane);
        return (new TaskChatApplyResult(after.CurrentLane, after.Status, action, postedId, content), null);
    }

    private async Task<string> ResolveApplyTextAsync(
        TaskChatTarget target, Ulid? messageId, string? text, bool summarize,
        string userId, string? locale, CancellationToken ct)
    {
        if (summarize)
            return await SummarizeAsync(target, userId, locale, ct);

        if (messageId is { } id)
        {
            var message = await db.AgentMessages.AsNoTracking()
                .Where(m => m.Id == id && m.OrgId == target.Task.OrgId && m.Role == "assistant")
                .Join(db.AgentConversations.Where(c => c.TaskId == target.Task.Id),
                    m => m.ConversationId, c => c.Id, (m, _) => m.Content)
                .FirstOrDefaultAsync(ct);
            return string.IsNullOrWhiteSpace(message)
                ? throw new InvalidApplyException("That message is not a reply in this task's discussion.")
                : message;
        }

        return string.IsNullOrWhiteSpace(text)
            ? throw new InvalidApplyException("Say what to apply: a messageId, a text, or summarize.")
            : text;
    }

    /// <summary>A summary of the discussion with the target agent, written by the utility model
    /// on the developer's runner — their subscription, recorded at $0 against the task.</summary>
    internal async Task<string> SummarizeAsync(TaskChatTarget target, string userId, string? locale, CancellationToken ct)
    {
        var task = target.Task;
        var conversation = await FindConversationAsync(task, target.Agent.Id, ct);
        var messages = conversation is null
            ? []
            : await db.AgentMessages.AsNoTracking()
                .Where(m => m.OrgId == task.OrgId && m.ConversationId == conversation.Id)
                .OrderBy(m => m.CreatedAt)
                .Select(m => new { m.Role, m.Content })
                .ToListAsync(ct);
        if (messages.Count == 0)
            throw new InvalidApplyException("There is no discussion to summarize yet.");

        var transcript = new StringBuilder();
        foreach (var m in messages)
        {
            var who = m.Role == "user" ? "Developer" : target.Agent.Name;
            var content = m.Content.Length <= SummaryMessageCap ? m.Content : m.Content[..SummaryMessageCap] + " […]";
            transcript.Append($"{who}: {content}\n\n");
        }
        var source = transcript.ToString();
        // Newest wins when the discussion is long — it holds what was finally agreed.
        if (source.Length > SummarySourceCap)
            source = "[…earlier discussion omitted…]\n\n" + source[^SummarySourceCap..];

        var project = await projectsApi.GetProjectSummaryAsync(task.ProjectId, ct);
        var language = AgentLanguage.DisplayName(ProjectLanguage.Resolve(project, locale));
        var model = anthropicOptions.Value.UtilityModel;
        var response = await runnerUtility.TryCompleteAsync(task.OrgId, userId, new AnthropicRequest
        {
            Model = model,
            MaxTokens = 1_500,
            System = "You condense a discussion between a developer and an AI agent about one task into what the "
                + "team should act on. Write the decisions, answers to open questions, the agreed plan or plan "
                + "changes, and anything still open — as short markdown bullet points, no preamble, no sign-off. "
                + "Only what the discussion actually concluded; never invent requirements."
                + (language is { } lang ? $" Write in {lang}." : ""),
            Messages = [new AnthropicMessage("user", $"Task #{task.Number}: {task.Title}\n\nDiscussion:\n\n{source}")],
        }, ct);
        if (response is null || string.IsNullOrWhiteSpace(response.Text))
            throw new SummaryUnavailableException(
                RunnerUtilityUnavailableException.MessageFor("summarize the discussion"));

        await usageRecorder.RecordUsageAsync(new RecordUsageCommand(
            OrgId: task.OrgId,
            ProjectId: task.ProjectId,
            TaskId: task.Id,
            AgentId: target.Agent.Id,
            ExecutionId: null,
            Model: model,
            InputTokens: response.InputTokens,
            OutputTokens: response.OutputTokens,
            CacheReadTokens: response.CacheReadTokens,
            CacheWriteTokens: response.CacheWriteTokens,
            FlatCostBase: 0m,
            UserId: userId), ct);

        return response.Text.Trim();
    }

    /// <summary>The task's repository, and the branch each repo's Discuss copy should be on: the
    /// task branch for the task's own repo (once it has one), the default branch for the rest.</summary>
    internal static (RepoSummary? Repo, IReadOnlyDictionary<Ulid, string> Branches) TaskRepo(
        TaskSnapshot task, IReadOnlyList<RepoSummary> repos)
    {
        var repo = task.RepoId is { } repoId ? repos.FirstOrDefault(r => r.Id == repoId) : null;
        IReadOnlyDictionary<Ulid, string> branches = repo is not null && task.BranchName is { Length: > 0 } branch
            ? new Dictionary<Ulid, string> { [repo.Id] = branch }
            : new Dictionary<Ulid, string>();
        return (repo, branches);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
