namespace Orkka.Modules.Tasks.Features.Executions;

public sealed record ListProjectExecutionsRequest
{
    /// <summary>Inclusive lower bound on <c>StartedAt</c>. Defaults to 24 hours ago.</summary>
    public DateTimeOffset? From { get; init; }
    /// <summary>Exclusive upper bound on <c>StartedAt</c>. Defaults to now.</summary>
    public DateTimeOffset? To { get; init; }
    /// <summary>Optional <c>ExecutionStatus</c> name filter (Running, Completed, Failed, Cancelled).</summary>
    public string? Status { get; init; }
    public Ulid? AgentId { get; init; }
    /// <summary>Max rows, clamped to 1..500. Default 300.</summary>
    public int? Limit { get; init; }
}

/// <summary>
/// One bar on the project Activity timeline. Executions that never started (Queued) are
/// excluded — a bar needs a left edge; a still-running execution has no right edge and the
/// client draws it to "now".
/// </summary>
public sealed record ProjectExecutionRow(
    Ulid ExecutionId,
    Ulid TaskId,
    string TaskKey,
    string TaskTitle,
    Ulid? AgentId,
    string? AgentName,
    string? AgentRole,
    string Lane,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    double? DurationSeconds,
    int TokensUsed,
    decimal Cost,
    string? ErrorMessage);

public sealed record ProjectExecutionsResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    bool Truncated,
    IReadOnlyList<ProjectExecutionRow> Executions,
    ActivitySummary Summary,
    IReadOnlyList<ActivityTaskRow> Tasks);

/// <summary>
/// The window's totals, counted over every execution in it rather than over the page of
/// <see cref="ProjectExecutionRow"/>s returned beside it. That distinction is the whole point:
/// once <c>Truncated</c> is true, anything the client adds up from the rows understates the truth,
/// and a headline that quietly shrinks as a project gets busier is worse than no headline.
///
/// The status filter is deliberately *not* applied here. A status breakdown filtered to one status
/// is a tautology, and the client's reason for showing this strip is to say "3 of these failed"
/// while you are looking at something else. The agent filter is applied, because narrowing to one
/// agent is a question about that agent.
/// </summary>
/// <param name="WastedCost">Spend on runs that failed or were cancelled — money that bought
/// nothing. Counted exactly, from the ids of those runs, rather than from the returned page.</param>
public sealed record ActivitySummary(
    int Runs,
    int Running,
    int Completed,
    int Failed,
    int Cancelled,
    int TasksTouched,
    double DurationSeconds,
    decimal Cost,
    decimal WastedCost,
    long Tokens,
    IReadOnlyList<ActivityAgentSpend> Agents)
{
    public static ActivitySummary Empty { get; } =
        new(0, 0, 0, 0, 0, 0, 0, 0m, 0m, 0, []);
}

/// <summary>
/// One agent's spend in the window. Run counts are left to the client, which derives them from the
/// execution rows it already has so that narrowing the range on the timeline restates them without
/// a round trip; cost and tokens have to come from here because the Usage module owns them.
/// </summary>
public sealed record ActivityAgentSpend(Ulid AgentId, decimal Cost, long Tokens);

/// <summary>
/// The state a task ended the window in — what the runs actually amounted to.
///
/// The executions say what happened; these say whether it worked. Every field is read straight off
/// <c>TaskEntity</c>, so this costs one extra query and no new write-path plumbing. Lane movement
/// is not here on purpose: the client reconstructs it from the <c>Lane</c> on each execution, which
/// is enough for the progression chips and avoids a history table nothing else needs yet.
/// </summary>
public sealed record ActivityTaskRow(
    Ulid TaskId,
    string TaskKey,
    string TaskTitle,
    string CurrentLane,
    string Status,
    string? PrUrl,
    bool IsMerged,
    DateTimeOffset? PrClosedAt,
    string? MergeFailureReason,
    string? MergeFailureKind,
    string? SetupFailure,
    int ReviewCycleCount,
    int QaCycleCount,
    int CheckCycleCount,
    int BaseSyncCycleCount,
    bool HasAgentWorking,
    DateTimeOffset LastActivityAt);
