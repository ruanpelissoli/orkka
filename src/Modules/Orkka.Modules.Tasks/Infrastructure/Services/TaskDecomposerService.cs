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

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

public sealed class TaskDecomposerService(
    IUserRunnerUtility runnerUtility,
    IUsageRecorder usageRecorder,
    TasksDbContext db,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    Orkka.Modules.SourceControl.Contracts.IRepoContextBuilder repoContextBuilder,
    Orkka.Modules.Agents.Contracts.IAgentsApi agentsApi,
    IOptions<AnthropicOptions> anthropicOptions,
    ILogger<TaskDecomposerService> logger) : ITaskDecomposer
{
    private readonly string _defaultModel = anthropicOptions.Value.PlannerModel;

    // Task plans are open-ended — the model decides how many tasks the work needs, and the
    // prompts steer it toward more, smaller tasks — so the budget has to fit a long plan of
    // fully-described tasks. A cut-off reply reads downstream as malformed JSON rather than
    // as the truncation it is.
    private const int TaskPlanMaxTokens = 32_000;

    // Not a planning target: the prompts leave the count to the model. This only stops a
    // runaway reply from flooding the board.
    private const int MaxTasks = 30;

    // Enough of the tree to infer stack and conventions, small enough not to dominate the
    // prompt. A repo with 4000 files does not become 10x more plannable at 4000 paths.
    private const int MaxTreePaths = 300;
    private const int MaxClaudeMdChars = 6000;
    private const int MaxExistingTasks = 40;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<DecompositionProposal> ProposeAsync(
        DecompositionRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(MilestonePlanSteps.ReadingProject);
        var context = await BuildContextAsync(request, ct);

        // Task titles/descriptions are written in the project's language; the caller's
        // locale only decides for projects without one (see ProjectLanguage.Resolve).
        var locale = ProjectLanguage.Resolve(
            await projectsApi.GetProjectSummaryAsync(request.ProjectId, ct), request.Locale);

        // Everything after this point is the model call, which is nearly all of the wall
        // clock — the phase the client counts elapsed seconds against.
        progress?.Report(MilestonePlanSteps.Planning);

        // On the requester's own runner (their subscription, $0) — the only place planning
        // runs. A miss throws RunnerUtilityUnavailableException, which the plan job reports
        // as "runner offline" rather than as a planning failure.
        var response = await runnerUtility.CompleteOnRunnerAsync(
            request.OrgId, request.RequesterUserId, new AnthropicRequest
            {
                Model = _defaultModel,
                MaxTokens = TaskPlanMaxTokens,
                System = DefaultPrompts.TaskDecompositionFromGoal + AgentLanguage.Instruction(locale),
                Messages = [new AnthropicMessage("user", context)],
            }, "plan this milestone", ct);

        if (response.InputTokens > 0 || response.OutputTokens > 0)
        {
            await usageRecorder.RecordUsageAsync(new RecordUsageCommand(
                request.OrgId, request.ProjectId, null, null, null,
                _defaultModel, response.InputTokens, response.OutputTokens,
                FlatCostBase: 0m,
                UserId: request.RequesterUserId), ct);
        }

        GoalDecompositionResponse parsed;
        try
        {
            parsed = ModelJson.Deserialize<GoalDecompositionResponse>(response.Text, "plan");
        }
        catch (ModelOutputException ex)
        {
            // The raw reply goes to the log, never to the caller — it is thousands of
            // characters and only useful to whoever is debugging the prompt.
            logger.LogError(
                "Plan parse failed for project {ProjectId}: {Reason}. Raw output: {RawOutput}",
                request.ProjectId, ex.Message, ex.RawOutput);
            throw;
        }

        if (parsed.Tasks is not { Count: > 0 })
            throw new ModelOutputException("The model returned a plan with no tasks.", response.Text);

        progress?.Report(MilestonePlanSteps.Ordering);
        var tasks = ToDecomposedTasks(parsed.Tasks);
        tasks = [.. await ApplyApiContractOrderingAsync(tasks, request, ct)];

        logger.LogInformation(
            "Planned goal into milestone '{Milestone}' with {Count} task(s) for project {ProjectId}",
            parsed.MilestoneTitle, tasks.Count, request.ProjectId);

        return new DecompositionProposal(
            // Falls back to the user's own words rather than inventing a title — a blank
            // heading is worse than an unpolished one.
            string.IsNullOrWhiteSpace(parsed.MilestoneTitle)
                ? Truncate(request.Goal, 200)
                : parsed.MilestoneTitle.Trim(),
            parsed.Outcome?.Trim() ?? "",
            tasks,
            ToPlannedContracts(parsed.Contracts, tasks));
    }

    /// <summary>
    /// Adds the API-before-client edges the model was asked for but may not have declared, and
    /// reorders the plan to match.
    ///
    /// Best-effort by design: if the agent roster cannot be read, or the project is not layered
    /// (single repo, managed/full-stack, all work on one side), the plan is returned exactly as
    /// the model produced it. A missing safety net is worse than no plan, but not by enough to
    /// fail a planning run the user paid for.
    /// </summary>
    private async Task<IReadOnlyList<DecomposedTask>> ApplyApiContractOrderingAsync(
        IReadOnlyList<DecomposedTask> tasks, DecompositionRequest request, CancellationToken ct)
    {
        try
        {
            var layers = await RepoLayerMap.BuildAsync(agentsApi, request.ProjectId, ct);
            if (!layers.IsLayered)
                return tasks;

            var reposByName = (await projectsApi.ListReposAsync(request.ProjectId, ct))
                .Where(r => r.OrgId == request.OrgId)
                .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

            // Same name-matching the accept endpoint uses when it persists the proposal, so the
            // layer this pass reasons about is the repo the task will actually be filed against.
            var ordered = ApiContractOrdering.Apply(tasks, layers, task =>
                task.Repo is { Length: > 0 } name && reposByName.TryGetValue(name, out var id)
                    ? id
                    : request.RepoId);

            if (!ReferenceEquals(ordered, tasks))
            {
                logger.LogInformation(
                    "Applied API-contract ordering to the plan for project {ProjectId}",
                    request.ProjectId);
            }

            return ordered;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not apply API-contract ordering for project {ProjectId} — keeping the "
                + "model's own dependency order", request.ProjectId);
            return tasks;
        }
    }

    /// <summary>
    /// Assembles what the model needs to plan against reality: the goal, the repo's shape, its
    /// own instructions, and the work already on the board.
    /// </summary>
    private async Task<string> BuildContextAsync(DecompositionRequest request, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Goal").AppendLine().AppendLine(request.Goal).AppendLine();

        await AppendRoadmapContextAsync(sb, request, ct);

        await AppendReposContextAsync(sb, request, ct);

        // Ordered by milestone, then lane — deliberately not by recency. On a project whose
        // first milestones are finished, "the 40 newest tasks" is all in-flight work and the
        // completed foundation drops out of view, so the model re-plans what already exists.
        var existing = await db.Tasks
            .AsNoTracking()
            .Where(t => t.OrgId == request.OrgId && t.ProjectId == request.ProjectId && !t.IsArchived)
            .OrderBy(t => t.MilestoneId == request.MilestoneId ? 0 : 1)
            .ThenByDescending(t => t.CurrentLane == Lane.Done)
            .ThenBy(t => t.Number)
            .Take(MaxExistingTasks)
            .Select(t => new { t.Number, t.Title, t.CurrentLane, t.MilestoneId })
            .ToListAsync(ct);

        if (existing.Count > 0)
        {
            sb.AppendLine("## Tasks already on the board (do not duplicate)").AppendLine();
            foreach (var t in existing)
            {
                var scope = t.MilestoneId == request.MilestoneId ? "this milestone" : "elsewhere";
                sb.AppendLine($"- #{t.Number} [{t.CurrentLane}, {scope}] {t.Title}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// The rest of the roadmap, marking which milestone is being planned.
    ///
    /// Without it a milestone is planned in isolation and quietly absorbs the scope of the ones
    /// after it — plan "Core booking flow" with no knowledge that "Payments" and "Notifications"
    /// are coming, and it plans those too. Later milestones are the important half: they are
    /// what the model must leave alone.
    /// </summary>
    private async Task AppendRoadmapContextAsync(
        StringBuilder sb, DecompositionRequest request, CancellationToken ct)
    {
        var roadmap = await db.Milestones
            .AsNoTracking()
            .Where(m => m.OrgId == request.OrgId && m.ProjectId == request.ProjectId
                && m.Status != MilestoneStatus.Cancelled)
            .OrderBy(m => m.Position)
            .Select(m => new { m.Id, m.Title, m.Outcome, m.Status })
            .ToListAsync(ct);

        if (roadmap.Count == 0)
            return;

        sb.AppendLine("## Project roadmap").AppendLine();

        if (request.MilestoneId is not null)
        {
            sb.AppendLine(
                "Plan ONLY the milestone marked PLANNING THIS ONE. Work belonging to a later "
                + "milestone is planned when its turn comes — do not pull it forward. Work "
                + "under an earlier milestone is already accounted for; do not repeat it.")
              .AppendLine();
        }

        // The first milestone is sized small on purpose (see RoadmapDecomposition), and the
        // bound has to reach the task planner too — on-demand "Generate tasks" on milestone 1
        // goes through here, not through the provisioning-time goal. It does NOT apply to a
        // roadmap of one: there, the milestone is the whole product and the bound would strand
        // most of the brief behind a milestone that never comes.
        var firstId = roadmap[0].Id;
        var onlyMilestone = roadmap.Count == 1;

        foreach (var m in roadmap)
        {
            var marker = m.Id != request.MilestoneId
                ? ""
                : m.Id == firstId && !onlyMilestone
                    ? " ← PLANNING THIS ONE (the first milestone — keep it to two or three tasks)"
                    : onlyMilestone
                        ? " ← PLANNING THIS ONE (the project's only milestone — plan everything the goal asks for)"
                        : " ← PLANNING THIS ONE";
            sb.AppendLine($"- [{m.Status}] {m.Title}{marker}");
            if (!string.IsNullOrWhiteSpace(m.Outcome))
                sb.AppendLine($"  {m.Outcome}");
        }

        sb.AppendLine();
    }

    /// <summary>
    /// Repo context is best-effort. A repo that has never been cloned, a bad credential, or a
    /// missing branch degrades the plan; it must not fail the request, because goal-only
    /// planning is still worth something.
    /// </summary>
    /// <summary>
    /// Names every repo in the project, then appends tree context for each. A multi-repo project
    /// that only ever saw one repo's tree had no way to file a Go API task anywhere but the repo
    /// it was shown — the model cannot choose between options it was never given.
    /// The tree budget is split across repos so adding a second one does not double the prompt.
    /// </summary>
    private async Task AppendReposContextAsync(
        StringBuilder sb, DecompositionRequest request, CancellationToken ct)
    {
        var repos = (await projectsApi.ListReposAsync(request.ProjectId, ct))
            .Where(r => r.OrgId == request.OrgId)
            .ToList();

        if (repos.Count == 0)
            return;

        if (repos.Count > 1)
        {
            sb.AppendLine("## Available Repositories").AppendLine();
            // The caller's repo choice (explicit pick, or the milestone's own repo) reaches
            // the model here — it used to be resolved and then never mentioned, so "plan this
            // against repo X" did not constrain the planner at all.
            foreach (var r in repos)
            {
                var marker = r.Id == request.RepoId
                    ? " ← the target repo for this plan: file tasks here unless the work clearly belongs in another repo"
                    : "";
                sb.AppendLine($"- {r.Name}{marker}");
            }
            sb.AppendLine();
        }

        var perRepoPaths = Math.Max(MaxTreePaths / repos.Count, 60);
        var perRepoClaudeMd = Math.Max(MaxClaudeMdChars / repos.Count, 1500);

        foreach (var repo in repos)
            await AppendRepoContextAsync(sb, request.OrgId, repo.Id, perRepoPaths, perRepoClaudeMd, request.RequesterUserId, ct);
    }

    private async Task AppendRepoContextAsync(
        StringBuilder sb, Ulid orgId, Ulid repoId, int maxTreePaths, int maxClaudeMdChars,
        string? runnerUserId, CancellationToken ct)
    {
        try
        {
            var repo = await projectsApi.GetRepoAsync(repoId, ct);
            if (repo is null || repo.OrgId != orgId)
                return;

            var context = await repoContextBuilder.BuildAsync(
                new Orkka.Modules.SourceControl.Contracts.RepoContextRequest(
                    orgId, repoId, repo.Name, repo.DefaultBranch,
                    maxTreePaths, maxClaudeMdChars, runnerUserId), ct);

            if (context is not null)
                sb.Append(context);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Repo context unavailable for repo {RepoId} — planning from the goal alone", repoId);
        }
    }

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "\n... (truncated)";

    /// <summary>
    /// Validates the model's task list and resolves its dependency indices onto the list we
    /// actually emit. Dropping a malformed task or hitting the <see cref="MaxTasks"/> cap
    /// shifts every position after it, so the indices have to be remapped rather than trusted —
    /// an unmapped one would silently attach the edge to whichever task took that slot.
    /// </summary>
    internal static List<DecomposedTask> ToDecomposedTasks(List<TaskDto> raw)
    {
        var kept = raw
            .Select((t, originalIndex) => (Task: t, OriginalIndex: originalIndex))
            .Where(x => !string.IsNullOrWhiteSpace(x.Task.Title)
                && !string.IsNullOrWhiteSpace(x.Task.Description))
            .Take(MaxTasks)
            .ToList();

        var remap = new Dictionary<int, int>(kept.Count);
        for (var i = 0; i < kept.Count; i++)
            remap[kept[i].OriginalIndex] = i;

        return kept
            .Select((x, newIndex) => new DecomposedTask(
                x.Task.Title.Trim(),
                x.Task.Description.Trim(),
                ValidatePriority(x.Task.Priority),
                ValidateType(x.Task.Type),
                ResolveDependsOn(x.Task.DependsOn, remap, newIndex),
                string.IsNullOrWhiteSpace(x.Task.Repo) ? null : x.Task.Repo.Trim(),
                x.Task.ContractKey))
            .ToList();
    }

    /// <summary>
    /// Keeps only contracts that at least TWO surviving tasks actually reference, and only where
    /// those tasks sit in different repositories.
    ///
    /// Both halves of the filter earn their place. A contract naming one task is not a contract —
    /// nothing crosses a boundary — and grouping it would hold that task's pull request behind a
    /// gate waiting on a sibling that does not exist. Two tasks in the SAME repository share a
    /// worktree, so the second would overwrite the first; they are ordinary dependent tasks and
    /// the model simply mislabelled them.
    /// </summary>
    internal static List<PlannedContract> ToPlannedContracts(
        List<ContractDto>? raw, IReadOnlyList<DecomposedTask> tasks)
    {
        if (raw is not { Count: > 0 })
            return [];

        var usable = tasks
            .Where(t => t.ContractKey is not null)
            .GroupBy(t => t.ContractKey!.Value)
            .Where(g => g.Count() > 1
                && g.Select(t => t.Repo ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        return raw
            .Where(c => usable.Contains(c.Key) && !string.IsNullOrWhiteSpace(c.Contract))
            .GroupBy(c => c.Key)
            .Select(g => new PlannedContract(g.Key, g.First().Contract!.Trim()))
            .ToList();
    }

    /// <summary>
    /// Keeps only backwards-pointing edges. Forward and self references are dropped rather
    /// than repaired: that is what makes a cycle impossible by construction instead of
    /// something the persistence layer has to detect.
    /// </summary>
    private static IReadOnlyList<int>? ResolveDependsOn(
        List<int>? raw, Dictionary<int, int> remap, int ownIndex)
    {
        if (raw is not { Count: > 0 })
            return null;

        var resolved = raw
            .Where(remap.ContainsKey)
            .Select(i => remap[i])
            .Where(i => i < ownIndex)
            .Distinct()
            .ToList();

        return resolved.Count > 0 ? resolved : null;
    }

    private static string ValidatePriority(string? priority)
    {
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Low", "Medium", "High", "Critical" };
        return valid.Contains(priority ?? "") ? priority! : "Medium";
    }

    private static string ValidateType(string? type)
    {
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Feature", "BugFix", "Refactor", "Improvement" };
        return valid.Contains(type ?? "") ? type! : "Feature";
    }

    private sealed class TaskDecompositionResponse
    {
        [JsonPropertyName("tasks")]
        public List<TaskDto>? Tasks { get; set; }
    }

    private sealed class GoalDecompositionResponse
    {
        [JsonPropertyName("milestoneTitle")]
        public string? MilestoneTitle { get; set; }

        [JsonPropertyName("outcome")]
        public string? Outcome { get; set; }

        [JsonPropertyName("tasks")]
        public List<TaskDto>? Tasks { get; set; }

        [JsonPropertyName("contracts")]
        public List<ContractDto>? Contracts { get; set; }
    }

    internal sealed class TaskDto
    {
        [JsonPropertyName("repo")]
        public string? Repo { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("priority")]
        public string? Priority { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("dependsOn")]
        public List<int>? DependsOn { get; set; }

        [JsonPropertyName("contractKey")]
        public int? ContractKey { get; set; }
    }

    internal sealed class ContractDto
    {
        [JsonPropertyName("key")]
        public int Key { get; set; }

        [JsonPropertyName("contract")]
        public string? Contract { get; set; }
    }
}
