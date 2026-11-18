namespace Orkka.Modules.Agents.Features.TaskChat;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Ai;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>Everything the Discuss chat's task-context block is rendered from — plain data, so
/// the rendering is testable without a database.</summary>
public sealed record TaskChatContextData(
    TaskSnapshot Task,
    string? RepoName,
    string? StoryContext,
    TaskChatPlan? Plan,
    IReadOnlyList<ConversationMessage> Thread,
    TaskChangeSummary? Changes);

/// <summary>The task's current plan: the one awaiting review, else the latest approved one.</summary>
public sealed record TaskChatPlan(string Status, string Summary, string? Approach, IReadOnlyList<string> Files);

/// <summary>
/// The task-context block of a Discuss chat's system prompt: the task, its tracker ticket, the
/// story it belongs to, the current plan, open questions, the recent thread and what the dev
/// runs changed so far. Everything a human, a tracker or an agent wrote is fenced as untrusted
/// (<see cref="PromptSafety.Wrap"/>), exactly like the pipeline prompts fence it. Each part is
/// capped so the whole block fits <c>SendMessageEndpoint.TaskContextCharCap</c>.
/// </summary>
public sealed class TaskChatContextBuilder(
    ITasksApi tasksApi,
    IServiceProvider services,
    ILogger<TaskChatContextBuilder> logger)
{
    internal const int ThreadMessageCount = 20;
    private const int DescriptionCap = 3_000;
    private const int PlanCap = 2_000;
    private const int MessageCap = 600;
    private const int ThreadCap = 3_000;
    private const int ChangedFilesShown = 25;

    public async Task<string> BuildAsync(TaskSnapshot task, string? repoName, CancellationToken ct)
    {
        var plan = await LoadPlanAsync(task, ct);
        var thread = await tasksApi.GetConversationMessagesAsync(task.Id, ct: ct);
        var changes = await tasksApi.GetTaskChangeSummaryAsync(task.Id, ct);
        var story = await StoryContextSectionAsync(task, ct);

        return Render(new TaskChatContextData(task, repoName, story, plan, thread, changes));
    }

    /// <summary>
    /// P7 HOOK — the story context section (parent story, sibling sub-tasks, what the earlier
    /// ones did). The single call site for it: the section itself is built by
    /// <c>TaskOrchestrator.BuildStoryContextSectionAsync</c> in the Execution module, reached
    /// through <see cref="ITaskStoryContextSource"/>. Until an implementation is registered
    /// this returns an empty string and the Discuss prompt simply has no story section.
    /// </summary>
    internal async Task<string> StoryContextSectionAsync(TaskSnapshot task, CancellationToken ct)
    {
        if (services.GetService<ITaskStoryContextSource>() is not { } source)
            return "";
        try
        {
            return await source.BuildStoryContextSectionAsync(task.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Discuss chat for task {TaskId} runs without its story context", task.Id);
            return "";
        }
    }

    private async Task<TaskChatPlan?> LoadPlanAsync(TaskSnapshot task, CancellationToken ct)
    {
        if (await tasksApi.GetPendingPlanAsync(task.OrgId, task.Id, ct) is { } pending)
            return new TaskChatPlan($"v{pending.Version}, awaiting the developer's approval",
                pending.Summary, pending.Approach, pending.Files);

        if (await tasksApi.GetLatestApprovedPlanAsync(task.Id, ct) is not { } approved)
            return null;

        string? approach = null;
        var files = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(approved.ContentJson);
            if (doc.RootElement.TryGetProperty("approach", out var a) && a.ValueKind == JsonValueKind.String)
                approach = a.GetString();
            if (doc.RootElement.TryGetProperty("filesChanged", out var f) && f.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in f.EnumerateArray())
                {
                    var action = file.TryGetProperty("action", out var act) ? act.GetString() : null;
                    var path = file.TryGetProperty("path", out var p) ? p.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(path))
                        files.Add(action is null ? path : $"{action}: {path}");
                }
            }
        }
        catch (JsonException)
        {
            // A plan whose content does not parse still has its summary.
        }
        return new TaskChatPlan("approved", approved.Summary, approach, files);
    }

    /// <summary>Renders the block. Pure — pinned by the prompt-assembly tests.</summary>
    internal static string Render(TaskChatContextData data)
    {
        var task = data.Task;
        var sb = new StringBuilder();

        sb.AppendLine("## The task you are discussing");
        sb.AppendLine("This is the task's Discuss chat: a conversation with the developer about the task below, "
            + "before or between the pipeline's runs. Answer their questions about the ticket, analyze the ticket "
            + "and the repository, point out what is unclear, and propose a plan when asked. You can READ the "
            + "repositories in your working directory but you cannot change anything here — no file edits, no "
            + "commands. When the developer likes something you said, they apply it to the task themselves: it "
            + "becomes the answer to the open questions, a change request on the plan awaiting approval, or a note "
            + "the next run reads. So make proposals concrete and self-contained.");
        sb.AppendLine();
        sb.AppendLine(PromptSafety.Notice);
        sb.AppendLine();

        sb.AppendLine($"Task #{task.Number} — title:");
        sb.AppendLine(PromptSafety.Wrap("task-title", task.Title));
        sb.AppendLine($"Type: {task.Type} · Priority: {task.Priority} · Lane: {task.CurrentLane} · Status: {task.Status}");
        if (data.RepoName is { Length: > 0 } repoName)
        {
            sb.Append($"Repository: {repoName}");
            sb.AppendLine(task.BranchName is { Length: > 0 } branch
                ? $" — your copy of `{repoName}/` is on the task branch `{branch}` when it has been pushed, "
                  + "otherwise on the default branch (the base the work starts from)."
                : " — no task branch yet: your copy is the default branch, the base the work will start from.");
        }
        if (task.WorkingSubpath is { Length: > 0 } subpath)
            sb.AppendLine($"The work belongs in `{subpath}` inside the repository.");

        sb.AppendLine();
        sb.AppendLine("### Description");
        sb.AppendLine(PromptSafety.Wrap("task-description",
            Cap(string.IsNullOrWhiteSpace(task.Description) ? "No description provided." : task.Description, DescriptionCap)));

        if (task.ExternalKey is { Length: > 0 } ticketKey)
        {
            sb.AppendLine();
            sb.AppendLine("### Tracker ticket");
            sb.AppendLine("The task was imported from this ticket; its text is the description above and its comments are in the thread below.");
            var ticket = new StringBuilder($"Key: {ticketKey}");
            if (task.ExternalUrl is { Length: > 0 } url)
                ticket.Append($"\nURL: {url}");
            if (task.ExternalEpic is { Length: > 0 } epic)
                ticket.Append($"\nEpic: {epic}");
            sb.AppendLine(PromptSafety.Wrap("tracker-ticket", ticket.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(data.StoryContext))
        {
            sb.AppendLine();
            sb.AppendLine(data.StoryContext.TrimEnd());
        }

        sb.AppendLine();
        sb.AppendLine("### Current plan");
        if (data.Plan is { } plan)
        {
            var body = new StringBuilder($"Status: {plan.Status}\nSummary: {plan.Summary}");
            if (!string.IsNullOrWhiteSpace(plan.Approach))
                body.Append($"\nApproach:\n{plan.Approach}");
            if (plan.Files.Count > 0)
                body.Append("\nFiles:\n" + string.Join("\n", plan.Files.Take(ChangedFilesShown).Select(f => $"- {f}")));
            sb.AppendLine(PromptSafety.Wrap("plan", Cap(body.ToString(), PlanCap)));
        }
        else
        {
            sb.AppendLine("No plan yet.");
        }

        var openQuestions = OpenQuestions(task, data.Thread);
        if (openQuestions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Open questions (the pipeline is waiting for the developer's answer)");
            foreach (var question in openQuestions)
                sb.AppendLine(PromptSafety.Wrap("question", Cap(question, MessageCap * 2)));
        }

        var recent = data.Thread.TakeLast(ThreadMessageCount).ToList();
        if (recent.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(recent.Count < data.Thread.Count
                ? $"### Task thread (the last {recent.Count} of {data.Thread.Count} messages, oldest first)"
                : "### Task thread (oldest first)");
            var entries = new List<string>();
            var used = 0;
            for (var i = recent.Count - 1; i >= 0; i--)
            {
                var m = recent[i];
                var entry = $"{Label(m)}:\n{PromptSafety.Wrap("message", Cap(m.Content, MessageCap))}\n";
                if (used + entry.Length > ThreadCap)
                    break;
                used += entry.Length;
                entries.Add(entry);
            }
            entries.Reverse();
            foreach (var entry in entries)
                sb.Append(entry);
        }

        sb.AppendLine();
        sb.AppendLine("### Code changes so far");
        if (data.Changes is { FileCount: > 0 } changes)
        {
            sb.AppendLine($"{changes.FileCount} file(s) changed, +{changes.Additions} −{changes.Deletions}:");
            foreach (var path in changes.FilePaths.Take(ChangedFilesShown))
                sb.AppendLine($"- {path}");
            if (changes.FilePaths.Count > ChangedFilesShown)
                sb.AppendLine($"- … and {changes.FilePaths.Count - ChangedFilesShown} more");
        }
        else
        {
            sb.AppendLine("None yet.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>The agent questions the pipeline is waiting on: while the task is in Pending
    /// Questions, every question asked after the last human message.</summary>
    internal static List<string> OpenQuestions(TaskSnapshot task, IReadOnlyList<ConversationMessage> thread)
    {
        if (task.CurrentLane != Lane.PendingQuestions && task.Status != TaskStatus.WaitingAnswer)
            return [];

        var questions = new List<string>();
        for (var i = thread.Count - 1; i >= 0; i--)
        {
            var m = thread[i];
            if (m.Role == "user")
                break;
            if (m.MessageType == MessageType.Question)
                questions.Add(m.Content);
        }
        questions.Reverse();
        return questions;
    }

    private static string Label(ConversationMessage m) => (m.Role, m.MessageType) switch
    {
        (_, MessageType.Question) => "Agent asked",
        ("user", MessageType.Answer) => "Developer answered",
        ("user", _) => "Developer said",
        ("reviewer", _) => "Reviewer feedback",
        ("qa", _) => "QA feedback",
        ("system", _) => "System note",
        ("tracker", _) => "Comment on the ticket",
        _ => m.Role,
    };

    private static string Cap(string value, int max)
        => value.Length <= max ? value : value[..max] + "\n[... truncated]";
}
