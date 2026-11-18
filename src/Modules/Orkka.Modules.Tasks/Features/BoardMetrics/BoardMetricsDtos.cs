namespace Orkka.Modules.Tasks.Features.BoardMetrics;

public sealed record BoardMetricsRequest
{
    /// <summary>Lookback window: <c>24h</c>, <c>7d</c> (default) or <c>30d</c>. Same vocabulary
    /// as agent-stats, resolved by <see cref="StatsWindow"/>.</summary>
    public string? Window { get; init; }
}

/// <summary>
/// The board strip's headline numbers. Deliberately wider than the four cells the UI renders:
/// every field falls out of the same in-memory fold, so letting the client choose which four to
/// show costs nothing here, while hard-coding four would make a UI tweak an API change.
/// </summary>
/// <param name="Shipped">Tasks merged in the window, anchored on <c>Task.CompletedAt</c> — there
/// is no MergedAt column, so a task that completed just before a boundary and merged just after
/// lands in the earlier window.</param>
/// <param name="AgentSecondsPerShippedTask">Agent seconds over each shipped task's whole life, not
/// just this window: a task that shipped today cost what it cost. This is the one figure that does
/// not describe the window the others describe.</param>
/// <param name="AgentSpend">Spend on agent runs only. Usage rows with no execution — chat, design,
/// briefs — are excluded by <c>IUsageQuery</c>, so this is legitimately lower than the billing
/// page's project total and must be labelled as agent spend.</param>
/// <param name="Tokens">Present because cost is structurally zero on Orkka Local and for any org
/// with an attached subscription; the client swaps the money cell for this one when it is.</param>
/// <param name="FailureRate">Over <em>finished</em> runs, never over <see cref="Runs"/> — counting
/// in-flight runs makes the rate dip every time someone opens the board mid-run.</param>
public sealed record BoardMetricsStrip(
    int Shipped,
    double? AgentSecondsPerShippedTask,
    double? AvgRunSeconds,
    decimal AgentSpend,
    decimal WastedSpend,
    long Tokens,
    int Runs,
    int Completed,
    int Failed,
    int Cancelled,
    int Running,
    int TasksTouched,
    double? FailureRate)
{
    public static BoardMetricsStrip Empty { get; } =
        new(0, null, null, 0m, 0m, 0, 0, 0, 0, 0, 0, 0, null);
}

/// <summary>
/// A metric's status pill, derived from the previous window of equal length rather than from a
/// targets table nobody has configured.
/// </summary>
/// <param name="Trend">The API decides direction, because the API is what knows a falling failure
/// rate is good and a falling ship count is not. The client only picks a colour.</param>
public sealed record MetricDelta(double? Previous, double? ChangePct, string Trend)
{
    public static MetricDelta Unknown { get; } = new(null, null, TrendUnknown);

    public const string TrendImproving = "Improving";
    public const string TrendWorsening = "Worsening";
    public const string TrendFlat = "Flat";
    public const string TrendUnknown = "Unknown";
}

/// <summary>Spend carries a previous value but never a verdict — spending more because you shipped
/// more is not a breach, and colouring it red teaches the wrong lesson.</summary>
public sealed record BoardMetricsDeltas(
    MetricDelta Shipped,
    MetricDelta AgentSecondsPerShippedTask,
    MetricDelta AvgRunSeconds,
    MetricDelta AgentSpend,
    MetricDelta WastedSpend,
    MetricDelta FailureRate);

/// <summary>
/// What one lane cost over the window. NOT dwell time — there is no lane-transition history and
/// adding one is a deliberate non-goal, so every duration here is the sum of execution spans
/// stamped with this lane. A lane a human owns never receives an execution and is therefore simply
/// absent from the map.
///
/// Deliberately the same shape as <see cref="BoardMetricsStrip"/> scoped to one column, so a lane
/// header can render the board's own four figures without the client folding anything.
/// </summary>
/// <param name="AvgAgentSeconds">Averaged over (task, lane) groups, not over runs: a lane that ran
/// three times because review bounced the task back is one experience of that lane.</param>
/// <param name="Runs">Finished runs behind the average — the denominator is never hidden.</param>
/// <param name="TotalAgentSeconds">Every span in the lane added up. The header shows the average
/// per task; the card shows what the column actually burned.</param>
/// <param name="Tokens">Billed tokens with the cache split intact, summed over this lane's runs.
/// Windowed on when a run <em>started</em>, unlike <see cref="BoardMetricsStrip.Tokens"/> which is
/// windowed on when usage was billed — the two can disagree by one straddling run.</param>
/// <param name="FailureRate">Over this lane's finished runs, 0..1, or null when none finished.
/// Null rather than zero: a lane with one run still going has no rate, and printing 0% would
/// claim it is clean.</param>
/// <param name="FinishedRuns">The failure rate's denominator.</param>
public sealed record BoardLaneMetric(
    double AvgAgentSeconds,
    int Runs,
    int Tasks,
    double TotalAgentSeconds,
    long Tokens,
    double? FailureRate,
    int FinishedRuns);

public sealed record BoardMetricsResponse(
    string Window,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset PreviousFrom,
    BoardMetricsStrip Current,
    BoardMetricsDeltas Deltas,
    IReadOnlyDictionary<string, BoardLaneMetric> Lanes);
