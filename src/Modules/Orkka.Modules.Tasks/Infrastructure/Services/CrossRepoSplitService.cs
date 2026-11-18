namespace Orkka.Modules.Tasks.Infrastructure.Services;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Usage.Contracts;
using Orkka.SharedKernel.Ai;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Text;
using System.Text.Json.Serialization;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Splits a task the human marked as spanning repositories into one task per repository, and
/// writes the contract they share.
///
/// Runs once, off the submission path, behind the same <c>DependencyAnalysisStartedAt</c> gate
/// the dependency analyzer uses — so the queue cannot claim a half-split group. It REPLACES the
/// analyzer for its members: it knows the build order from the plan it just made, which beats
/// inferring edges from an agent roster that may not distinguish the two sides at all.
/// </summary>
public sealed class CrossRepoSplitService(
    IUserRunnerUtility runnerUtility,
    IUsageRecorder usageRecorder,
    TasksDbContext db,
    ITaskNumberService taskNumbers,
    ITaskNotifier taskNotifier,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    Orkka.Modules.SourceControl.Contracts.IRepoContextBuilder repoContextBuilder,
    IOptions<AnthropicOptions> anthropicOptions,
    ILogger<CrossRepoSplitService> logger) : ICrossRepoSplitter
{
    private readonly string _model = anthropicOptions.Value.PlannerModel;

    // A contract with real signatures for several endpoints is long, and a truncated reply reads
    // downstream as malformed JSON rather than as the truncation it is.
    private const int MaxTokens = 16_000;

    // Split across the repos, as the decomposer does — a second repo must not double the prompt.
    private const int MaxTreePaths = 300;
    private const int MaxClaudeMdChars = 6000;

    public async Task SplitAsync(Ulid taskId, CancellationToken ct = default)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null || task.GroupId is not { } groupId)
            return;

        var group = await db.TaskGroups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null || group.Status != TaskGroupStatus.Splitting)
            return;

        // Members already exist — a retry after the process died mid-split. Re-running would
        // duplicate every sibling, which is worse than leaving the group as it is.
        var alreadySplit = await db.Tasks.AnyAsync(t => t.GroupId == groupId && t.Id != taskId, ct);
        if (alreadySplit)
        {
            logger.LogInformation(
                "Group {GroupId} already has members — skipping the split for task {TaskId}",
                groupId, taskId);
            return;
        }

        var repos = (await projectsApi.ListReposAsync(task.ProjectId, ct))
            .Where(r => r.OrgId == task.OrgId)
            .ToList();

        var spanned = ResolveSpannedRepos(group, repos, task.RepoId);
        if (spanned.Count < 2)
        {
            await AbandonAsync(task, group,
                "This task no longer spans more than one repository, so it will run against its "
                + "own repository as an ordinary task.", ct);
            return;
        }

        SplitResponse parsed;
        try
        {
            parsed = await PlanSplitAsync(task, group, spanned, ct);
        }
        catch (RunnerUtilityUnavailableException ex)
        {
            // No platform-key fallback: the split runs on the creator's runner or not at all.
            logger.LogInformation(
                "Cross-repo split for task {TaskId} could not run: {Reason}", taskId, ex.Message);
            await AbandonAsync(task, group,
                $"{ex.Message} This task will run against its own repository only — the work "
                + "in the other repositories still needs its own tasks; re-create it as a "
                + "spanning task with the runner connected to split it.", ct);
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cross-repo split failed for task {TaskId}", taskId);
            await AbandonAsync(task, group,
                "The cross-repo split could not be planned, so this task will run against its "
                + "own repository only. The work in the other repositories still needs its own "
                + "tasks — re-create it as a spanning task to try again.", ct);
            return;
        }

        var parts = MatchPartsToRepos(parsed.Parts, spanned);

        // The model's own escape hatch, and the same shape as a reply we could not use: one
        // repo's worth of work does not need a group, a gate, or a second agent run.
        if (parts.Count < 2)
        {
            await AbandonAsync(task, group,
                "Planning found no work outside this task's own repository, so it will run as an "
                + "ordinary task.", ct);
            return;
        }

        await ApplySplitAsync(task, group, parts, parsed.Contract, ct);
    }

    /// <summary>
    /// The repos the group is meant to cover: whatever the human picked, plus the originating
    /// task's own repo, filtered to repos the project still has.
    /// </summary>
    private static List<Orkka.Modules.Projects.Contracts.RepoSummary> ResolveSpannedRepos(
        TaskGroup group, List<Orkka.Modules.Projects.Contracts.RepoSummary> repos, Ulid? primaryRepoId)
    {
        var wanted = SpannedRepoIds.Parse(group.SpansRepoIdsJson).ToHashSet();
        if (primaryRepoId is { } primary)
            wanted.Add(primary);

        return repos.Where(r => wanted.Contains(r.Id)).ToList();
    }

    private async Task<SplitResponse> PlanSplitAsync(
        TaskEntity task, TaskGroup group,
        List<Orkka.Modules.Projects.Contracts.RepoSummary> spanned, CancellationToken ct)
    {
        var context = await BuildContextAsync(task, group, spanned, ct);

        // On the creator's own runner (their subscription, $0); a miss throws
        // RunnerUtilityUnavailableException, which SplitAsync turns into an honest abandon.
        var response = await runnerUtility.CompleteOnRunnerAsync(
            task.OrgId, task.CreatedByUserId, new AnthropicRequest
            {
                Model = _model,
                MaxTokens = MaxTokens,
                System = DefaultPrompts.CrossRepoSplit + AgentLanguage.Instruction(
                    ProjectLanguage.Resolve(await projectsApi.GetProjectSummaryAsync(task.ProjectId, ct), task.RequesterLocale)),
                Messages = [new AnthropicMessage("user", context)],
            }, "split this task across its repositories", ct);

        if (response.InputTokens > 0 || response.OutputTokens > 0)
        {
            await usageRecorder.RecordUsageAsync(new RecordUsageCommand(
                task.OrgId, task.ProjectId, task.Id, null, null,
                _model, response.InputTokens, response.OutputTokens,
                FlatCostBase: 0m,
                UserId: task.CreatedByUserId), ct);
        }

        try
        {
            return ModelJson.Deserialize<SplitResponse>(response.Text, "split");
        }
        catch (ModelOutputException ex)
        {
            // The raw reply goes to the log, never to the user — thousands of characters, and
            // only useful to whoever is debugging the prompt.
            logger.LogError(
                "Split parse failed for task {TaskId}: {Reason}. Raw output: {RawOutput}",
                task.Id, ex.Message, ex.RawOutput);
            throw;
        }
    }

    private async Task<string> BuildContextAsync(
        TaskEntity task, TaskGroup group,
        List<Orkka.Modules.Projects.Contracts.RepoSummary> spanned, CancellationToken ct)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## The Request").AppendLine();
        sb.AppendLine($"Title: {group.Title}").AppendLine();
        var request = string.IsNullOrWhiteSpace(group.Request) ? task.Description : group.Request;
        if (!string.IsNullOrWhiteSpace(request))
            sb.AppendLine(request).AppendLine();

        sb.AppendLine("## Repositories To Split Across").AppendLine();
        foreach (var repo in spanned)
        {
            var marker = repo.Id == task.RepoId ? " (the task was filed here originally)" : "";
            sb.AppendLine($"- {repo.Name}{marker}");
        }
        sb.AppendLine();

        // Best-effort, exactly as in decomposition: a repo that was never cloned, a bad
        // credential or a missing branch degrades the split rather than failing it.
        var perRepoPaths = Math.Max(MaxTreePaths / spanned.Count, 60);
        var perRepoClaudeMd = Math.Max(MaxClaudeMdChars / spanned.Count, 1500);

        foreach (var repo in spanned)
        {
            try
            {
                var context = await repoContextBuilder.BuildAsync(
                    new Orkka.Modules.SourceControl.Contracts.RepoContextRequest(
                        task.OrgId, repo.Id, repo.Name, repo.DefaultBranch,
                        perRepoPaths, perRepoClaudeMd, task.RunnerUserId ?? task.CreatedByUserId), ct);

                if (context is not null)
                    sb.AppendLine(context).AppendLine();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not read repo {RepoId} for the split of task {TaskId} — planning "
                    + "without its tree", repo.Id, task.Id);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Pairs each planned part with a real repo, keeping the model's order.
    ///
    /// A repo named twice keeps its first part and drops the rest: two tasks in one repo would
    /// share a worktree and a branch, and the second would silently overwrite the first.
    /// </summary>
    private List<ResolvedPart> MatchPartsToRepos(
        List<SplitPart>? parts, List<Orkka.Modules.Projects.Contracts.RepoSummary> spanned)
    {
        if (parts is not { Count: > 0 })
            return [];

        var byName = spanned
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var resolved = new List<ResolvedPart>(parts.Count);
        var claimed = new HashSet<Ulid>();

        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (part.Repo is not { Length: > 0 } name
                || !byName.TryGetValue(name.Trim(), out var repo))
            {
                logger.LogWarning("Split named an unknown repository {Repo} — dropping the part", part.Repo);
                continue;
            }

            if (!claimed.Add(repo.Id))
            {
                logger.LogWarning("Split named repository {Repo} twice — keeping the first part", name);
                continue;
            }

            resolved.Add(new ResolvedPart(
                repo,
                string.IsNullOrWhiteSpace(part.Title) ? repo.Name : part.Title.Trim(),
                part.Description,
                // Indices refer to the model's array; remapped once the drops are known.
                (part.DependsOn ?? []).Where(d => d >= 0 && d < i).ToList(),
                SourceIndex: i));
        }

        return resolved;
    }

    /// <summary>
    /// Writes the split: the contract on the group, the originating task rewritten as its own
    /// repo's half, a task per remaining repo, and the build order as real edges.
    /// </summary>
    private async Task ApplySplitAsync(
        TaskEntity task, TaskGroup group, List<ResolvedPart> parts, string? contract,
        CancellationToken ct)
    {
        // Numbers for the new halves first, on their own: the counter lives on the projects
        // table and SQLite has one writer, so reserving inside the transaction would wait on
        // it. A split that fails to save leaves a gap in the numbering, which is fine.
        var newHalves = parts.Count(p => p.Repo.Id != task.RepoId);
        var nextNumber = newHalves == 0
            ? 0
            : await taskNumbers.ReserveRangeAsync(task.ProjectId, newHalves, ct) - newHalves + 1;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        group.Contract = string.IsNullOrWhiteSpace(contract) ? null : contract.Trim();
        group.ContractSource = ContractSource.Declared;
        group.Status = TaskGroupStatus.Active;

        // Source index -> the task that implements it, so the model's dependsOn indices survive
        // the parts that were dropped for naming an unknown or duplicated repo.
        var bySourceIndex = new Dictionary<int, TaskEntity>(parts.Count);
        var queuePosition = await MaxQueuePositionAsync(task.OrgId, task.ProjectId, ct);

        foreach (var part in parts)
        {
            // The originating task keeps its identity — its number, its URL, whatever the human
            // already linked to — and takes over its own repo's half of the work.
            if (part.Repo.Id == task.RepoId)
            {
                task.Title = part.Title;
                task.Description = part.Description ?? task.Description;
                bySourceIndex[part.SourceIndex] = task;
                continue;
            }

            var sibling = new TaskEntity
            {
                // Explicit id: the SaveChanges interceptor assigns ids too late for the edges
                // below to reference them.
                Id = Ulid.NewUlid(),
                OrgId = task.OrgId,
                ProjectId = task.ProjectId,
                GroupId = group.Id,
                MilestoneId = task.MilestoneId,
                RepoId = part.Repo.Id,
                Number = nextNumber++,
                Title = part.Title,
                Description = part.Description,
                Priority = task.Priority,
                Type = task.Type,
                // Submitted, not drafted. The human already approved this work when they marked
                // the request as spanning repos; leaving the other halves in Drafting would mean
                // the gate waits forever on tasks nobody knew to submit. Each sibling still hits
                // its own PM review and plan approval before any code is written.
                CurrentLane = Lane.Ready,
                Status = TaskStatus.InQueue,
                QueuePosition = ++queuePosition,
                CreatedByUserId = task.CreatedByUserId,
                RequesterLocale = task.RequesterLocale,
                SkipPlanning = task.SkipPlanning,
                SkipTesting = task.SkipTesting,
                SkipCodeReview = task.SkipCodeReview,
                Model = task.Model,
                // Where a task runs is a property of the human who asked for it, not of the
                // repo, so every half runs wherever the original was going to.
                RunnerUserId = task.RunnerUserId,
                AutopilotSessionId = task.AutopilotSessionId,
            };

            db.Tasks.Add(sibling);
            bySourceIndex[part.SourceIndex] = sibling;
        }

        foreach (var part in parts)
        {
            if (!bySourceIndex.TryGetValue(part.SourceIndex, out var dependent))
                continue;

            foreach (var dependsOnIndex in part.DependsOn)
            {
                if (!bySourceIndex.TryGetValue(dependsOnIndex, out var blocker)
                    || blocker.Id == dependent.Id)
                    continue;

                db.TaskDependencies.Add(new TaskDependency
                {
                    Id = Ulid.NewUlid(),
                    OrgId = task.OrgId,
                    TaskId = dependent.Id,
                    DependsOnTaskId = blocker.Id,
                    Reason = "Cross-repo split — this side calls an interface the other defines.",
                    // Manual, not Ai: the dependency analyzer clears and rewrites its own rows
                    // on every run and would drop these on the first re-analysis.
                    Source = DependencySource.Manual,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Split task {TaskId} across {Count} repos under group {GroupId}, contract {ContractChars} chars",
            task.Id, parts.Count, group.Id, group.Contract?.Length ?? 0);

        foreach (var created in bySourceIndex.Values)
        {
            if (created.Id == task.Id)
                continue;

            await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                created.Id, created.Title, Lane.Drafting.ToString(), Lane.Ready.ToString(),
                created.Status.ToString(), DateTimeOffset.UtcNow));
        }
    }

    /// <summary>
    /// Gives up on the split and lets the task run alone.
    ///
    /// Deliberately leaves NO members behind: a group with one member would hold that task's PR
    /// behind a gate waiting on siblings that will never exist. Clearing the link is what keeps
    /// a planning failure to a planning failure.
    /// </summary>
    private async Task AbandonAsync(TaskEntity task, TaskGroup group, string reason, CancellationToken ct)
    {
        task.GroupId = null;
        group.Status = TaskGroupStatus.Cancelled;
        group.SettledAt = DateTimeOffset.UtcNow;

        db.TaskMessages.Add(new TaskMessage
        {
            Id = Ulid.NewUlid(),
            OrgId = task.OrgId,
            TaskId = task.Id,
            Role = "system",
            Content = reason,
            MessageType = MessageType.SystemEvent,
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Abandoned the cross-repo split for task {TaskId}: {Reason}", task.Id, reason);
    }

    private async Task<int> MaxQueuePositionAsync(Ulid orgId, Ulid projectId, CancellationToken ct)
        => await db.Tasks
            .Where(t => t.OrgId == orgId && t.ProjectId == projectId && t.QueuePosition != null)
            .MaxAsync(t => (int?)t.QueuePosition, ct) ?? 0;

    private sealed record ResolvedPart(
        Orkka.Modules.Projects.Contracts.RepoSummary Repo,
        string Title,
        string? Description,
        List<int> DependsOn,
        int SourceIndex);

    private sealed class SplitResponse
    {
        [JsonPropertyName("contract")]
        public string? Contract { get; set; }

        [JsonPropertyName("parts")]
        public List<SplitPart>? Parts { get; set; }
    }

    private sealed class SplitPart
    {
        [JsonPropertyName("repo")]
        public string? Repo { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("dependsOn")]
        public List<int>? DependsOn { get; set; }
    }
}
