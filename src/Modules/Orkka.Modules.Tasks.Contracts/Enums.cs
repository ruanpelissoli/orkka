namespace Orkka.Modules.Tasks.Contracts;

// Task-domain enums shared with cross-module consumers (Worker orchestration, board events).

public enum DependencySource
{
    Ai,
    Manual
}

/// <summary>
/// Progress of the documentation pass that runs once a task's work has settled — reached Done
/// and, when it touched a repo, actually merged. Stored as a string so values can be appended
/// without renumbering existing rows (unlike <see cref="Lane"/>).
///
/// <see cref="Skipped"/> is the expected outcome, not a failure: most tasks change nothing a
/// document needs to say, and the pass is designed to stop after one cheap triage call.
/// </summary>
public enum DocSyncStatus
{
    /// <summary>Not yet considered — the sweep's entry condition.</summary>
    None,

    /// <summary>Claimed by a worker; a run is in flight.</summary>
    Running,

    /// <summary>Documents were written.</summary>
    Completed,

    /// <summary>Ran (or was correctly declined) and produced nothing to write.</summary>
    Skipped,

    /// <summary>The run errored. Retried up to a small cap, then left alone.</summary>
    Failed
}

public enum ExecutionStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>Where a forecast was shown. Scored separately — an estimate made at creation and
/// one made after an approved plan exists are different predictions.</summary>
public enum ForecastSurface
{
    TaskCreate,
    PlanApproval,
    MilestoneRollup
}

/// <summary>
/// Whether a forecast can be scored. <see cref="Invalidated"/> is not a miss: it means the task
/// stopped matching what the forecast assumed, so counting it would corrupt the accuracy metric
/// rather than measure it.
/// </summary>
public enum ForecastOutcome
{
    Pending,
    Scored,
    Invalidated
}

/// <summary>
/// Lifecycle of a milestone. Transitions are automatic: the first task to start work moves it to
/// Active, the last one to merge moves it to Completed. Cancelled is the only human-driven state
/// and it never deletes the tasks underneath.
/// </summary>
public enum MilestoneStatus
{
    Planned,
    Active,
    Completed,
    Cancelled
}

/// <summary>
/// How far a completed milestone has got toward landing on the repo's default branch.
///
/// Separate from <see cref="MilestoneStatus"/> on purpose: Completed describes the work
/// ("every task under this is Done"), ShipState describes the merge that follows it. Keeping
/// them apart is what lets a ship fail — and be retried — without lying about whether the work
/// itself finished.
///
/// The transition into <see cref="Pending"/> is a DB flag, not an event: the merge runs on the
/// worker, and API→worker actions go through a claimed row rather than the in-process bus.
/// </summary>
public enum MilestoneShipState
{
    /// <summary>Not every task is Done yet — nothing to ship. Also the resting state of every
    /// milestone that predates milestone branching.</summary>
    NotReady,

    /// <summary>Ready to merge into the default branch, waiting for the ship sweep to claim it.</summary>
    Pending,

    /// <summary>Claimed by the sweep; the PR is open or the merge is in flight.</summary>
    Shipping,

    /// <summary>The milestone branch is on the default branch. The one state that triggers a deploy.</summary>
    Shipped,

    /// <summary>The merge could not complete (conflicts, branch protection, CI). See
    /// <c>Milestone.ShipFailureReason</c>; a human retries from the roadmap.</summary>
    Failed
}

/// <summary>
/// How a milestone's tasks reach the repo's default branch. Decided once, when the roadmap is
/// seeded, and never re-derived: the base branch a task forks from must not change under a
/// milestone whose earlier tasks already landed somewhere.
/// </summary>
public enum MilestoneIntegrationMode
{
    /// <summary>Tasks fork from and merge into <c>orkka/milestone/{slug}-{id8}</c>; the branch
    /// ships to the default branch once, when the last task completes. One deploy per milestone.</summary>
    Branch,

    /// <summary>Tasks fork from and merge straight into the default branch; every task merge is
    /// a deploy. Reserved for the first milestone of a greenfield managed project, where the only
    /// thing on the default branch is a placeholder and the owner is waiting to see anything real.</summary>
    Trunk
}

public enum Lane
{
    Drafting,
    Ready,
    Planning,
    PlanReady,
    PendingQuestions,
    Developing,
    Testing,
    CodeReview,
    Done
}

/// <summary>
/// Lifecycle of an interactive session's approval prompt.
///
/// <see cref="Expired"/> is deliberately distinct from <see cref="Denied"/>: nobody refused an
/// expired request, so the agent is told the prompt lapsed and may ask again, while a denial
/// tells it not to retry. Conflating them teaches the agent that its tools are closed.
/// </summary>
public enum ApprovalStatus
{
    Pending,
    Allowed,
    AllowedForSession,
    Denied,
    Expired,
}

/// <summary>
/// Why a merge is stuck — the part of a failure that a UI can act on, separated from the
/// free-text reason it shows.
///
/// Exists to answer one question honestly: is automation still trying, or has it run out of
/// moves? <see cref="Unknown"/> means the former (a transient failure the poller retries every
/// cycle, which is most of them) and must never be presented as terminal.
/// <see cref="ResolutionExhausted"/> is the one that means "this really failed": the resolver
/// ran, was checked, and could not produce a finished merge.
///
/// Stored as a nullable string so values can be appended without renumbering rows.
/// </summary>
public enum MergeFailureKind
{
    /// <summary>Unclassified, and still on the retry path. The default.</summary>
    Unknown,

    /// <summary>The user's environment must change first (dirty working repo, wrong branch
    /// checked out, runner offline). No resolver run can help, so the page says what to fix.</summary>
    EnvironmentBlocked,

    /// <summary>Automatic resolution ran its full budget, was verified, and still could not
    /// finish the merge. Terminal for automation — a human has to decide.</summary>
    ResolutionExhausted,

    /// <summary>The merge itself was resolved, but the branch could not be pushed (no valid
    /// token, branch gone). Not a conflict — retrying resolution would fix nothing.</summary>
    PushFailed,

    /// <summary>The forge refused an otherwise-mergeable PR: branch protection, a required
    /// check, or a review requirement. Nothing in the code needs changing.</summary>
    ForgeRejected
}

public static class MergeFailureKinds
{
    /// <summary>True for a failure automation is out of moves on — a human has to act. The
    /// retry path (<see cref="MergeFailureKind.Unknown"/>, <see cref="MergeFailureKind.EnvironmentBlocked"/>)
    /// is not terminal: the merge sweep tries again every cycle.</summary>
    public static bool IsTerminal(MergeFailureKind? kind) =>
        kind is MergeFailureKind.ResolutionExhausted or MergeFailureKind.PushFailed or MergeFailureKind.ForgeRejected;
}

public enum MessageType
{
    Text,
    Question,
    Progress,
    Error,
    Plan,
    CodeReview,
    QaReport,
    Answer,
    ToolCall,
    ToolResult,
    SystemEvent,
    // A comment written on the tracker ticket the task was imported from (role "tracker").
    // Part of the conversation agents read, fenced as untrusted like every message.
    ExternalComment
}

public enum PlanStatus
{
    Draft,
    PendingReview,
    Approved,
    Rejected
}

public enum Priority
{
    Low,
    Medium,
    High,
    Critical
}

public enum StreamEventType
{
    AgentThinking,
    ToolUsage,
    CodeChange,
    Question,
    PlanGenerated,
    LaneChanged,
    StatusChanged,
    Error,
    Progress,
    NewMessage
}

public enum TaskStatus
{
    Draft,
    Active,
    InQueue,
    WaitingPlanApproval,
    WaitingCodeReview,
    ReasoningTimeout,
    OutOfTokens,
    Blocked,
    Completed,
    Archived,
    WaitingAnswer,
    WaitingQaReview,
    WaitingUserVerification,
    // Parked because the owner's runner is offline everywhere. Auto-requeued when their
    // runner reconnects (hub Hello sweep).
    WaitingForRunner,
    // User requested a full reset to Drafting: the worker's reset sweep cancels any
    // in-flight run, removes the worktree, wipes messages/plans, then finalizes to
    // Draft. Display-only — TaskEntity.ResetRequestedAt is the authoritative flag
    // (a racing phase completion may overwrite this status; it cannot clear the flag).
    Resetting
}

public enum TaskType
{
    Feature,
    BugFix,
    Refactor,
    Improvement
}

/// <summary>
/// Why an autopilot session ended. Every value except <see cref="Running"/> is terminal — a
/// session is never resumed, because the cap and the counters are defined over the window the
/// user opened. Toggling autopilot back on starts a new one.
/// </summary>
public enum AutopilotStopReason
{
    /// <summary>Still submitting. The only non-terminal value.</summary>
    Running,
    /// <summary>The user toggled autopilot off.</summary>
    Manual,
    /// <summary>The session's estimated list-price spend reached its cap.</summary>
    CapReached,
    /// <summary>No longer set: it meant an org-wide budget, which the local-only product does not
    /// have (Orkka bills nothing). Kept — stored as a string — so sessions that stopped on it
    /// still load.</summary>
    BudgetBlocked,
    /// <summary>Too many submitted tasks failed in a row. Without this a broken setup command
    /// burns the whole cap on work that cannot succeed.</summary>
    ConsecutiveFailures,
    /// <summary>
    /// Nothing left for autopilot to do: no submittable task in Drafting, and nothing it already
    /// submitted can still move without a human.
    ///
    /// Deliberately NOT "nothing to submit". A milestone whose backend tasks are running and
    /// whose frontend tasks are blocked on them has zero submittable work at that instant, and
    /// stopping there would switch autopilot off in the middle of its own dependency wave. Work
    /// parked for a human — an unresolved question, a rejected review, a merge conflict autopilot
    /// could not resolve — does not count as progress either: it will never move on its own, and
    /// holding the session open for it means holding it open until morning.
    /// </summary>
    Drained,
    /// <summary>The project was deleted, or its repos were unlinked, while a session was open.</summary>
    ProjectUnavailable
}

/// <summary>
/// Where a <c>TaskGroup</c>'s contract came from. Stored because a human-edited contract must
/// survive anything that would otherwise regenerate it — a re-split, or an agent's "as built"
/// append — and there is no way to tell the two apart from the text.
/// </summary>
public enum ContractSource
{
    /// <summary>Written by the split pass from the request and the repo trees.</summary>
    Declared,

    /// <summary>A human wrote or corrected it. Authoritative.</summary>
    Human
}

/// <summary>
/// Whether a group's pull requests land together.
///
/// <see cref="Gated"/> is the default because the reason to group tasks in the first place is
/// that neither half works without the other. It removes the WINDOW between merges; it does not
/// make them transactional — two forges cannot be committed atomically, and nothing here
/// pretends otherwise.
/// </summary>
public enum TaskGroupMergePolicy
{
    /// <summary>Hold every member's PR until all of them can merge, then merge in dependency
    /// order.</summary>
    Gated,

    /// <summary>Each member merges on its own, exactly as an ungrouped task does. For the case
    /// where the halves genuinely ship independently.</summary>
    Independent
}

/// <summary>
/// What a <c>TaskGroup</c> is for. Stored as a number; never renumber.
/// </summary>
public enum TaskGroupKind
{
    /// <summary>One request split across repositories, written against a shared contract and
    /// landed all-or-nothing (the original — and still the default — meaning).</summary>
    CrossRepo = 0,

    /// <summary>A tracker Story: its sub-tasks are members worked in order on ONE shared branch
    /// per repository, each building on the code the previous one pushed, published as one
    /// pull request (draft after the first lands, ready after the last).</summary>
    Story = 1,
}

/// <summary>
/// Lifecycle of a <c>TaskGroup</c>.
///
/// <see cref="PartiallyMerged"/> is a real, reachable state rather than an error: once one
/// member's PR is merged it cannot be un-merged, so a failure part-way through the gate leaves
/// the group genuinely half-landed. Recording it is the only honest option — collapsing it into
/// Merged would hide a broken deploy, and into Active would re-attempt merges that already
/// happened.
/// </summary>
public enum TaskGroupStatus
{
    /// <summary>Created, waiting for the split pass to write the contract and the members.</summary>
    Splitting,

    /// <summary>Members exist and are moving through the pipeline.</summary>
    Active,

    /// <summary>Every member is done; the gate is waiting on mergeability.</summary>
    AwaitingMerge,

    /// <summary>Every member merged.</summary>
    Merged,

    /// <summary>Some members merged and one failed. Needs a human.</summary>
    PartiallyMerged,

    /// <summary>Abandoned before it landed.</summary>
    Cancelled
}
