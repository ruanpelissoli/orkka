namespace Orkka.Modules.Tasks.Domain;

using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Domain;

/// <summary>
/// A forecast as it was shown to a user, plus what actually happened.
///
/// Its own table rather than columns on <see cref="TaskEntity"/>: that row is already heavy
/// enough that the Attention Inbox projects around it, a forecast is written once and never read
/// on a hot path, and a task can carry more than one — which is the interesting comparison
/// (does the estimate improve once an approved plan exists?).
///
/// Without this table there is no way to tell a working estimator from a plausible-looking one,
/// and a number users learn to distrust is worse than no number.
/// </summary>
public sealed class TaskForecast : TenantEntity
{
    public Ulid TaskId { get; set; }
    public Ulid ProjectId { get; set; }
    public ForecastSurface Surface { get; set; }

    /// <summary>Null on runner-executed tasks — those bill at $0 by construction, so a cost
    /// forecast would be a made-up number. The agent-time estimate still applies.</summary>
    public decimal? CostP25 { get; set; }
    public decimal? CostMedian { get; set; }
    public decimal? CostP75 { get; set; }

    /// <summary>Agent time — the sum of execution spans, never wall-clock-to-done, which is
    /// mostly a measure of how fast the human replies.</summary>
    public int AgentSecondsP25 { get; set; }
    public int AgentSecondsMedian { get; set; }
    public int AgentSecondsP75 { get; set; }

    /// <summary>
    /// What the forecast assumed: lanes, model per lane, which fallback bucket was used, and
    /// the sample count behind each lane. Recording this is what makes a miss diagnosable — a
    /// forecast that assumed Testing would run, against a task the user then set SkipTesting
    /// on, is a stale forecast rather than estimator error, and scoring the two together
    /// silently poisons the metric.
    /// </summary>
    public string AssumptionsJson { get; set; } = "{}";

    public decimal? ActualCost { get; set; }
    public int? ActualAgentSeconds { get; set; }
    public string? ActualLanesJson { get; set; }
    public ForecastOutcome Outcome { get; set; } = ForecastOutcome.Pending;
    public DateTimeOffset? ScoredAt { get; set; }
}
