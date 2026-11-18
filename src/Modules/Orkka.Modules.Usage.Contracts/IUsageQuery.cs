namespace Orkka.Modules.Usage.Contracts;

/// <summary>
/// The read side of usage. <see cref="IUsageRecorder"/> writes what work cost; this reads it back
/// for modules that need to display or forecast spend without owning the pricing rules.
///
/// Cost lives here on purpose. It depends on the per-project markup rate, admin-managed model
/// rates, and the subscription-auth rule that zeroes a user's own runs — none of which the Tasks
/// module knows. Copying a cost figure into another schema produces two numbers that drift.
/// </summary>
public interface IUsageQuery
{
    /// <summary>
    /// Estimated list-price spend per task, in US dollars: each usage row priced at the cost the
    /// vendor's CLI reported for it (<c>VendorReportedCostUsd</c>), else at the model's list
    /// price for its tokens (the model catalog, <c>CostCalculator</c> as the fallback). Orkka
    /// bills nothing and records runner work at <c>CostTotal = 0</c>, so this — not
    /// <see cref="GetTaskCostsAsync"/> — is what the task spend limit and the autopilot cap
    /// measure: close to the real charge on an API key or Bedrock, an API-equivalent estimate on
    /// a subscription. Tasks with no usage rows are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Ulid, decimal>> GetTaskEstimatedSpendAsync(
        Ulid orgId, Ulid projectId, IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default);

    /// <summary>Total recorded cost per task (<c>CostTotal</c>: 0 for every run on the
    /// developer's runner, since Orkka bills nothing). Tasks with no usage rows are absent from
    /// the result rather than present with zero.</summary>
    /// <param name="projectId">Scopes the SQL filter to an indexed, non-nullable column. The
    /// id match happens in memory afterwards: <c>TaskId</c>/<c>ExecutionId</c> are nullable,
    /// converter-mapped Ulid columns, never matched with a <c>Contains</c>. Grouping first keeps the materialized set to one row per task/execution.</param>
    Task<IReadOnlyDictionary<Ulid, decimal>> GetTaskCostsAsync(
        Ulid orgId, Ulid projectId, IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default);

    /// <summary>Total cost per execution. Same semantics as <see cref="GetTaskCostsAsync"/>.</summary>
    Task<IReadOnlyDictionary<Ulid, decimal>> GetExecutionCostsAsync(
        Ulid orgId, Ulid projectId, IReadOnlyCollection<Ulid> executionIds, CancellationToken ct = default);

    /// <summary>
    /// Token counts for the given executions, with the cache split intact — the input the
    /// forecaster samples from.
    ///
    /// The caller chooses the executions because the caller owns <c>TaskExecutions</c> and
    /// therefore knows which are recent, completed, and in which lane. Billing has no business
    /// re-deriving that.
    /// </summary>
    Task<IReadOnlyList<ExecutionTokenSample>> GetExecutionTokenSamplesAsync(
        Ulid orgId, Ulid projectId, IReadOnlyCollection<Ulid> executionIds, CancellationToken ct = default);

    /// <summary>
    /// Same as above over several of the org's projects. The forecaster's org-wide fallback
    /// samples tasks from every project in the org; filtering their usage rows by the one
    /// project being forecast returned nothing for all of them, which read as "these tasks
    /// cost 0" and forecast a first milestone at zero.
    /// </summary>
    Task<IReadOnlyList<ExecutionTokenSample>> GetExecutionTokenSamplesAsync(
        Ulid orgId, IReadOnlyCollection<Ulid> projectIds, IReadOnlyCollection<Ulid> executionIds,
        CancellationToken ct = default);

    /// <summary>
    /// The same token samples for executions that may belong to ANY org — the forecaster's
    /// platform-wide fallback, so a brand-new org sees an estimate on its first task instead of
    /// "not enough history". This is the one deliberate cross-tenant read on the usage table:
    /// what leaves it is token counts keyed by execution id, no content, no org, no cost.
    /// </summary>
    /// <param name="projectIds">The projects the executions belong to — the SQL filter, on a
    /// non-nullable indexed column, for the same reason the org-scoped variant filters on
    /// its project. The execution-id match happens in memory afterwards.</param>
    Task<IReadOnlyList<ExecutionTokenSample>> GetExecutionTokenSamplesAcrossOrgsAsync(
        IReadOnlyCollection<Ulid> projectIds, IReadOnlyCollection<Ulid> executionIds,
        CancellationToken ct = default);

    /// <summary>
    /// Lifetime token totals per agent, with the cache split intact.
    ///
    /// <c>ProjectAgent</c> keeps its own denormalized <c>TotalTokensIn</c>/<c>TotalTokensOut</c>
    /// counters, incremented per run. Those predate cache accounting and have no bucket to put
    /// cached tokens in, so they under-report by roughly the same margin the execution card did.
    /// Rather than add two more counters that could drift from the usage rows, the read side
    /// derives from the rows the invoice is built from.
    ///
    /// Agents absent from the result have no usage rows; callers should fall back to the stored
    /// counters rather than displaying zero.
    /// </summary>
    Task<IReadOnlyDictionary<Ulid, AgentTokenTotals>> GetAgentTokenTotalsAsync(
        Ulid orgId, Ulid projectId, IReadOnlyCollection<Ulid> agentIds, CancellationToken ct = default);

    /// <summary>
    /// What a project's agent runs cost inside a time window, in one aggregate.
    ///
    /// The other reads here take an explicit id list, which is fine when the caller is already
    /// holding the rows. The Activity page is not: it caps the executions it returns, so summing
    /// the costs of the ids it happens to have shows a smaller number than was actually spent.
    /// This one filters on the window instead, so the headline stays true no matter how many rows
    /// the caller chose to display.
    ///
    /// Two deliberate narrowings. Rows with no <c>ExecutionId</c> are excluded — chat, design and
    /// brief runs spend against the same project but are not agent runs and would inflate the
    /// figure past anything the caller can show. And the window is applied to when usage was
    /// recorded, not to when its run started, so a run straddling the boundary lands in the window
    /// it was billed in; over a 24h-or-longer window the difference is a rounding error, and
    /// "what did this project spend today" is the question being answered.
    /// </summary>
    Task<ProjectSpendWindow> GetProjectSpendAsync(
        Ulid orgId, Ulid projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

/// <summary>
/// A project's agent-run spend over a window, plus the same figures per agent.
///
/// Tokens travel alongside cost because they are the only signal with any content on Orkka Local,
/// where every cost is zero by design (runs go through the user's own subscription).
/// </summary>
public sealed record ProjectSpendWindow(
    decimal TotalCost,
    long TotalTokens,
    IReadOnlyDictionary<Ulid, AgentSpend> ByAgent)
{
    public static readonly ProjectSpendWindow Empty =
        new(0m, 0, new Dictionary<Ulid, AgentSpend>());
}

/// <summary>One agent's share of <see cref="ProjectSpendWindow"/>.</summary>
public sealed record AgentSpend(decimal Cost, long Tokens);

/// <summary>One agent's lifetime token usage within a project, split by billing bucket.</summary>
public sealed record AgentTokenTotals(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens)
{
    public long TotalTokens =>
        InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
}

/// <summary>
/// One execution's token usage under one model. An execution that called more than one model
/// yields more than one row — the forecaster sums tokens per execution and prices them with the
/// model the *next* run will use, so <paramref name="Model"/> here is diagnostic rather than an
/// input to the estimate.
/// </summary>
/// <remarks>
/// The cache split matters. <c>TaskExecution</c> only carries Prompt/Completion totals, which
/// price cache reads as full input — and <c>TaskExecution.SessionId</c> exists precisely so later
/// lanes resume a session, making cached tokens a large share of a real task. Sampling from here
/// instead of from the execution row is what keeps forecasts from running high.
/// </remarks>
public sealed record ExecutionTokenSample(
    Ulid ExecutionId,
    string Model,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens);
