namespace Orkka.Modules.Tasks.Contracts;

/// <summary>
/// A range, never a point. Token usage is right-skewed and one runaway task drags a mean
/// somewhere no task has ever landed, so the median leads and the band comes from a bootstrap
/// over the empirical distribution — not from summing each lane's p75, which would assume every
/// lane goes badly at once.
/// </summary>
/// <param name="CostP25">Null when there are no cost samples: a runner-executed task (billed at
/// $0 against the user's own subscription) or a host that does not bill at all. The agent-time
/// estimate still stands.</param>
/// <param name="AgentSecondsMedian">Agent time — the sum of execution spans. Explicitly NOT
/// wall-clock-to-done, which is dominated by how fast the human replies.</param>
public sealed record TaskForecastResult(
    decimal? CostP25,
    decimal? CostMedian,
    decimal? CostP75,
    int AgentSecondsP25,
    int AgentSecondsMedian,
    int AgentSecondsP75,
    ForecastAssumptions Assumptions);

/// <param name="Lanes">Lanes assumed to run, with the model each was priced at.</param>
/// <param name="BucketLevel">"project" | "org" | "platform" — which fallback produced the
/// samples. "platform" is other tenants' token counts and durations (never content), so a
/// brand-new org still sees a number on its first task.</param>
/// <param name="MinLaneSamples">Smallest sample count behind any contributing lane.</param>
public sealed record ForecastAssumptions(
    IReadOnlyList<ForecastLaneAssumption> Lanes,
    string BucketLevel,
    int MinLaneSamples,
    bool CostForecastSuppressed);

public sealed record ForecastLaneAssumption(string Lane, string Model, int SampleCount);

/// <summary>
/// Everything the estimator needs about a task — all of it knowable before the task runs, and
/// all of it settable on a form. That is what lets the create screen re-estimate as the user
/// switches models or toggles a lane off, which a whole-task historical average could not do.
/// </summary>
public sealed record TaskForecastInput(
    Ulid OrgId,
    Ulid ProjectId,
    TaskType Type,
    /// <summary>Per-task model override. Honored only for the dev lanes, matching the rule the
    /// orchestrator applies at run time.</summary>
    string? Model,
    Ulid? AssignedAgentId,
    bool SkipPmReview,
    bool SkipPlanning,
    bool SkipTesting,
    bool SkipCodeReview,
    /// <summary>Excluded from its own sample set. Null for a task that doesn't exist yet.</summary>
    Ulid? ExcludeTaskId = null);

public interface ITaskForecaster
{
    /// <summary>
    /// Forecasts an existing task, or returns null when there isn't enough history.
    ///
    /// Null is a first-class answer: below the sample floor in any contributing lane the whole
    /// forecast is suppressed rather than partially summed, because a partial sum reads as a
    /// full one. Every new org starts here.
    /// </summary>
    Task<TaskForecastResult?> ForecastAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>Forecasts a task that doesn't exist yet — the create form's estimate.</summary>
    Task<TaskForecastResult?> ForecastDraftAsync(
        TaskForecastInput input, CancellationToken ct = default);
}
