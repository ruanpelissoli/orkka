namespace Orkka.Modules.Tasks.Features.BoardMetrics;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Usage.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;

/// <summary>
/// The numbers above the board: what shipped, what it cost, how much of that bought nothing, and
/// how long an agent spends in each lane. Read-only, open to every org member (the board is).
///
/// Every duration here is <em>agent time</em> — the sum of execution spans. It is not how long a
/// card sat in a column: there is no lane-transition history, deliberately, so dwell time is not a
/// number this platform has. Presenting agent time as dwell time would mostly measure how fast the
/// human replied, which is the confusion <c>TaskForecast</c> already warns about.
/// </summary>
public sealed class GetBoardMetricsEndpoint(
    TasksDbContext db,
    IProjectsApi projectsApi,
    IUsageQuery usageQuery,
    ICurrentUser currentUser,
    IProjectResolver projectResolver)
    : Endpoint<BoardMetricsRequest, BoardMetricsResponse>
{
    /// <summary>Below this many finished runs in the previous window there is no trend. One run
    /// flipping a 24h window from 0% to 100% is noise, and a red pill on noise trains people to
    /// ignore pills — the same instinct behind the forecaster's minimum sample count.</summary>
    private const int MinSamplesForDelta = 3;

    /// <summary>Inside this band the trend reads Flat, so an ordinary reload is not coloured.</summary>
    private const double FlatBandPct = 10d;

    public override void Configure()
    {
        Get("/api/projects/{projectId}/board-metrics");
    }

    public override async Task HandleAsync(BoardMetricsRequest req, CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // The resolver lets a well-formed ULID through on access alone; confirm it exists in this
        // org so an unknown id reads as 404 rather than an empty strip.
        var project = await projectsApi.GetProjectSummaryAsync(projectId, ct);
        if (project is null || project.OrgId != currentUser.OrgId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var (window, span) = StatsWindow.Parse(req.Window);
        var to = DateTimeOffset.UtcNow;
        var from = to - span;
        var previousFrom = from - span;

        // One query for both windows rather than two. Both are range scans over the same index, so
        // doubling the range roughly doubles the rows for a single plan instead of paying for two.
        var executions = await db.TaskExecutions.AsNoTracking()
            .Where(e => e.OrgId == currentUser.OrgId
                        && e.Task.ProjectId == projectId
                        && e.StartedAt != null
                        && e.StartedAt >= previousFrom
                        && e.StartedAt < to)
            .Select(e => new ExecutionRow(
                e.Id,
                e.TaskId,
                e.Lane,
                e.Status,
                e.StartedAt!.Value,
                e.CompletedAt,
                e.Task.InteractiveSessionStartedAt != null))
            .ToListAsync(ct);

        var currentRuns = executions.Where(r => r.StartedAt >= from).ToList();
        var previousRuns = executions.Where(r => r.StartedAt < from).ToList();

        // Shipped is anchored on the task's own completion, not on an execution: the merge is the
        // outcome, and there is no MergedAt column to anchor on instead.
        var shipped = await db.Tasks.AsNoTracking()
            .Where(t => t.OrgId == currentUser.OrgId
                        && t.ProjectId == projectId
                        && t.IsMerged
                        && t.CompletedAt != null
                        && t.CompletedAt >= previousFrom
                        && t.CompletedAt < to)
            .Select(t => new { t.Id, CompletedAt = t.CompletedAt!.Value })
            .ToListAsync(ct);

        var currentShipped = shipped.Where(t => t.CompletedAt >= from).Select(t => t.Id).ToList();
        var previousShipped = shipped.Where(t => t.CompletedAt < from).Select(t => t.Id).ToList();

        // Agent seconds over the shipped tasks' whole lives, not just this window — a task that
        // shipped today cost what it cost, however long ago it started.
        var shippedIds = shipped.Select(t => t.Id).ToList();
        var shippedSpans = shippedIds.Count == 0
            ? []
            : await db.TaskExecutions.AsNoTracking()
                .Where(e => e.OrgId == currentUser.OrgId
                            && shippedIds.Contains(e.TaskId)
                            && e.StartedAt != null
                            && e.CompletedAt != null)
                .Select(e => new { e.TaskId, StartedAt = e.StartedAt!.Value, CompletedAt = e.CompletedAt!.Value })
                .ToListAsync(ct);
        // The span is computed here: SQLite has no date arithmetic over the stored ticks.

        var secondsByTask = shippedSpans
            .GroupBy(x => x.TaskId)
            .ToDictionary(g => g.Key, g => g.Sum(x => (x.CompletedAt - x.StartedAt).TotalSeconds));

        // Failures are the minority of a healthy window, so pricing them by id stays bounded and
        // gives an exact figure. Both windows' ids go in one call and are bucketed afterwards.
        var wastedIds = executions.Where(r => ExecutionOutcome.IsWasted(r.Status)).Select(r => r.Id).ToList();
        var wastedCosts = wastedIds.Count == 0
            ? new Dictionary<Ulid, decimal>()
            : (await usageQuery.GetExecutionCostsAsync(currentUser.OrgId, projectId, wastedIds, ct))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

        // Per-lane token totals. Same query shape as the costs above — it groups the project's
        // usage rows in SQL and matches ids in memory — so asking for every run in the window
        // costs no more than asking for the failed ones did.
        var currentIds = currentRuns.Select(r => r.Id).ToList();
        var tokensByExecution = currentIds.Count == 0
            ? new Dictionary<Ulid, long>()
            : (await usageQuery.GetExecutionTokenSamplesAsync(currentUser.OrgId, projectId, currentIds, ct))
                .GroupBy(sample => sample.ExecutionId)
                .ToDictionary(
                    g => g.Key,
                    // One row per model, so an execution that called two models has two rows.
                    g => g.Sum(sample => (long)sample.InputTokens + sample.OutputTokens
                                         + sample.CacheReadTokens + sample.CacheWriteTokens));

        var currentSpend = await usageQuery.GetProjectSpendAsync(currentUser.OrgId, projectId, from, to, ct);
        var previousSpend = await usageQuery.GetProjectSpendAsync(currentUser.OrgId, projectId, previousFrom, from, ct);

        var current = Fold(currentRuns, currentShipped, secondsByTask, currentSpend, wastedCosts);
        var previous = Fold(previousRuns, previousShipped, secondsByTask, previousSpend, wastedCosts);
        var previousFinished = previousRuns.Count(r => ExecutionOutcome.IsFinished(r.Status));

        // Each metric is gated on its own denominator, not on one shared run count: "you shipped 5,
        // up from 2" is noisy because *two* is a thin baseline, not because few runs happened.
        var deltas = new BoardMetricsDeltas(
            Shipped: Delta(current.Shipped, previous.Shipped, higherIsBetter: true, previous.Shipped),
            AgentSecondsPerShippedTask: Delta(current.AgentSecondsPerShippedTask, previous.AgentSecondsPerShippedTask, false, previous.Shipped),
            AvgRunSeconds: Delta(current.AvgRunSeconds, previous.AvgRunSeconds, false, previousFinished),
            // No verdict on spend: spending more because you shipped more is not a breach. The
            // previous figure still travels so the client can print "vs $12.40" without a colour.
            AgentSpend: new MetricDelta((double)previous.AgentSpend, null, MetricDelta.TrendUnknown),
            WastedSpend: Delta((double)current.WastedSpend, (double)previous.WastedSpend, false, previousFinished),
            FailureRate: Delta(current.FailureRate, previous.FailureRate, false, previousFinished));

        await Send.OkAsync(
            new BoardMetricsResponse(
                window, from, to, previousFrom, current, deltas,
                BuildLanes(currentRuns, tokensByExecution)),
            ct);
    }

    /// <summary>
    /// What each lane cost, keyed on the board's own lane keys so a column header can index
    /// straight into it.
    ///
    /// The average is grouped by (task, lane) before averaging: a lane that ran three times because
    /// review bounced the task back is one experience of that lane, not three. Interactive sessions
    /// are excluded throughout because they stamp <c>Developing</c> on hours of human-driven work —
    /// that is neither agent time nor an agent's failure to report. Human-owned lanes need no
    /// filter: they never receive an execution, so they are simply absent, and a whitelist would
    /// silently swallow a lane added later.
    ///
    /// Tokens and the failure rate take wider slices than the average does — every run in the lane
    /// rather than only the ones with a completion stamp — because a run that burned tokens and
    /// then failed is exactly what those two figures exist to surface.
    /// </summary>
    private static IReadOnlyDictionary<string, BoardLaneMetric> BuildLanes(
        List<ExecutionRow> runs,
        Dictionary<Ulid, long> tokensByExecution) =>
        runs
            .Where(r => !r.IsInteractive)
            .GroupBy(r => r.Lane)
            .ToDictionary(
                g => LaneKeys.ToKey(g.Key),
                g => BuildLane([.. g], tokensByExecution));

    private static BoardLaneMetric BuildLane(
        List<ExecutionRow> laneRuns,
        Dictionary<Ulid, long> tokensByExecution)
    {
        var spans = laneRuns.Where(r => r.CompletedAt is not null).ToList();

        // Per-task spans, so a bounced task counts once however many times it came back.
        var perTask = spans
            .GroupBy(r => r.TaskId)
            .Select(g => g.Sum(r => (r.CompletedAt!.Value - r.StartedAt).TotalSeconds))
            .ToList();

        var finished = laneRuns.Count(r => ExecutionOutcome.IsFinished(r.Status));
        var wasted = laneRuns.Count(r => ExecutionOutcome.IsWasted(r.Status));

        return new BoardLaneMetric(
            AvgAgentSeconds: perTask.Count == 0 ? 0 : Math.Round(perTask.Average(), 1),
            Runs: spans.Count,
            Tasks: perTask.Count,
            TotalAgentSeconds: Math.Round(perTask.Sum(), 1),
            Tokens: laneRuns.Sum(r => tokensByExecution.GetValueOrDefault(r.Id)),
            FailureRate: finished == 0 ? null : Math.Round((double)wasted / finished, 4),
            FinishedRuns: finished);
    }

    private static BoardMetricsStrip Fold(
        List<ExecutionRow> runs,
        List<Ulid> shippedIds,
        Dictionary<Ulid, double> secondsByTask,
        ProjectSpendWindow spend,
        Dictionary<Ulid, decimal> wastedCosts)
    {
        if (runs.Count == 0 && shippedIds.Count == 0)
            return BoardMetricsStrip.Empty;

        var finished = runs.Where(r => ExecutionOutcome.IsFinished(r.Status)).ToList();
        var completedSpans = runs.Where(r => r.CompletedAt is not null).ToList();
        var wasted = finished.Count(r => ExecutionOutcome.IsWasted(r.Status));

        return new BoardMetricsStrip(
            Shipped: shippedIds.Count,
            AgentSecondsPerShippedTask: shippedIds.Count == 0
                ? null
                : Math.Round(shippedIds.Sum(id => secondsByTask.GetValueOrDefault(id)) / shippedIds.Count, 1),
            AvgRunSeconds: completedSpans.Count == 0
                ? null
                : Math.Round(completedSpans.Average(r => (r.CompletedAt!.Value - r.StartedAt).TotalSeconds), 1),
            AgentSpend: spend.TotalCost,
            WastedSpend: runs.Where(r => ExecutionOutcome.IsWasted(r.Status))
                             .Sum(r => wastedCosts.GetValueOrDefault(r.Id)),
            Tokens: spend.TotalTokens,
            Runs: runs.Count,
            Completed: runs.Count(r => r.Status == ExecutionStatus.Completed),
            Failed: runs.Count(r => r.Status == ExecutionStatus.Failed),
            Cancelled: runs.Count(r => r.Status == ExecutionStatus.Cancelled),
            Running: runs.Count(r => r.Status == ExecutionStatus.Running),
            TasksTouched: runs.Select(r => r.TaskId).Distinct().Count(),
            // Over finished runs only: an in-flight run is not yet a success or a failure, and
            // counting it as neither-but-present makes the rate dip mid-run.
            FailureRate: finished.Count == 0 ? null : Math.Round((double)wasted / finished.Count, 4));
    }

    /// <summary>
    /// Below the sample floor the baseline still travels — the client can say "vs 2" honestly, it
    /// just cannot say "+150%" about it. Withholding the number entirely would make a young project
    /// look like it has no history at all.
    /// </summary>
    private static MetricDelta Delta(double? current, double? previous, bool higherIsBetter, int previousSamples)
    {
        if (previous is not { } p || current is not { } c)
            return MetricDelta.Unknown;

        // Percentage change from zero is not a number, and a baseline this thin cannot carry a
        // verdict. Both cases: keep the figure, drop the percentage and the colour.
        if (p == 0 || previousSamples < MinSamplesForDelta)
            return new MetricDelta(Math.Round(p, 2), null, MetricDelta.TrendUnknown);

        var pct = (c - p) / Math.Abs(p) * 100d;
        var trend = Math.Abs(pct) < FlatBandPct
            ? MetricDelta.TrendFlat
            : pct > 0 == higherIsBetter
                ? MetricDelta.TrendImproving
                : MetricDelta.TrendWorsening;

        return new MetricDelta(Math.Round(p, 2), Math.Round(pct, 1), trend);
    }

    private sealed record ExecutionRow(
        Ulid Id,
        Ulid TaskId,
        Lane Lane,
        ExecutionStatus Status,
        DateTimeOffset StartedAt,
        DateTimeOffset? CompletedAt,
        bool IsInteractive);
}
