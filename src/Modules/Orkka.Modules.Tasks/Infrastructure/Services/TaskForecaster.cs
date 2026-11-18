namespace Orkka.Modules.Tasks.Infrastructure.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Ai;

/// <summary>
/// Forecasts how much agent time a task will take.
///
/// Every task runs on its owner's runner, on their own AI subscription, so there is no cost to
/// forecast: the cost band is always suppressed and only agent time is estimated. A task's time
/// is a sum over the lanes that will run, and which lanes run (the skip flags) is known exactly
/// before it starts; only each lane's duration has to be sampled from history.
/// </summary>
public sealed class TaskForecaster(
    TasksDbContext db,
    IAgentsApi agentsApi,
    IProjectsApi projectsApi,
    ILogger<TaskForecaster> logger) : ITaskForecaster
{
    /// <summary>Below this in any contributing lane, the whole forecast is suppressed. A
    /// confident wrong number is worse than an empty state.</summary>
    private const int MinSamplesPerLane = 5;

    /// <summary>Recent completed tasks to sample. Enough to smooth noise, recent enough that a
    /// project's older, slower era doesn't dominate.</summary>
    private const int SampleTaskLimit = 20;

    private const int BootstrapIterations = 1000;

    public async Task<TaskForecastResult?> ForecastAsync(Ulid taskId, CancellationToken ct = default)
    {
        var task = await db.Tasks
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);

        if (task is null)
            return null;

        return await ForecastDraftAsync(new TaskForecastInput(
            task.OrgId, task.ProjectId, task.Type, task.Model, task.AssignedAgentId,
            task.SkipPmReview, task.SkipPlanning, task.SkipTesting, task.SkipCodeReview,
            task.Id), ct);
    }

    public async Task<TaskForecastResult?> ForecastDraftAsync(
        TaskForecastInput input, CancellationToken ct = default)
    {
        var project = await projectsApi.GetProjectSummaryAsync(input.ProjectId, ct);
        var agents = await agentsApi.ListAgentsAsync(input.ProjectId, ct);

        var lanes = PlannedLanes(input, project?.Settings?.QaEnabled == true);
        if (lanes.Count == 0)
            return null;

        var samples = await LoadSamplesAsync(input, lanes, ct);
        if (samples is null)
        {
            logger.LogDebug(
                "Forecast suppressed for project {ProjectId}: no scope has {Floor} samples in every planned lane",
                input.ProjectId, MinSamplesPerLane);
            return null;
        }

        var laneAssumptions = new List<ForecastLaneAssumption>();
        var timeDraws = new List<List<int>>();

        foreach (var lane in lanes)
        {
            var model = ResolveModel(lane, input, agents);
            var bucket = samples.For(lane);
            laneAssumptions.Add(new ForecastLaneAssumption(lane.ToString(), model, bucket.Count));
            timeDraws.Add(bucket.Select(s => s.AgentSeconds).ToList());
        }

        // Bootstrap rather than summing per-lane quantiles: adding each lane's p75 assumes every
        // lane goes badly at once, which lands well above the true p75 of the sum.
        var (timeP25, timeMedian, timeP75) = BootstrapInt(timeDraws);

        return new TaskForecastResult(
            null, null, null,
            timeP25, timeMedian, timeP75,
            new ForecastAssumptions(
                laneAssumptions,
                samples.BucketLevel,
                laneAssumptions.Min(l => l.SampleCount),
                CostForecastSuppressed: true));
    }

    /// <summary>
    /// The lanes this task will actually pass through. Every one of these is knowable before the
    /// task runs, which is why they are inputs rather than predictions.
    /// </summary>
    private static List<Lane> PlannedLanes(TaskForecastInput input, bool qaEnabled)
    {
        List<Lane> lanes = [];
        if (!input.SkipPmReview) lanes.Add(Lane.Ready);
        if (!input.SkipPlanning) lanes.Add(Lane.Planning);
        lanes.Add(Lane.Developing);
        if (qaEnabled && !input.SkipTesting) lanes.Add(Lane.Testing);
        if (!input.SkipCodeReview) lanes.Add(Lane.CodeReview);
        return lanes;
    }

    /// <summary>
    /// Which model each lane will run on. The per-task override applies only to the dev lanes —
    /// PM, QA and Code Review always keep their own agents' models, same rule the orchestrator
    /// enforces at run time.
    /// </summary>
    private static string ResolveModel(
        Lane lane, TaskForecastInput input, IReadOnlyList<AgentSummary> agents)
    {
        if (lane is Lane.Planning or Lane.Developing && !string.IsNullOrWhiteSpace(input.Model))
            return input.Model!;

        var role = lane switch
        {
            Lane.Ready => AgentRole.PM,
            Lane.Testing => AgentRole.QA,
            Lane.CodeReview => AgentRole.CodeReviewer,
            _ => (AgentRole?)null,
        };

        if (role is { } r)
            return agents.FirstOrDefault(a => a.Role == r)?.Model ?? UnknownModel;

        // Dev lanes: the pinned agent if there is one, else any dev-role agent on the project.
        if (input.AssignedAgentId is { } assignedId)
        {
            var pinned = agents.FirstOrDefault(a => a.Id == assignedId);
            if (pinned is not null) return pinned.Model;
        }

        // Same pick the orchestrator's AgentAssigner makes, so the lane is priced on the
        // agent that will actually run it (repo scope is unknown for a draft: null).
        return DevAgentSelection.FindDevAgent(agents, repoId: null)?.Model ?? UnknownModel;
    }

    // Reported as the lane's model when no agent can be resolved for it.
    private const string UnknownModel = "unknown";

    /// <summary>
    /// Loads per-lane duration samples, falling back project → org → platform. A scope
    /// is used only when EVERY planned lane clears the sample floor in it; a project with three
    /// finished tasks is not "some history", it is "not enough history", and the estimate comes
    /// from the next scope out rather than being suppressed. Returns null only when even the
    /// platform lacks the floor.
    /// </summary>
    private async Task<LaneSamples?> LoadSamplesAsync(
        TaskForecastInput input, IReadOnlyList<Lane> lanes, CancellationToken ct)
    {
        var project = await LoadSamplesForScopeAsync(input, SampleScope.Project, ct);
        if (project is not null && MeetsFloor(project, lanes))
            return project;

        var org = await LoadSamplesForScopeAsync(input, SampleScope.Org, ct);
        if (org is not null && MeetsFloor(org, lanes))
            return org;

        // Platform-wide: the one deliberate cross-tenant read in the forecast path. A new org's
        // first task must still show a number (user rule, 2026-09-04: "a clear cost estimate for
        // every task"), and what crosses the tenant line is durations keyed by execution id —
        // no titles, no content, no org, no cost. The label on the
        // result says "platform" so the UI can say whose history it is.
        var platform = await LoadSamplesForScopeAsync(input, SampleScope.Platform, ct);
        return platform is not null && MeetsFloor(platform, lanes) ? platform : null;
    }

    /// <summary>The floor: <see cref="MinSamplesPerLane"/> samples in every planned lane.</summary>
    private static bool MeetsFloor(LaneSamples samples, IReadOnlyList<Lane> lanes)
        => lanes.All(lane => samples.For(lane).Count >= MinSamplesPerLane);

    private enum SampleScope { Project, Org, Platform }

    private async Task<LaneSamples?> LoadSamplesForScopeAsync(
        TaskForecastInput input, SampleScope scope, CancellationToken ct)
    {
        // Sampled from tasks of the same type: a BugFix and a Feature are different animals, and
        // the type is free to key on.
        var sourceTasks = (scope == SampleScope.Platform
                ? db.Tasks.AsNoTracking().IgnoreQueryFilters()
                : db.Tasks.AsNoTracking().Where(t => t.OrgId == input.OrgId))
            .Where(t => t.Status == Contracts.TaskStatus.Completed && t.Type == input.Type);

        if (input.ExcludeTaskId is { } excludeId)
            sourceTasks = sourceTasks.Where(t => t.Id != excludeId);

        if (scope == SampleScope.Project)
            sourceTasks = sourceTasks.Where(t => t.ProjectId == input.ProjectId);

        var taskIds = await sourceTasks
            .OrderByDescending(t => t.CompletedAt)
            .Take(SampleTaskLimit)
            .Select(t => new { t.Id, t.ProjectId })
            .ToListAsync(ct);

        if (taskIds.Count == 0)
            return null;

        // A List, never a Ulid[]: an array parameter needs an array converter EF has not been
        // able to build for Ulid, while a list expands inline. Every other Ulid `Contains` in
        // this codebase is a List for the same reason.
        var ids = taskIds.Select(t => t.Id).ToList();

        // Only executions that actually ran. A Queued row has no StartedAt and would otherwise
        // contribute a zero-second sample.
        var executions = await (scope == SampleScope.Platform
                ? db.TaskExecutions.AsNoTracking().IgnoreQueryFilters()
                : db.TaskExecutions.AsNoTracking().Where(e => e.OrgId == input.OrgId))
            .Where(e => ids.Contains(e.TaskId) && e.StartedAt != null && e.CompletedAt != null)
            .Select(e => new { e.Id, e.TaskId, e.Lane, e.StartedAt, e.CompletedAt })
            .ToListAsync(ct);

        if (executions.Count == 0)
            return null;

        var byLane = new Dictionary<Lane, List<ExecutionSample>>();

        // Grouped per (task, lane) first: a lane that ran three times because review bounced it
        // back is ONE sample of "how long this lane takes on a task", which is how the
        // review-cycle rate gets into the estimate without being modelled separately.
        foreach (var group in executions.GroupBy(e => new { e.TaskId, e.Lane }))
        {
            var seconds = (int)group.Sum(e => (e.CompletedAt!.Value - e.StartedAt!.Value).TotalSeconds);

            (byLane.TryGetValue(group.Key.Lane, out var list) ? list : byLane[group.Key.Lane] = [])
                .Add(new ExecutionSample(Math.Max(seconds, 0)));
        }

        return new LaneSamples(byLane, scope switch
        {
            SampleScope.Project => "project",
            SampleScope.Org => "org",
            _ => "platform",
        });
    }

    /// <summary>
    /// Draws one sample per lane from that lane's empirical distribution, sums, and repeats —
    /// so the reported band is a quantile of the total rather than a total of quantiles.
    /// Deterministic seed: two callers looking at the same task should see the same number.
    /// </summary>
    private static (int P25, int Median, int P75) BootstrapInt(List<List<int>> perLane)
    {
        var rng = new Random(Seed);
        var totals = new List<int>(BootstrapIterations);

        for (var i = 0; i < BootstrapIterations; i++)
        {
            var total = 0;
            foreach (var lane in perLane)
                total += lane[rng.Next(lane.Count)];
            totals.Add(total);
        }

        totals.Sort();
        return (Quantile(totals, 0.25), Quantile(totals, 0.50), Quantile(totals, 0.75));
    }

    private const int Seed = 20260730;

    private static T Quantile<T>(List<T> sorted, double q)
    {
        var index = (int)Math.Clamp(Math.Round(q * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[index];
    }

    private sealed record ExecutionSample(int AgentSeconds);

    private sealed class LaneSamples(Dictionary<Lane, List<ExecutionSample>> byLane, string bucketLevel)
    {
        public string BucketLevel => bucketLevel;
        public bool HasAny => byLane.Count > 0;

        public IReadOnlyList<ExecutionSample> For(Lane lane) =>
            byLane.TryGetValue(lane, out var list) ? list : [];
    }
}
