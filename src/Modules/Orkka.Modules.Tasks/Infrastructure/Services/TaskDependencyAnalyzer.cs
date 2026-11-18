namespace Orkka.Modules.Tasks.Infrastructure.Services;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Usage.Contracts;
using Orkka.SharedKernel.Ai;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Logging;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

public sealed class TaskDependencyAnalyzer(
    IUserRunnerUtility runnerUtility,
    TasksDbContext db,
    IUsageRecorder usageRecorder,
    ITaskNotifier taskNotifier,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    Orkka.Modules.Agents.Contracts.IAgentsApi agentsApi,
    IOptions<AnthropicOptions> anthropicOptions,
    ILogger<TaskDependencyAnalyzer> logger) : ITaskDependencyAnalyzer
{
    private readonly string _defaultModel = anthropicOptions.Value.FastUtilityModel;
    private const int MaxTokens = 2048;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true
    };

    private const string SystemPrompt = """
        You analyze software development tasks for dependencies between them.
        Task X "depends on" task Y if X requires code, database schema, API endpoints, components,
        or infrastructure that task Y will create or modify — and X cannot be properly implemented
        until Y is complete.

        There are two kinds, and both count:
        1. Same repository — X reads, imports, or extends code Y has not written yet.
        2. Across repositories — X CALLS an interface Y defines: an HTTP endpoint, an RPC method,
           a message contract, a shared schema. The two share no source code, and the dependency
           is real anyway: a client cannot be written against a contract that does not exist yet.
           A task in a web or mobile repo that consumes an endpoint a backend task will add
           depends on that backend task.

        Rules:
        - Only flag genuine dependencies, not conceptual or thematic relationships.
        - A task does NOT depend on another just because they touch the same area of the codebase,
          or because both serve the same feature.
        - Dependency means X cannot compile, run, or function correctly without Y's output.
        - Across repositories, require a named interface both sides plausibly share. "Both are
          part of checkout" is not a dependency; "this screen calls POST /orders, which that task
          creates" is.
        - Tasks already completed or in code review should NOT be listed as dependencies.
        - Return ONLY dependencies where the submitted task depends on a candidate task, not the reverse.
        - Return a JSON array: [{"dependsOnTaskId": "...", "reason": "..."}] or [] if no dependencies.
        - Return ONLY the JSON array, no markdown fences or extra text.
        """;

    /// <summary>
    /// Best-effort layer map. Analysis is a background pass on a path the user already paid
    /// for — an unreadable agent roster degrades it to same-repo-only, which is exactly the
    /// behaviour it had before cross-repo edges existed, and never fails the submission.
    /// </summary>
    private async Task<RepoLayerMap?> TryBuildLayerMapAsync(Ulid projectId, CancellationToken ct)
    {
        try
        {
            var layers = await RepoLayerMap.BuildAsync(agentsApi, projectId, ct);
            return layers.IsLayered ? layers : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not resolve repo layers for project {ProjectId} — analysing same-repo only",
                projectId);
            return null;
        }
    }

    /// <summary>Repo id to name for every repo represented in this analysis.</summary>
    private async Task<IReadOnlyDictionary<Ulid, string>> ResolveRepoNamesAsync(
        TaskEntity task, IEnumerable<Ulid?> candidateRepoIds, CancellationToken ct)
    {
        var ids = candidateRepoIds
            .Concat([task.RepoId])
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToHashSet();

        if (ids.Count <= 1)
            return new Dictionary<Ulid, string>();

        try
        {
            return (await projectsApi.ListReposAsync(task.ProjectId, ct))
                .Where(r => ids.Contains(r.Id))
                .ToDictionary(r => r.Id, r => r.Name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not resolve repo names for project {ProjectId} — omitting them from the prompt",
                task.ProjectId);
            return new Dictionary<Ulid, string>();
        }
    }

    public async Task AnalyzeDependenciesAsync(Ulid taskId, CancellationToken ct = default)
    {
        var task = await db.Tasks
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);

        if (task is null)
        {
            logger.LogWarning("Task {TaskId} not found for dependency analysis", taskId);
            return;
        }

        if (string.IsNullOrWhiteSpace(task.Description))
        {
            logger.LogDebug("Skipping dependency analysis for task {TaskId} — no description", taskId);
            return;
        }

        // Cross-repo candidates are admitted only for the case they exist to catch: a client
        // task whose API is being built in another repo. Widening the net unconditionally would
        // multiply the prompt by the project's repo count on every submission and invite
        // false edges between repos that never talk to each other.
        var layers = await TryBuildLayerMapAsync(task.ProjectId, ct);
        var submittedLayer = layers?.LayerOf(task.RepoId) ?? WorkLayer.Unknown;
        var crossRepo = submittedLayer == WorkLayer.ApiConsumer;

        // Find candidate tasks in the same project that aren't done yet. Same repo always;
        // other repos only when this task consumes an API, and then only their provider side.
        var candidates = await db.Tasks
            .AsNoTracking()
            .Where(t => t.ProjectId == task.ProjectId
                && t.Id != taskId
                && !t.IsArchived
                && t.Status != TaskStatus.Completed
                && t.CurrentLane != Lane.Done
                && t.CurrentLane != Lane.CodeReview
                && t.CurrentLane != Lane.Testing
                && (task.RepoId == null || crossRepo || t.RepoId == task.RepoId))
            .Select(t => new { t.Id, t.Title, t.Description, t.Type, t.Status, t.CurrentLane, t.RepoId })
            .ToListAsync(ct);

        if (crossRepo && layers is not null)
        {
            candidates = [.. candidates.Where(c =>
                c.RepoId == task.RepoId || layers.LayerOf(c.RepoId) == WorkLayer.ApiProvider)];
        }

        if (candidates.Count == 0)
        {
            logger.LogDebug("No candidate tasks for dependency analysis of task {TaskId}", taskId);
            return;
        }

        logger.LogInformation(
            "Analyzing dependencies for task {TaskId} against {CandidateCount} candidates",
            taskId, candidates.Count);

        // Build the user message
        var sb = new StringBuilder();
        sb.AppendLine($"Submitted task:");
        sb.AppendLine($"- ID: {task.Id}");
        sb.AppendLine($"- Title: {task.Title}");
        sb.AppendLine($"- Description: {task.Description}");
        sb.AppendLine($"- Type: {task.Type}");
        sb.AppendLine();
        // Repo names are in the prompt only when more than one is represented. On a single-repo
        // project they are noise; on a multi-repo one they are the whole point — the model
        // cannot judge a cross-repo contract dependency without knowing which side is which.
        var repoNames = await ResolveRepoNamesAsync(task, candidates.Select(c => c.RepoId), ct);

        if (repoNames.Count > 1 && task.RepoId is { } submittedRepoId
            && repoNames.TryGetValue(submittedRepoId, out var submittedRepoName))
        {
            sb.AppendLine($"- Repository: {submittedRepoName}");
            sb.AppendLine();
        }

        sb.AppendLine(repoNames.Count > 1
            ? "Other tasks in this project (repository shown per task):"
            : "Other tasks in this project (same repository):");

        foreach (var c in candidates)
        {
            var repo = repoNames.Count > 1 && c.RepoId is { } rid
                && repoNames.TryGetValue(rid, out var name)
                ? $" | Repository: {name}"
                : "";

            sb.AppendLine($"- ID: {c.Id} | Title: {c.Title} | Type: {c.Type} | Status: {c.Status} | Lane: {c.CurrentLane}{repo}");
            if (!string.IsNullOrWhiteSpace(c.Description))
                sb.AppendLine($"  Description: {c.Description}");
        }

        // On the creator's own runner (their subscription, $0; the same machine their agent
        // runs use). No platform-key fallback: a miss leaves the task's dependencies as they
        // are — manual edges still apply, and the next submission analyses again.
        var (dependencies, inputTokens, outputTokens) =
            await CallRunnerAsync(sb.ToString(), task.OrgId, task.CreatedByUserId, taskId, ct);

        // Runner-served analysis records at $0 (FlatCostBase), the same convention runner
        // agent runs use — attribution stays visible, spend stays zero.
        if (inputTokens > 0 || outputTokens > 0)
        {
            await usageRecorder.RecordUsageAsync(new RecordUsageCommand(
                task.OrgId, task.ProjectId, taskId, null, null,
                _defaultModel, inputTokens, outputTokens,
                FlatCostBase: 0m,
                UserId: task.CreatedByUserId), ct);
        }

        if (dependencies is not { Count: > 0 })
        {
            logger.LogInformation("No dependencies detected for task {TaskId}", taskId);
            return;
        }

        // Validate and persist
        var candidateIds = candidates.Select(c => c.Id).ToHashSet();

        // Clear previous AI-detected dependencies for idempotency
        var deleted = await db.TaskDependencies
            .Where(d => d.TaskId == taskId && d.Source == DependencySource.Ai)
            .ExecuteDeleteAsync(ct);

        var added = 0;
        foreach (var dep in dependencies)
        {
            if (!Ulid.TryParse(dep.DependsOnTaskId, out var depId))
                continue;

            if (!candidateIds.Contains(depId))
                continue;

            // Check for circular dependency
            if (await TaskDependencyGraph.WouldCreateCycleAsync(db, taskId, depId, ct))
            {
                logger.LogWarning(
                    "Skipping circular dependency: task {TaskId} -> {DepId}", taskId, depId);
                continue;
            }

            // Check for duplicates (manual dependency might already exist)
            var exists = await db.TaskDependencies
                .AnyAsync(d => d.TaskId == taskId && d.DependsOnTaskId == depId, ct);

            if (exists)
                continue;

            db.TaskDependencies.Add(new TaskDependency
            {
                TaskId = taskId,
                DependsOnTaskId = depId,
                Reason = dep.Reason?.Length > 1000 ? dep.Reason[..1000] : dep.Reason,
                Source = DependencySource.Ai,
                OrgId = task.OrgId
            });

            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Added {Count} AI-detected dependencies for task {TaskId}", added, taskId);
        }

        // Analysis runs fire-and-forget after submission, so without this event the board
        // would only show the new blockers after a reload.
        if (added > 0 || deleted > 0)
            await NotifyDependenciesChangedAsync(task, ct);
    }

    private async Task NotifyDependenciesChangedAsync(TaskEntity task, CancellationToken ct)
    {
        try
        {
            var project = await projectsApi.GetProjectSummaryAsync(task.ProjectId, ct);
            if (project is null)
                return;

            var blockedBy = await db.TaskDependencies
                .AsNoTracking()
                .Where(d => d.TaskId == task.Id)
                .Where(Features.Tasks.TaskDependencyQueries.IsUnmet)
                .Select(d => new { d.DependsOnTaskId, d.DependsOnTask!.Number, d.DependsOnTask!.Status, d.DependsOnTask!.IsMerged })
                .ToListAsync(ct);

            await taskNotifier.SendTaskDependenciesChangedAsync(task.ProjectId, new TaskDependenciesChangedEvent(
                task.Id,
                blockedBy.Select(b => new BlockingTaskRef(b.DependsOnTaskId, $"{project.Key}-{b.Number}", b.Status.ToString(), b.IsMerged)).ToList(),
                DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            // Realtime is best-effort — never fail dependency analysis over a notification.
            logger.LogWarning(ex, "Failed to notify dependency change for task {TaskId}", task.Id);
        }
    }

    private async Task<(List<DependencyDto>? Dependencies, int InputTokens, int OutputTokens)>
        CallRunnerAsync(string userMessage, Ulid orgId, string? creatorUserId, Ulid taskId, CancellationToken ct)
    {
        try
        {
            var request = new AnthropicRequest
            {
                Model = _defaultModel,
                MaxTokens = MaxTokens,
                System = SystemPrompt,
                Messages = [new AnthropicMessage("user", userMessage)],
            };

            var response = await runnerUtility.TryCompleteAsync(orgId, creatorUserId, request, ct);
            if (response is null)
            {
                logger.LogInformation(
                    "Dependency analysis for task {TaskId} skipped — {Reason}", taskId,
                    RunnerUtilityUnavailableException.MessageFor("analyse task dependencies"));
                return (null, 0, 0);
            }

            if (string.IsNullOrWhiteSpace(response.Text))
                return (null, response.InputTokens, response.OutputTokens);

            var deps = ParseDependencies(response.Text, logger);
            return (deps, response.InputTokens, response.OutputTokens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Dependency analysis call failed for task {TaskId}", taskId);
            return (null, 0, 0);
        }
    }

    /// <summary>
    /// Extracts the JSON array from the model's reply and deserializes it. The prompt says
    /// "no markdown fences" but the model sometimes fences or explains anyway; feeding that
    /// wrapping to the deserializer used to throw and silently degrade an Autopilot board to
    /// "no dependencies". Anything stripped is logged so the prompt drift stays visible.
    /// </summary>
    internal static List<DependencyDto>? ParseDependencies(string text, ILogger logger)
    {
        var json = ModelJson.ExtractArray(text);
        if (json is null)
        {
            logger.LogWarning(
                "Dependency analysis reply contained no complete JSON array — treating as no dependencies. Reply: {Reply}",
                Truncate(text));
            return null;
        }

        var start = text.IndexOf(json, StringComparison.Ordinal);
        var before = text[..start].Trim();
        var after = text[(start + json.Length)..].Trim();
        if (before.Length > 0 || after.Length > 0)
        {
            logger.LogWarning(
                "Stripped non-JSON wrapping from dependency analysis reply — before: {Before} | after: {After}",
                Truncate(before), Truncate(after));
        }

        return JsonSerializer.Deserialize<List<DependencyDto>>(json, JsonOptions);
    }

    private static string Truncate(string text) =>
        text.Length <= 500 ? text : text[..500] + "…";

    internal sealed class DependencyDto
    {
        [JsonPropertyName("dependsOnTaskId")]
        public string DependsOnTaskId { get; set; } = "";

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }
}
