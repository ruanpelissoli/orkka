namespace Orkka.Modules.Tasks.Features.Milestones;

public sealed record CreateMilestoneRequest(
    string Title,
    string? Outcome = null,
    int? Position = null,
    /// <summary>Repo the work lands in. Null = spans repos, or a single-repo project.</summary>
    Ulid? RepoId = null);

/// <param name="RepoId">Pass an empty string to clear it back to cross-repo; omit to leave
/// it alone. Same convention the task endpoint uses for milestone reassignment.</param>
public sealed record UpdateMilestoneRequest(
    string? Title = null,
    string? Outcome = null,
    string? Status = null,
    int? Position = null,
    string? RepoId = null);

/// <summary>
/// Task counts under a milestone, computed on every read rather than stored. A cached counter
/// would go stale on every lane move, reset, and archive — and the query is one grouped scan of
/// an indexed column.
/// </summary>
public sealed record MilestoneCountsResponse(
    int Total,
    int Done,
    /// <summary>Work the pipeline can move right now. Excludes drafts — a task nobody has
    /// submitted is not in flight, and counting it as such made the roadmap contradict its
    /// own status badge.</summary>
    int InFlight,
    /// <summary>Parked until a human acts — the same <see cref="Orkka.Modules.Tasks.Contracts
    /// .AttentionStatuses.Actionable"/> set the Attention Inbox uses, so the roadmap and the
    /// inbox can never disagree about what is stuck.</summary>
    int NeedsAttention,
    /// <summary>Still in Drafting: planned but never submitted. Total is the sum of all four.</summary>
    int NotStarted);

/// <param name="RepoId">Null when the milestone spans repos, or the project has only one.</param>
/// <param name="RepoName">Resolved for display so the roadmap doesn't have to join repos
/// client-side just to label a card.</param>
public sealed record MilestoneResponse(
    Ulid Id,
    Ulid ProjectId,
    string Title,
    string? Outcome,
    string Status,
    int Position,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    MilestoneCountsResponse Counts,
    DateTimeOffset CreatedAt,
    Ulid? RepoId = null,
    string? RepoName = null,
    // Milestone branching: the integration branch its tasks land on, and how far that branch
    // has got toward the default branch. Null branch = nothing has been built under this
    // milestone yet, so there is no branch to show.
    string? BranchName = null,
    string ShipState = "NotReady",
    string? ShipFailureReason = null,
    DateTimeOffset? ShippedAt = null,
    // Trunk = tasks land one by one and the milestone ships as it completes. Always Trunk
    // now; Branch only appears on milestones from before Orkka stopped merging to forges.
    string IntegrationMode = "Trunk");

/// <summary>A task under a milestone, as the detail page lists it.</summary>
public sealed record MilestoneTaskResponse(
    Ulid Id,
    int Number,
    string Title,
    string Lane,
    string Status,
    string Priority,
    string Type);

/// <summary>An unfinished milestone earlier in the roadmap.</summary>
public sealed record MilestoneNeighbourResponse(Ulid Id, string Title, string Status);

/// <summary>
/// One milestone with its tasks.
/// </summary>
/// <param name="PreviousUnfinished">The nearest earlier milestone that is neither completed nor
/// cancelled, if any. Drives a "the one before this isn't done yet" hint — deliberately a hint
/// and not a gate, matching the platform's standing rule that planning ahead is the user's call
/// to make, not the tool's to block.</param>
public sealed record MilestoneDetailResponse(
    MilestoneResponse Milestone,
    IReadOnlyList<MilestoneTaskResponse> Tasks,
    MilestoneNeighbourResponse? PreviousUnfinished);

/// <summary>
/// The roadmap payload: milestones in <c>Position</c> order plus the counts for tasks that belong
/// to no milestone. The backlog is not a milestone and deliberately has no row of its own —
/// it is whatever hasn't been planned into one yet.
/// </summary>
public sealed record MilestoneListResponse(
    IReadOnlyList<MilestoneResponse> Milestones,
    MilestoneCountsResponse Backlog);

/// <param name="RepoId">Repo to plan against. Omitted = the project's first repo, which is the
/// right guess for the single-repo case and harmless for the rest since the user confirms.</param>
/// <param name="MilestoneId">Plan more work into an existing milestone instead of a new one.</param>
/// <param name="Goal">What to plan, in the user's words. Optional when
/// <paramref name="MilestoneId"/> is set: that milestone's outcome is the goal, and the server
/// derives it rather than having the client copy it into a second, divergent field.</param>
public sealed record PlanMilestoneRequest(
    string? Goal = null,
    Ulid? RepoId = null,
    Ulid? MilestoneId = null);

/// <param name="Key">Stable identifier for this task within the proposal, so
/// <paramref name="DependsOn"/> survives the user deleting other tasks before accepting.
/// Array indices would not: removing one silently slides every later edge onto its neighbour.</param>
/// <param name="DependsOn">Keys of tasks that must finish first. Only ever lower keys, which
/// is what keeps the graph acyclic without a cycle check.</param>
public sealed record PlannedTaskResponse(
    string Title,
    string Description,
    string Priority,
    string Type,
    int Key = 0,
    IReadOnlyList<int>? DependsOn = null,
    /// <summary>Repository name this task belongs in. Null falls back to the request's RepoId.
    /// Editable in the proposal, so a wrong guess is corrected before anything is created.</summary>
    string? Repo = null,
    /// <summary>Tasks sharing a key are two halves of one piece of work spanning repositories,
    /// and become a cross-repo group on accept. Null on nearly every task.</summary>
    int? ContractKey = null);

/// <summary>The interface crossing the boundary between one set of grouped tasks.</summary>
public sealed record PlannedContractResponse(int Key, string Contract);

/// <summary>A proposal. Nothing exists in the database until the user accepts it.</summary>
/// <param name="RepoId">The repo the plan was built against. Returned so the client can echo it
/// on accept — without it every planned task is created with no repo and can never be
/// submitted, since SubmitTask requires one.</param>
public sealed record MilestonePlanResponse(
    string MilestoneTitle,
    string Outcome,
    IReadOnlyList<PlannedTaskResponse> Tasks,
    Ulid? RepoId = null,
    IReadOnlyList<PlannedContractResponse>? Contracts = null);

/// <summary>
/// The proposal as the user edited it. Sent back verbatim rather than referencing a server-side
/// draft, so there is no half-accepted plan to expire or clean up.
/// </summary>
public sealed record AcceptMilestonePlanRequest(
    string MilestoneTitle,
    string? Outcome,
    IReadOnlyList<PlannedTaskResponse> Tasks,
    Ulid? RepoId = null,
    Ulid? MilestoneId = null,
    /// <summary>Echoed back from the proposal. Keys with no surviving pair of tasks are dropped
    /// on accept — the user may have deleted one half, and a group of one would hold that task's
    /// pull request behind a gate waiting on a sibling that will never exist.</summary>
    IReadOnlyList<PlannedContractResponse>? Contracts = null);
