namespace Orkka.Modules.Tasks.Contracts;

// ─────────────────────────────────────────────────────────────────────────────
// Worker-facing read snapshots and transition results (P10). These replace the
// Execution module's former direct TasksDbContext access: reads return slim
// immutable records, writes go through phase-shaped methods on ITasksApi that
// preserve the original single-SaveChanges atomicity units.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Full scalar snapshot of a task as the Execution module reads it.</summary>
public sealed record TaskSnapshot(
    Ulid Id,
    Ulid OrgId,
    Ulid ProjectId,
    Ulid? RepoId,
    int Number,
    string Title,
    string? Description,
    Priority Priority,
    TaskType Type,
    Lane CurrentLane,
    TaskStatus Status,
    bool IsArchived,
    string? RequesterLocale,
    int TotalTokensUsed,
    bool HasAgentWorking,
    string? BranchName,
    string? PrUrl,
    string? Tags,
    string? ContextFiles,
    string? ContextDocumentIds,
    bool SkipPlanning,
    bool SkipTesting,
    bool SkipCodeReview,
    bool IsMerged,
    int ReviewCycleCount,
    int QaCycleCount,
    Lane? QuestionSourceLane,
    Ulid? AssignedAgentId,
    Ulid? CurrentAgentId,
    Ulid? SourceMessageId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    // Whose runner executes this task's agent phases (trailing default keeps older
    // positional constructions compiling).
    string? RunnerUserId = null,
    // Merge page: pending human resolution choice + conflict snapshot (JSON).
    string? MergeResolutionStrategy = null,
    string? MergeResolutionGuidance = null,
    string? MergeConflictsJson = null,
    // Reset-to-Drafting: suffixes the fallback branch name (-r{n}) so a re-run never
    // resumes a previous run's pushed branch.
    int ResetCount = 0,
    // Per-task model override picked at creation — used in place of the dev agent's own
    // model for Planning/Developing. null = the agent's model.
    string? Model = null,
    // System-generated tasks skip the PM review phase (their description is already
    // agent-written). Orthogonal to SkipPlanning, which only skips the planning lane.
    bool SkipPmReview = false,
    // user id of whoever created the task — carried so every usage record the run writes
    // can be attributed to them. Null for system-generated tasks.
    string? CreatedByUserId = null,
    // Non-null = the repo's setup command failed for this worktree; the app cannot start.
    string? SetupFailure = null,
    // Non-null = autopilot submitted this task, under that session — the signal the
    // orchestrator resolves its human gates through (see EffectiveTaskPolicy). A task
    // submitted by hand leaves this null and keeps the project's configured behaviour.
    Ulid? AutopilotSessionId = null,
    // Interactive development: a human drives this task's turns from a live session instead of
    // the queue. The session marker is non-null only while that session is open.
    bool IsInteractive = false,
    DateTimeOffset? InteractiveSessionStartedAt = null,
    // Watermark of the newest user message already answered, and the human's explicit
    // "wrap up and ship" signal. See TaskEntity for why neither is inferred.
    DateTimeOffset? InteractiveAnsweredThroughAt = null,
    DateTimeOffset? InteractiveFinishRequestedAt = null,
    // The interface this task's work exposed to other repositories, extracted from its diff.
    // Only populated when something in another repo depended on it.
    string? ApiSurface = null,
    // Dev passes spent on a failed automated check (repo CheckCommand) after Developing.
    // Trailing default keeps older positional constructions compiling.
    int CheckCycleCount = 0,
    // The outcome this task belongs to, or null for Backlog work. Carried because it decides
    // the task's BASE BRANCH: milestone work forks from and merges into the milestone's
    // integration branch so the whole outcome lands as one merge, while Backlog work goes
    // straight to the repo default and ships on its own.
    Ulid? MilestoneId = null,
    // The base the worktree forked from (see TaskEntity.BaseBranch). Null for tasks from
    // before it was recorded; readers then fall back to resolving it.
    string? BaseBranch = null,
    // Dev passes already spent reconciling a moved base branch. The end-of-Developing sync
    // reads it to fail OPEN at the cap: a finished run must complete and leave the collision
    // to merge-time resolution, never park at the finish line (prefer completion over caps).
    int BaseSyncCycleCount = 0,
    // US dollars the requester approved beyond the project's task spend limit (see TaskEntity).
    decimal? SpendApprovedUsd = null,
    // The autopilot session this task runs under answers every question (see
    // AutopilotSession.AnswerEveryQuestion). Always false when AutopilotSessionId is null.
    // Carried here so EffectiveTaskPolicy stays a pure function of what it is handed.
    bool AutopilotAnswersEveryQuestion = false,
    // The cross-repo group this task is one half of, if any (see TaskEntity.GroupId). The
    // conflict resolver reads it: a Gated group's member is never merged by the resolver
    // itself, only by the merge sweep once every sibling can land.
    Ulid? GroupId = null,
    // The tracker ticket the task was imported from (SHOP-123, its URL, the epic it sits under)
    // and the folder inside the repository its epic is mapped to. Null for board-made tasks.
    string? ExternalKey = null,
    string? ExternalUrl = null,
    string? ExternalEpic = null,
    string? WorkingSubpath = null,
    // A Story member's place in its story, and its starting commit on the shared branch: the
    // review diff is ReviewBaseSha..HEAD (this member's change only).
    int? StoryOrder = null,
    string? ReviewBaseSha = null);

/// <summary>A task the user asked to reset (ResetRequestedAt set) — input for the worker's
/// reset sweep, which cancels any in-flight run, removes the worktree, and finalizes the
/// task back to Drafting/Draft. Lease fields let a replica skip tasks another replica is
/// still orchestrating; a null BranchName means no worktree was ever created.
/// ResetRequestedAt bounds how long the sweep waits for an offline runner before it
/// finalizes without the runner-side cleanup.</summary>
public sealed record ResetPendingTask(
    Ulid Id, Ulid OrgId, Ulid ProjectId, Ulid? RepoId,
    string Title, Lane CurrentLane, string? BranchName,
    string? LeaseOwner, DateTimeOffset? LeaseExpiresAt,
    string? RunnerUserId = null,
    DateTimeOffset? ResetRequestedAt = null);

/// <summary>A task waiting in the queue (Status = InQueue), in pickup order. Carries the
/// runner owner so the queue processor can apply claim affinity (a task is only claimable by
/// the Worker replica holding its owner's runner connection).</summary>
public sealed record QueuedTaskInfo(
    Ulid Id, Ulid OrgId, Ulid ProjectId, Ulid? RepoId,
    string? RunnerUserId = null,
    string? CreatedByUserId = null,
    // Which phase claiming this task would start, which is what decides whether it needs an
    // execution slot or may run ahead of one. See TaskPhaseKind.
    Lane CurrentLane = Lane.Ready,
    bool SkipPlanning = false,
    // Prep-ahead is an autopilot speedup and stays scoped to it: a hand-submitted task's plan
    // parks in PlanReady for a human, so planning it early buys nothing and bills for a plan
    // that may never be approved.
    bool IsAutopilot = false);

/// <summary>
/// What claiming a queued task would actually start, and therefore which budget it spends.
///
/// The line that matters is whether the phase WRITES THE BRANCH. Only <see cref="Build"/> does,
/// and only <see cref="Build"/> is what MaxParallelTasks and the same-repo exclusion exist to
/// bound. The other two fork a worktree, read, and hand the task on — running them while the
/// execution slots are busy is what keeps those slots doing the thing they are for.
///
/// <see cref="Prep"/> (PM review, planning) and <see cref="Review"/> (code review) are split
/// because they differ in one respect that decides their budgets: prep is SPECULATIVE — it runs
/// on a task nothing has started yet, and a session that stops before reaching it has paid for
/// a plan nobody used — so it is bounded by PlanAheadDepth. A review is work the task has
/// already earned; it will run whether or not it overlaps anything, so overlapping it costs no
/// extra tokens at all, only finishes sooner.
/// </summary>
public enum TaskPhaseKind
{
    Prep,
    Build,
    Review
}

/// <summary>A live interactive session that no worker currently holds. Same routing fields as
/// <see cref="QueuedTaskInfo"/> — claim affinity applies identically, since a session streams to
/// its creator's runner — but a separate type because the two are answers to
/// different questions and must not drift into one query.</summary>
public sealed record InteractiveSessionInfo(
    Ulid Id, Ulid OrgId, Ulid ProjectId, Ulid? RepoId,
    DateTimeOffset SessionStartedAt,
    string? RunnerUserId = null,
    // Lease state, decided by the caller rather than the query: a worker that restarted still
    // owns leases for sessions nothing is driving any more, and only that worker knows which
    // ones it is actually running. Filtering on "unheld or expired" in SQL strands those for the
    // whole lease TTL after every restart.
    string? LeaseOwner = null,
    DateTimeOffset? LeaseExpiresAt = null);

/// <summary>An Active, non-archived task in a project (concurrency accounting). Carries enough
/// to say WHICH budget it is spending — a task exploring the codebase to write a plan holds no
/// execution slot, so counting it as one is what used to stall the queue behind it.</summary>
public sealed record ActiveTaskInfo(
    Ulid Id, Ulid? RepoId, Lane CurrentLane = Lane.Developing, bool SkipPlanning = false,
    bool IsAutopilot = false);

/// <summary>Which budget a task's current or next phase spends. One classifier, so the queue
/// processor's admission decision and its accounting of what is already running can never
/// disagree about what "in flight" means.</summary>
public static class TaskPhases
{
    /// <summary>
    /// Which budget a phase spends.
    ///
    /// <paramref name="skipPlanning"/> is what makes Ready ambiguous: normally it is the PM
    /// review that precedes planning, but on a SkipPlanning task the same lane runs the
    /// workspace setup and drops straight into Developing — so it is build work from the
    /// moment it is claimed.
    ///
    /// Testing is deliberately Build, unlike the other two read-heavy lanes: the QA agent
    /// writes E2E tests into the worktree, so it is not read-only and must not overlap the
    /// branch's own development.
    /// </summary>
    public static TaskPhaseKind Of(Lane lane, bool skipPlanning) => lane switch
    {
        Lane.Ready when !skipPlanning => TaskPhaseKind.Prep,
        Lane.Planning => TaskPhaseKind.Prep,
        Lane.CodeReview => TaskPhaseKind.Review,
        _ => TaskPhaseKind.Build
    };

    public static TaskPhaseKind Kind(this QueuedTaskInfo task)
        => Of(task.CurrentLane, task.SkipPlanning);

    public static TaskPhaseKind Kind(this ActiveTaskInfo task)
        => Of(task.CurrentLane, task.SkipPlanning);
}

/// <summary>A Blocked task the auto-recovery sweep may re-queue: its automatic rounds are not
/// exhausted and its last failed run recorded an error. The caller classifies the error —
/// whether a failure is a provider blip is run semantics, and keeping that judgement out of
/// this query is what keeps it in one place (the same classifier the in-run retry uses).</summary>
public sealed record AutoRecoverableBlockedTask(
    Ulid Id, Ulid OrgId, Ulid ProjectId, string Title,
    int AutoRecoveryCount, string? LastError);

/// <summary>A task that was mid-phase when the worker restarted (crash recovery).</summary>
public sealed record ResumableTaskInfo(Ulid Id, Lane CurrentLane);

/// <summary>A task in CodeReview/Done with a branch but no PR yet.</summary>
public sealed record PrCandidateTask(
    Ulid Id, Ulid OrgId, Ulid ProjectId, Ulid? RepoId,
    string? BranchName, string Title, string? Description, TaskType Type,
    // Decides the PR's base: a milestone task targets its milestone's integration branch, so
    // the outcome lands as one merge. Null (Backlog) targets the repo default, as before.
    Ulid? MilestoneId = null,
    // The base the worktree actually forked from, when recorded — wins over MilestoneId.
    string? BaseBranch = null);

/// <summary>A Done task with an unmerged PR, ordered oldest-completed first.</summary>
public sealed record MergeCandidateTask(
    Ulid Id, Ulid OrgId, Ulid ProjectId, Ulid? RepoId, string? PrUrl,
    // Non-null = a human picked a merge resolution that hasn't run yet.
    string? MergeResolutionStrategy = null,
    // The cross-repo group this task is one half of, if any. Under a Gated policy its PR is
    // held until every sibling can land too.
    Ulid? GroupId = null,
    // Whether this task's merge moved the repo's DEFAULT branch. False for a task under a
    // milestone that has an integration branch — its PR landed there instead. Same meaning as
    // TaskMerged.LandedOnDefaultBranch, and the deployment backstop keys on it for the same
    // reason: a watch row for a merge that did not touch the deployed branch is a wasted build.
    bool LandedOnDefaultBranch = true,
    // The base the task was built on — what its PR must open against. Null for older tasks.
    string? BaseBranch = null,
    // Where the task is NOW. A task that was Completed once stays a candidate after a failed
    // merge sends it back to Developing for rework, and the sweep must leave it alone until
    // that lap brings it back to Done — unless a human armed a resolution.
    Lane CurrentLane = Lane.Done,
    // Queued or running again (a rework pass after a failed merge): the sweep leaves it alone
    // until that run ends. A task parked on the merge page is WaitingCodeReview and is looked at.
    TaskStatus Status = TaskStatus.Completed,
    // A recorded merge failure, and its kind. A conflict notice (kind Unknown) whose PR merges
    // cleanly again is stale — the resolver fixed it — and the sweep clears it.
    bool HasMergeFailure = false,
    MergeFailureKind? MergeFailureKind = null);

/// <summary>A milestone whose integration branch recently landed on the default branch —
/// input for the deployment sweep's lost-event backstop, the milestone-shaped counterpart of
/// <see cref="MergeCandidateTask"/>.</summary>
public sealed record ShippedMilestoneInfo(
    Ulid Id, Ulid OrgId, Ulid ProjectId, string Title, DateTimeOffset ShippedAt);

/// <summary>
/// Whether a cross-repo group's pull requests may land yet, and in what order.
/// </summary>
/// <param name="Ready">Every member has finished and holds a landable PR (or has already merged,
/// or produced no PR at all). False means at least one half is still being written, so landing
/// the others would ship exactly the half-migrated state the gate exists to prevent.</param>
/// <param name="MergeOrder">Member task ids, dependency order — whatever DEFINES an interface
/// before whatever calls it. Not a correctness requirement between separate repos, which cannot
/// conflict with each other, but it decides which half is live first if the run is interrupted,
/// and a provider that is live early is harmless where a caller that is live early is not.</param>
/// <param name="BlockedBy">Human-readable reason the group is not ready, for the UI. Null when
/// it is.</param>
public sealed record TaskGroupMergeState(
    Ulid GroupId,
    TaskGroupMergePolicy MergePolicy,
    bool Ready,
    IReadOnlyList<Ulid> MergeOrder,
    string? BlockedBy);

/// <summary>A settled task whose documentation pass has not run yet.</summary>
public sealed record DocSyncCandidateTask(
    Ulid Id, Ulid OrgId, Ulid ProjectId, string Title, string? CreatedByUserId);

/// <summary>A Done task old enough for the auto-archive sweep. ProjectId is what the sweep
/// groups by — the setting that authorises the archive lives on the project, not the task;
/// Number and Title are there so the log line names the task a human would recognise.</summary>
public sealed record AutoArchiveCandidateTask(
    Ulid Id, Ulid OrgId, Ulid ProjectId, int Number, string Title, DateTimeOffset CompletedAt);

/// <summary>
/// What a task's dev work changed, read back after the branch is gone. Aggregated across every
/// dev cycle the task went through: file paths from all snapshots (deduplicated), the newest
/// snapshot's diff. This is the only durable record — worktrees are GC'd and branches die on
/// merge — which is exactly why the documentation pass reads it instead of a repository.
/// </summary>
/// <summary>What a task changed, as paths and counts. Never the code: Orkka keeps no copy of it.</summary>
public sealed record TaskChangeSummary(
    IReadOnlyList<string> FilePaths, int FileCount, int Additions, int Deletions);

/// <summary>
/// Payload of TaskEntity.MergeConflictsJson (camelCase): the conflicted files + AI
/// plain-language summary shown on the merge page, plus per-file human choices when
/// strategy = "perFile" ("ai" | "task" | "current").
/// </summary>
public sealed record MergeConflictSnapshot(
    IReadOnlyList<MergeConflictSnapshotFile> Files, string? Summary, DateTimeOffset DetectedAt);

public sealed record MergeConflictSnapshotFile(string Path, string Detail, string? Choice = null);

/// <summary>A human/agent conversation entry rendered into agent prompts.</summary>
public sealed record ConversationMessage(string Role, MessageType MessageType, string Content);

/// <summary>An unanswered human message in an interactive session. The timestamp is the point of
/// the record: it becomes the watermark once the turn that consumed it completes.</summary>
public sealed record InteractiveUserMessage(string Content, DateTimeOffset CreatedAt);

/// <summary>Shared constants for interactive sessions.</summary>
public static class InteractiveSessionDefaults
{
    /// <summary>
    /// The title a session gets when the human did not name it — they should be able to start
    /// typing about the bug before naming it.
    ///
    /// Shared rather than duplicated because it is load-bearing in two places that must agree:
    /// the endpoint writes it, and auto-titling only overwrites a title that still equals it, so
    /// a name the user chose is never replaced by a generated one.
    /// </summary>
    public const string UntitledSession = "Interactive session";
}

/// <summary>A user-uploaded task attachment, prompt-facing. BlobName is relative to the
/// task-attachments container — used to mint fresh signed download URLs for agents.</summary>
public sealed record TaskAttachmentInfo(string FileName, string FileType, long FileSize, string BlobName);

/// <summary>A persisted task message (for SignalR NewMessage events).</summary>
public sealed record TaskMessageSnapshot(
    Ulid Id, Ulid TaskId, Ulid ProjectId, string Role, string Content,
    MessageType MessageType, string? SenderId, DateTimeOffset CreatedAt);

/// <summary>Result of blocking a task with an error reason (status event + thread broadcast).</summary>
public sealed record BlockedTaskResult(Ulid ProjectId, TaskMessageSnapshot? Message);

/// <summary>
/// Result of recording a merge failure. Changed=false means the same reason was already
/// recorded (no new message, no broadcast needed).
/// </summary>
public sealed record MergeFailureUpdate(Ulid ProjectId, bool Changed, TaskMessageSnapshot? Message);

/// <summary>Scalar snapshot of a task execution (memory extraction, diagnostics).</summary>
public sealed record ExecutionSnapshot(
    Ulid Id, Ulid TaskId, Ulid OrgId, Ulid? AgentId, Lane Lane,
    ExecutionStatus Status, string? AgentLog, int TokensUsed,
    int PromptTokens, int CompletionTokens, string? ErrorMessage,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);

/// <summary>Result of starting a phase execution (task claimed into the phase).</summary>
public sealed record ExecutionStart(Ulid ExecutionId, Lane FromLane, TaskSnapshot Task);

/// <summary>Resumable CLI session reported by a task's previous completed run in a lane —
/// CompletedAt bounds which conversation messages are "new since that run".</summary>
/// <summary>
/// A resumable CLI session left behind by an earlier execution in the same lane: its id,
/// when that execution ended, and whether it ended by being cut off (transport drop,
/// runner disconnect, worker restart) rather than by finishing its work.
/// </summary>
public sealed record ExecutionSessionInfo(
    string SessionId, DateTimeOffset CompletedAt, bool Interrupted = false,
    // When the run began — lets the resume decision spot a COMPLETED run that lasted seconds,
    // the signature of a session poisoned by a mid-response connection drop. Trailing default
    // keeps older positional constructions compiling.
    DateTimeOffset? StartedAt = null);

/// <summary>Enough of a task to render a clickable reference from another module's page —
/// the billing breakdown's "which task cost what" rows.</summary>
public sealed record TaskDisplayRef(Ulid ProjectId, int Number, string Title);

/// <summary>The latest approved plan, serialized with camelCase (prompt-ready JSON).</summary>
public sealed record ApprovedPlanInfo(string Summary, string ContentJson);

/// <summary>Result of persisting a new plan together with its producing execution.</summary>
public sealed record PlanSaveResult(int PlanVersion, ExecutionSnapshot Execution);

/// <summary>Lane/status the task moved to when the developing phase finished.</summary>
public sealed record DevelopingTransition(Lane FromLane, Lane NewLane, TaskStatus NewStatus);

/// <summary>Outcome of applying a reviewer verdict to the task.</summary>
public sealed record CodeReviewTransition(
    Lane FromLane, Lane NewLane, TaskStatus NewStatus,
    int ReviewCycleCount, bool EscalatedToHuman);

/// <summary>Outcome of applying a QA test verdict to the task.</summary>
public sealed record QaTransition(
    Lane FromLane, Lane NewLane, TaskStatus NewStatus,
    int QaCycleCount, bool EscalatedToHuman);

/// <summary>
/// Outcome of applying a failed automated check (the repo's CheckCommand after Developing).
/// <paramref name="EscalatedToHuman"/> means the retry budget is spent and the task is parked
/// as Developing/WaitingUserVerification instead of being re-queued again.
/// </summary>
public sealed record CheckTransition(
    Lane FromLane, Lane NewLane, TaskStatus NewStatus,
    int CheckCycleCount, bool EscalatedToHuman);

/// <summary>
/// Outcome of sending a task back to Developing because the base branch stopped merging
/// cleanly. <paramref name="EscalatedToHuman"/> means the retry budget is spent and the task
/// is parked on its merge page instead of being re-queued again.
/// </summary>
public sealed record BaseSyncRequeue(
    Lane FromLane, Lane NewLane, TaskStatus NewStatus,
    int BaseSyncCycleCount, bool EscalatedToHuman);
