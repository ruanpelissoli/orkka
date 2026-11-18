namespace Orkka.Modules.Tasks.Contracts;

/// <summary>A sanitized task spec extracted from a PM agent chat message (repo already resolved).</summary>
public sealed record ChatTaskSpec(
    string Title,
    string? Description,
    string Priority,
    string Type,
    Ulid RepoId,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string>? ContextFiles,
    // Null = the Backlog. Trailing default keeps existing positional constructions compiling.
    Ulid? MilestoneId = null);

/// <summary>A milestone as agent chat sees it — enough to list, reference, and plan into.</summary>
public sealed record ChatMilestoneRef(Ulid Id, string Title, string? Outcome, string Status);

/// <summary>Identity of a task created from a PM chat message.</summary>
/// <summary>How a board operation ended. <see cref="Failure"/> maps to an HTTP status for
/// endpoints (NotFound → 404, Conflict → 409, Invalid → 400) and to a tool error for MCP.
/// <see cref="Warning"/> is a non-fatal note (e.g. the forge copy of a review didn't land).</summary>
public sealed record BoardOpOutcome(
    bool Ok,
    BoardOpFailure Failure = BoardOpFailure.None,
    string? Message = null,
    Ulid? EntityId = null,
    string? Warning = null,
    // A merge click that hit conflicts and successfully armed automatic resolution. The
    // click WORKED — reporting it as a failure (the old contract) is why users read the
    // auto-merge tool as broken. Ok is true; the merge itself lands on a later worker cycle.
    bool ResolutionStarted = false)
{
    public static BoardOpOutcome Success(Ulid? entityId = null, string? warning = null) => new(true, EntityId: entityId, Warning: warning);
    public static BoardOpOutcome ResolutionInProgress(Ulid entityId, string message)
        => new(true, Message: message, EntityId: entityId, ResolutionStarted: true);
    public static BoardOpOutcome NotFound(string message = "Not found.") => new(false, BoardOpFailure.NotFound, message);
    public static BoardOpOutcome Conflict(string message) => new(false, BoardOpFailure.Conflict, message);
    public static BoardOpOutcome Invalid(string message) => new(false, BoardOpFailure.Invalid, message);
}

public enum BoardOpFailure { None, NotFound, Conflict, Invalid }

/// <summary>The plan awaiting a human on a task — enough for a reviewer (or an MCP client) to
/// decide without opening the app.</summary>
public sealed record PendingPlanSummary(Ulid PlanId, int Version, string Summary, string Approach, IReadOnlyList<string> Files);

/// <summary>An inline review comment for <see cref="ITasksApi.SubmitCodeReviewAsync"/>.</summary>
public sealed record BoardReviewComment(string Path, int Line, string Body);

/// <summary>A task waiting on a human — the MCP/agent-facing slice of the Attention Inbox.</summary>
public sealed record AttentionTaskSummary(
    Ulid TaskId, int Number, string Key, string Title, string Lane, string Status,
    Ulid ProjectId, string ProjectSlug, DateTimeOffset UpdatedAt);

public sealed record ExternalTaskResult(Ulid TaskId, int Number, string Title, bool Created);

/// <summary>A task imported from a tracker ticket. Runs on <paramref name="OwnerUserId"/>'s
/// runner — the developer the ticket is assigned to, whose machine polled it.</summary>
/// <param name="ExternalRef">Idempotency key: <c>jira:{site}:{key}</c> / <c>ado:{org}:{id}</c>.</param>
/// <param name="RepoId">Null when routing could not tell — the task is created without one and
/// asks (<see cref="ITasksApi.AskTrackerRepoAsync"/>); it cannot be submitted until answered.</param>
/// <param name="SpansRepoIds">Two or more repositories the ticket spans: the task is created as a
/// cross-repo group (like a task the human marked as spanning), split when it is submitted.</param>
public sealed record TrackerTaskSpec(
    Ulid OrgId,
    Ulid ProjectId,
    Ulid? RepoId,
    string OwnerUserId,
    string ExternalRef,
    string ExternalKey,
    string ExternalUrl,
    string Title,
    string Description,
    TaskType Type,
    Priority Priority,
    IReadOnlyList<string> Tags,
    string? Epic = null,
    string? Subpath = null,
    IReadOnlyList<Ulid>? SpansRepoIds = null);

/// <summary>How far a tracker task has got — what its ticket's status is moved to follow.</summary>
/// <param name="Started">An agent has picked it up (claimed, or past Ready) — the Planning step.</param>
/// <param name="Developing">Its plan was approved: a Developing run happened, or it is past it.</param>
/// <param name="InReview">It reached Orkka's Code Review lane (or got past it).</param>
/// <param name="GroupId">The Story group it is a member of, if any.</param>
/// <param name="PublishedPrUrl">The forge pull request opened for it, if any.</param>
/// <param name="LandedInFolder">Squash-merged into a linked local folder — merged, with no forge
/// pull request to wait on.</param>
/// <param name="RepoId">The task's repository now — set later than import when routing asked.</param>
public sealed record TrackerTaskState(
    Ulid TaskId, bool Started, string? PublishedPrUrl, bool LandedInFolder, bool IsArchived, Ulid? RepoId = null,
    bool Developing = false, bool InReview = false, Ulid? GroupId = null);

/// <summary>What happened to a task whose ticket left the source.</summary>
public enum TrackerTaskRetirement
{
    /// <summary>Nothing had started — the task is archived.</summary>
    Archived,
    /// <summary>Work had started — the task is left alone, with a note, for a human to decide.</summary>
    Held,
    /// <summary>The task no longer exists, or was already archived.</summary>
    Gone,
}

public sealed record CreatedChatTask(Ulid Id, int Number, string Title);

/// <summary>A task linked to the agent chat message that created it.</summary>
public sealed record ChatTaskLink(Ulid SourceMessageId, Ulid Id, int Number, string Title);

/// <summary>Board listing projection for agent chat tools — description stays out.</summary>
public sealed record ProjectTaskSummary(
    Ulid Id, int Number, string Title, Lane CurrentLane, TaskStatus Status,
    Priority Priority, TaskType Type, Ulid? RepoId);

/// <summary>A task an agent ran phase executions on (or is assigned to) — the agent chat
/// "my work history" projection. LanesWorked/ExecutionCount/LastWorkedAt cover only that
/// agent's executions, not the whole task.</summary>
public sealed record AgentTaskSummary(
    Ulid Id, int Number, string Title, Lane CurrentLane, TaskStatus Status,
    Priority Priority, TaskType Type, IReadOnlyList<Lane> LanesWorked,
    int ExecutionCount, DateTimeOffset? LastWorkedAt);

/// <summary>One phase execution of a task — the agent chat task-history projection.
/// FilesChanged is the execution's raw JSON array of repo paths, when recorded.</summary>
public sealed record TaskExecutionSummary(
    Ulid? AgentId, Lane Lane, ExecutionStatus Status,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    int TokensUsed, string? ErrorMessage, string? FilesChanged);

/// <summary>
/// Public surface of the Tasks module (implemented by Orkka.Modules.Tasks.PublicApi.TasksApi).
/// </summary>
public interface ITasksApi
{
    /// <summary>Number of tasks per project (archived included). Missing keys mean zero tasks.</summary>
    Task<IReadOnlyDictionary<Ulid, int>> GetTaskCountsAsync(
        IReadOnlyCollection<Ulid> projectIds, CancellationToken ct = default);

    /// <summary>
    /// Creates a Drafting task from an external trigger (a GitHub issue, a failed CI run).
    /// Idempotent on <paramref name="externalRef"/> per project: a second call for the same
    /// ref returns the existing task with <c>Created = false</c>. Autopilot submits Drafting
    /// tasks when running; otherwise a human triages them.
    /// </summary>
    // ── Board operations (shared by the HTTP endpoints and the MCP tools) ────

    /// <summary>Approve or reject a plan awaiting review. Approve → Developing (queued);
    /// reject → plan deleted, feedback recorded, back to Planning (queued).</summary>
    Task<BoardOpOutcome> ReviewPlanAsync(
        Ulid orgId, Ulid taskId, Ulid planId, bool approve, string? feedback, CancellationToken ct = default);

    /// <summary>The task's PendingReview plan, or null when nothing awaits review.</summary>
    Task<PendingPlanSummary?> GetPendingPlanAsync(Ulid orgId, Ulid taskId, CancellationToken ct = default);

    /// <summary>Post a human message on a task. Resumes the agent when the task was waiting
    /// (PendingQuestions, WaitingUserVerification, WaitingCodeReview). EntityId = message id.</summary>
    Task<BoardOpOutcome> AnswerTaskAsync(
        Ulid orgId, Ulid taskId, string content, string messageType, string? senderUserId, string? locale,
        CancellationToken ct = default);

    /// <summary>Submit a code review verdict on the task's PR: "approve" (→ Done),
    /// "request_changes" (→ Developing, feedback recorded), or "comment". The forge copy is
    /// best-effort — a refusal lands in <see cref="BoardOpOutcome.Warning"/>.</summary>
    Task<BoardOpOutcome> SubmitCodeReviewAsync(
        Ulid orgId, Ulid taskId, string decision, string? body, IReadOnlyList<BoardReviewComment>? comments,
        CancellationToken ct = default);

    /// <summary>Squash-merge the task's PR and complete the task. A conflict arms the automatic
    /// resolver and returns Conflict with the "started" message.</summary>
    Task<BoardOpOutcome> MergePullRequestAsync(Ulid orgId, Ulid taskId, CancellationToken ct = default);

    /// <summary>Drafting → InQueue with the Submit button's pre-flight checks (repo assigned,
    /// a runnable provider).</summary>
    Task<BoardOpOutcome> SubmitTaskAsync(Ulid orgId, Ulid taskId, string? locale, CancellationToken ct = default);

    /// <summary>Tasks blocked on a human, most recently updated first (optionally one project).</summary>
    Task<IReadOnlyList<AttentionTaskSummary>> ListAttentionTasksAsync(
        Ulid orgId, Ulid? projectId, int take, CancellationToken ct = default);

    /// <summary>
    /// Creates (or returns, idempotently on <see cref="TrackerTaskSpec.ExternalRef"/>) the
    /// Drafting task for a tracker ticket, routed to its owner's runner.
    /// </summary>
    Task<ExternalTaskResult> CreateTrackerTaskAsync(TrackerTaskSpec spec, CancellationToken ct = default);

    /// <summary>
    /// The ticket's text changed. Before work starts the task's title and description are
    /// rewritten; once it has started they are left alone (an agent may be mid-plan on them) and
    /// false is returned so the caller can post the change as a note instead.
    /// </summary>
    Task<bool> TryRewriteTrackerTaskAsync(
        Ulid taskId, string title, string description, CancellationToken ct = default);

    /// <summary>
    /// The ticket left the source (unassigned, closed, moved out of the epic): archives the task
    /// when nothing has started, otherwise leaves it running — never kills a run, never opens
    /// anything without a human. Either way <paramref name="reason"/> is posted on the task,
    /// followed by what was done about it.
    /// </summary>
    Task<TrackerTaskRetirement> RetireTrackerTaskAsync(
        Ulid taskId, string reason, CancellationToken ct = default);

    /// <summary>Sets (or clears, with null) the card's "couldn't move the ticket" line.</summary>
    Task SetTrackerSyncErrorAsync(Ulid taskId, string? error, CancellationToken ct = default);

    /// <summary>Progress of the given tracker tasks, for status write-back. Missing ids are absent.</summary>
    Task<IReadOnlyList<TrackerTaskState>> GetTrackerTaskStatesAsync(
        IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default);

    /// <summary>A ticket that left the source came back (reassigned, reopened): an archived
    /// task returns to Drafting with <paramref name="note"/>; a live one just gets the note.</summary>
    Task ReviveTrackerTaskAsync(Ulid taskId, string note, CancellationToken ct = default);

    /// <summary>
    /// A tracker task routing could not place: posts a question offering
    /// <paramref name="repoNames"/> (with <paramref name="reason"/>) and parks the task in
    /// PendingQuestions. The answer sets the repository — two or more make it a cross-repo
    /// group — returns the task to Drafting and, when <paramref name="submitOnAnswer"/>, submits it.
    /// </summary>
    Task AskTrackerRepoAsync(
        Ulid taskId, IReadOnlyList<string> repoNames, string reason, bool submitOnAnswer, CancellationToken ct = default);

    // ── Stories (a tracker Story worked as ordered members on one shared branch) ──

    /// <summary>
    /// Creates (or returns, idempotently on the Story's key) the Story group and one member per
    /// sub-task and repository, in order: <c>StoryOrder</c> 1…n, <c>BranchName</c> = the shared
    /// branch, and a dependency from each member on the one before. Members run on the owner's
    /// runner.
    /// </summary>
    Task<TrackerStoryResult> CreateTrackerStoryAsync(TrackerStorySpec spec, CancellationToken ct = default);

    /// <summary>
    /// A sub-task the tracker added to a Story later: appended at the end of the chain. Before
    /// the Story's pull requests are ready it just joins; once they are ready (and still open)
    /// <c>ReadyAt</c> is cleared and <see cref="StoryAppendResult.WasReady"/> tells the caller to
    /// turn them back into drafts. A Story whose pull request merged or closed takes no more.
    /// </summary>
    Task<StoryAppendResult> AppendStoryMemberAsync(
        Ulid groupId, StorySubTaskSpec subTask, CancellationToken ct = default);

    /// <summary>The Story groups' progress, for status write-back. Missing ids are absent.</summary>
    Task<IReadOnlyList<TrackerGroupState>> GetTrackerGroupStatesAsync(
        IReadOnlyCollection<Ulid> groupIds, CancellationToken ct = default);

    /// <summary>Sub-tasks the last poll saw under the Story that have no member yet.</summary>
    Task SetStoryPendingChildrenAsync(Ulid groupId, int count, CancellationToken ct = default);

    /// <summary>The Story the task is a member of, with every member and branch; null otherwise.</summary>
    Task<StoryContext?> GetStoryForTaskAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>A Story by its group id; null when it is not a Story.</summary>
    Task<StoryContext?> GetStoryAsync(Ulid groupId, CancellationToken ct = default);

    /// <summary>Records (or creates) the repository's shared-branch row and its forge pull request.</summary>
    Task SetGroupBranchPullRequestAsync(
        Ulid groupId, Ulid repoId, string branchName, string? forgePrUrl, bool draft, CancellationToken ct = default);

    /// <summary>Marks the Story's pull requests ready (<paramref name="readyAt"/>) or back to
    /// draft (null) — the group and every repository row together.</summary>
    Task SetStoryReadyAsync(Ulid groupId, DateTimeOffset? readyAt, CancellationToken ct = default);

    /// <summary>The Story landed for good (squash-merged into a linked folder).</summary>
    Task SettleStoryAsync(Ulid groupId, CancellationToken ct = default);

    /// <summary>A Story pull request's forge state as the developer's CLI reported it.</summary>
    Task SetGroupBranchForgeStateAsync(Ulid groupId, Ulid repoId, string state, CancellationToken ct = default);

    /// <summary>A Story member's starting commit on the shared branch (see TaskEntity.ReviewBaseSha).
    /// Written once: a re-entered phase keeps the first value.</summary>
    Task SetTaskReviewBaseShaAsync(Ulid taskId, string sha, CancellationToken ct = default);

    Task<ExternalTaskResult> CreateExternalTaskAsync(
        Ulid orgId, Ulid projectId, Ulid repoId, string title, string description,
        IReadOnlyList<string> tags, string externalRef, CancellationToken ct = default);

    /// <summary>
    /// Creates Drafting-lane tasks extracted from a PM agent chat message. Reserves task numbers
    /// and inserts in one transaction, then broadcasts TaskCreated board events (best effort).
    /// The orchestrator skips the Ready-lane PM review for these (SourceMessageId != null —
    /// the PM already refined them in chat) but they still go through dev planning.
    /// Returns the created tasks in insertion order.
    /// </summary>
    Task<IReadOnlyList<CreatedChatTask>> CreateChatTasksAsync(
        Ulid orgId, Ulid projectId, Ulid sourceMessageId,
        string? createdByUserId, string? requesterLocale,
        IReadOnlyList<ChatTaskSpec> specs, CancellationToken ct = default);

    /// <summary>The project's milestones, for agent chat to list and plan into.</summary>
    Task<IReadOnlyList<ChatMilestoneRef>> ListMilestonesForChatAsync(
        Ulid orgId, Ulid projectId, CancellationToken ct = default);

    /// <summary>
    /// Creates a milestone from agent chat. Returns the existing one when the title already
    /// matches — an agent asked twice in one conversation should not produce two roadmap rows
    /// with the same name.
    /// </summary>
    Task<ChatMilestoneRef> CreateMilestoneForChatAsync(
        Ulid orgId, Ulid projectId, string title, string? outcome,
        string? createdByUserId, CancellationToken ct = default);

    /// <summary>Tasks created from the given agent chat messages, ordered by task number.
    /// Scoped to a project — the message-id match happens in memory, never as a Contains over
    /// the nullable, converter-mapped SourceMessageId column.</summary>
    Task<IReadOnlyList<ChatTaskLink>> GetChatCreatedTasksAsync(
        Ulid projectId, IReadOnlyCollection<Ulid> sourceMessageIds, CancellationToken ct = default);

    /// <summary>Titles for the given task ids, for display alongside a task reference held by
    /// another module (a document revision's "written by task X"). Unknown ids are omitted —
    /// the caller renders the reference without a name rather than failing.</summary>
    Task<IReadOnlyDictionary<Ulid, string>> GetTaskTitlesAsync(
        IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default);

    /// <summary>Display refs (project, number, title) for task ids held by another module —
    /// the billing breakdown's task rows, which need enough to render a clickable
    /// "/p/{project}/tasks/{number}" link, not just a name. Unknown ids are omitted, same
    /// contract as <see cref="GetTaskTitlesAsync"/>.</summary>
    Task<IReadOnlyDictionary<Ulid, TaskDisplayRef>> GetTaskRefsAsync(
        IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default);

    /// <summary>Non-archived tasks in the project, newest first, capped at
    /// <paramref name="limit"/>. Callable from scopes without an ICurrentUser (agent chat
    /// tool handlers), so org/project are filtered explicitly — those arguments are the
    /// isolation boundary.</summary>
    Task<IReadOnlyList<ProjectTaskSummary>> ListProjectTaskSummariesAsync(
        Ulid orgId, Ulid projectId, int limit = 200, CancellationToken ct = default);

    /// <summary>Non-archived tasks the agent ran a phase execution on — or is assigned
    /// to / currently working — most recently worked first. Callable from scopes without
    /// an ICurrentUser (agent chat tool handlers), so org/project are filtered explicitly —
    /// those arguments are the isolation boundary.</summary>
    Task<IReadOnlyList<AgentTaskSummary>> ListAgentTaskSummariesAsync(
        Ulid orgId, Ulid projectId, Ulid agentId, int limit = 100, CancellationToken ct = default);

    /// <summary>The task's phase executions, oldest first — or null when the task does not
    /// exist in the given org/project (callers distinguish "unknown task" from "no runs
    /// yet"). Callable from scopes without an ICurrentUser (agent chat tool handlers), so
    /// org/project are filtered explicitly — those arguments are the isolation boundary.</summary>
    Task<IReadOnlyList<TaskExecutionSummary>?> ListTaskExecutionSummariesAsync(
        Ulid orgId, Ulid projectId, Ulid taskId, CancellationToken ct = default);

    // ─────────────────────────────────────────────────────────────────────────
    // Worker / Execution-module surface (P10). Reads return slim snapshots;
    // writes are phase-shaped and each maps to ONE module-side SaveChanges so
    // previously-atomic persistence units stay atomic.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Full scalar snapshot of a task, or null when it does not exist.</summary>
    Task<TaskSnapshot?> GetTaskSnapshotAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>User-uploaded attachments of a task, oldest first, for agent-prompt injection.
    /// Attachments whose stored URL cannot be mapped back to a blob name are omitted.</summary>
    Task<IReadOnlyList<TaskAttachmentInfo>> ListTaskAttachmentsAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>
    /// Non-archived InQueue tasks in pickup order: priority desc, queue position, created.
    /// </summary>
    Task<IReadOnlyList<QueuedTaskInfo>> ListQueuedTasksAsync(CancellationToken ct = default);

    /// <summary>
    /// Live interactive sessions with no worker holding them: either never picked up, or picked
    /// up by a worker that has since died (expired lease).
    ///
    /// Deliberately NOT part of <see cref="ListQueuedTasksAsync"/>, which excludes interactive
    /// tasks so the autonomous sweep can never claim one. A session is Active from the moment it
    /// opens and is driven by its human, so "needs a worker" is a different question from "is
    /// waiting in the queue" — and the same predicate doubles as the recovery path when a
    /// session's worker dies mid-conversation.
    /// </summary>
    Task<IReadOnlyList<InteractiveSessionInfo>> ListInteractiveSessionsAwaitingPickupAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Human messages in an interactive session that no turn has answered yet — user-role
    /// messages created after <paramref name="answeredThrough"/>, oldest first.
    ///
    /// Carries timestamps, unlike <see cref="GetConversationMessagesAsync"/>, because the turn
    /// must stamp the newest message it actually consumed rather than the time it finished.
    /// </summary>
    Task<IReadOnlyList<InteractiveUserMessage>> ListUnansweredInteractiveMessagesAsync(
        Ulid taskId, DateTimeOffset? answeredThrough, CancellationToken ct = default);

    /// <summary>Advances the answered-through watermark after a turn consumed messages up to
    /// <paramref name="answeredThrough"/>. Never moves it backwards.</summary>
    Task SetInteractiveAnsweredThroughAsync(
        Ulid taskId, DateTimeOffset answeredThrough, CancellationToken ct = default);

    /// <summary>Ends a live session: clears the session marker, any finish request, and the
    /// agent-working flag. Idempotent — the loop calls it on every exit path.</summary>
    Task CloseInteractiveSessionAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>
    /// Names an unnamed session from what the human actually asked for. Applies only while the
    /// title is still <see cref="InteractiveSessionDefaults.UntitledSession"/>, so a title the
    /// user typed — or one an earlier turn already generated — is never overwritten. Returns
    /// false when it did not apply.
    /// </summary>
    Task<bool> TrySetGeneratedSessionTitleAsync(
        Ulid taskId, string title, CancellationToken ct = default);

    /// <summary>Ids among <paramref name="taskIds"/> that have a dependency on a non-Completed task.</summary>
    Task<IReadOnlySet<Ulid>> GetTasksWithUnmetDependenciesAsync(
        IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default);

    /// <summary>The given tasks whose milestone still waits on another that has not shipped —
    /// whatever lane they are in. The queue counts those it holds at Developing as banked
    /// plans, and never gives one a build slot.</summary>
    Task<IReadOnlySet<Ulid>> GetMilestoneHeldTaskIdsAsync(
        IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default);

    /// <summary>True when this task's milestone still waits on another that has not shipped.</summary>
    Task<bool> IsTaskMilestoneHeldAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>
    /// Active, non-archived tasks in the project, for concurrency-slot accounting.
    ///
    /// Excludes interactive sessions: MaxParallelTasks budgets autonomous work, and a
    /// human-paced session that spends its life waiting for someone to type should never be
    /// holding a slot the pipeline could use.
    /// </summary>
    Task<IReadOnlyList<ActiveTaskInfo>> ListActiveTasksAsync(Ulid projectId, CancellationToken ct = default);

    /// <summary>
    /// Tasks that were mid-phase when their worker died: HasAgentWorking, Active,
    /// current lane within <paramref name="lanes"/>, not archived, and whose
    /// orchestration lease is absent or expired (a live lease means another worker
    /// is still running them). Resume requires a successful
    /// <see cref="TryAcquireTaskLeaseAsync"/> — this listing alone grants nothing.
    /// </summary>
    Task<IReadOnlyList<ResumableTaskInfo>> ListResumableTasksAsync(
        IReadOnlyCollection<Lane> lanes, CancellationToken ct = default);

    /// <summary>Tasks in CodeReview/Done with a branch and repo but no PR, not archived.</summary>
    Task<IReadOnlyList<PrCandidateTask>> ListTasksAwaitingPrAsync(CancellationToken ct = default);

    /// <summary>Done tasks with an unmerged PR and a repo, oldest completed first.</summary>
    Task<IReadOnlyList<MergeCandidateTask>> ListTasksAwaitingMergeAsync(CancellationToken ct = default);

    /// <summary>
    /// Conversation for prompts: all non-agent messages plus agent questions, oldest first.
    /// <paramref name="since"/> keeps only messages created after it (resumed runs already
    /// hold the older conversation in their CLI session transcript).
    /// </summary>
    /// <param name="until">Exclusive upper bound. Interactive turns use it to stop history right
    /// where the messages they deliver as authoritative begin — otherwise the same text appears
    /// twice, once inside the untrusted-content fence, and the agent refuses its own operator's
    /// instruction as a suspected injection.</param>
    Task<IReadOnlyList<ConversationMessage>> GetConversationMessagesAsync(
        Ulid taskId, DateTimeOffset? since = null, CancellationToken ct = default,
        DateTimeOffset? until = null);

    /// <summary>
    /// Session id + end time of the task's most recent finished execution in
    /// <paramref name="lane"/> that reported a resumable CLI session, or null.
    ///
    /// Failed executions count. A run killed mid-flight (transport drop, runner
    /// disconnect, worker restart) is exactly the one whose transcript is worth resuming:
    /// re-entering the phase cold would replay the whole prompt and redo work the agent
    /// has already done. The returned <see cref="ExecutionSessionInfo.Interrupted"/> says
    /// which case this is so the caller can frame the resume correctly.
    /// </summary>
    Task<ExecutionSessionInfo?> GetLatestExecutionSessionAsync(
        Ulid taskId, Lane lane, CancellationToken ct = default);

    /// <summary>Latest approved plan (highest version) with camelCase-serialized content, or null.</summary>
    Task<ApprovedPlanInfo?> GetLatestApprovedPlanAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>
    /// Resolves a task from a branch name, scoped to the given repos: the orkka/task-{ulid}
    /// convention first (verified against the repo set — branch names are repo-author-chosen
    /// content, so an unscoped match would let one repo's branch complete another org's task),
    /// then the newest task whose persisted branch name matches (friendly orkka/{KEY}-{n}
    /// branches, which are not globally unique).
    /// </summary>
    Task<Ulid?> FindTaskIdByBranchAsync(
        string branchName, IReadOnlyCollection<Ulid> repoIds, CancellationToken ct = default);

    /// <summary>
    /// Open-PR tasks still mid-pipeline (not Done, never completed, no armed resolution) —
    /// the set the merge sweep does not watch. Reconciled against the forge as the webhook's
    /// backstop, so a PR merged or closed directly on GitHub always reaches its task.
    /// </summary>
    Task<IReadOnlyList<MergeCandidateTask>> ListTasksForForgeReconciliationAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Records the conflict snapshot (files + plain-language summary, JSON) shown on the
    /// merge page. Skipped when unchanged, when the task is already merged, or while a
    /// human-picked resolution is pending (their per-file choices live in the snapshot).
    /// </summary>
    Task SetTaskMergeConflictsAsync(Ulid taskId, string conflictsJson, CancellationToken ct = default);

    /// <summary>
    /// Ends the resolution attempt that was holding this task. When the user queued a
    /// follow-up while it ran, and the task still hasn't merged, that follow-up becomes the
    /// active request instead of being cleared — the merge sweep starts it on its next cycle.
    /// Returns the strategy promoted, or null when the task simply came free.
    /// </summary>
    Task<string?> ClearTaskMergeResolutionAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>
    /// Stamps when the current attempt began, if it isn't stamped already. The armed paths
    /// stamp themselves; this is for the automatic pass, which arms no strategy and is
    /// recognisable only by the lease it holds.
    /// </summary>
    Task MarkMergeResolutionStartedAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>
    /// Optimistically claims an InQueue task (InQueue → Active, HasAgentWorking = true) and
    /// takes its orchestration lease, via a set-based conditional update. Returns false when
    /// another processor already claimed it.
    /// </summary>
    Task<bool> TryClaimTaskAsync(
        Ulid taskId, string leaseOwner, TimeSpan leaseDuration, CancellationToken ct = default);

    /// <summary>
    /// Submits a Drafting task on autopilot's behalf (Drafting → InQueue), stamping the session
    /// that owns it. Performs the identical submission the API endpoint does — dependency gate,
    /// queue position, milestone start, board broadcast, background edge analysis.
    ///
    /// Guarded like <see cref="TryClaimTaskAsync"/>: returns false when the task was no longer
    /// in Drafting, which for a sweep running on several replicas means another one got there
    /// first. That is an ordinary outcome, not an error.
    /// </summary>
    Task<bool> TryAutoSubmitAsync(Ulid taskId, Ulid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Whether anything in a DIFFERENT repo depends on this task — i.e. whether extracting its
    /// API surface would ever be read. The guard that keeps the extraction call proportional to
    /// actual need rather than running on every backend task in the project.
    /// </summary>
    Task<bool> HasCrossRepoDependentsAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>Stores the interface a task exposes to other repos (markdown, or null to clear).</summary>
    Task SetApiSurfaceAsync(Ulid taskId, string? apiSurface, CancellationToken ct = default);

    /// <summary>
    /// The cross-repo group this task belongs to, or null when it stands alone.
    ///
    /// Distinct from <see cref="ListBlockerApiContractsAsync"/> and read even when the task has
    /// no blockers at all: the group's contract binds every member, including the one that
    /// DEFINES the interface and therefore waits on nobody. Handing it only to the callers is
    /// how the provider ends up free to ship something the contract does not describe.
    /// </summary>
    Task<TaskGroupContract?> GetGroupContractAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>
    /// Merge readiness for the given groups, for the merge poller's gate. Groups that no longer
    /// exist are simply absent from the result.
    /// </summary>
    Task<IReadOnlyList<TaskGroupMergeState>> GetGroupMergeStatesAsync(
        IReadOnlyCollection<Ulid> groupIds, CancellationToken ct = default);

    /// <summary>
    /// Recomputes a group's status from its members after one of them merged: Merged once they
    /// all have, PartiallyMerged once some have and another has failed.
    ///
    /// PartiallyMerged is not an error path to be avoided — it is the honest name for a state
    /// that is genuinely reachable, because a merged PR cannot be un-merged and two forges
    /// cannot be committed atomically.
    /// </summary>
    Task RefreshGroupMergeStatusAsync(Ulid groupId, CancellationToken ct = default);

    /// <summary>
    /// Records an agent's ask to add a repository to its cross-repo task. Returns what to tell
    /// the agent — including when the ask was a duplicate, so a re-entered phase does not think
    /// its request vanished.
    ///
    /// Records only. Creating the sibling is a human decision: it widens the task's blast radius
    /// and its bill, and an agent that guessed wrong would file work in a repository nobody
    /// wanted touched.
    /// </summary>
    Task<string> RequestGroupRepoAsync(
        Ulid taskId, string repoName, string reason, CancellationToken ct = default);

    /// <summary>Appends what a member actually shipped to its group's contract, under an
    /// "As built" heading. Never replaces the agreed text — see the append's own comment.</summary>
    Task AppendGroupAsBuiltAsync(
        Ulid groupId, int taskNumber, string taskTitle, string apiSurface,
        CancellationToken ct = default);

    /// <summary>
    /// API surfaces published by the tasks this one waits on, from other repositories only.
    ///
    /// Same-repo blockers are excluded deliberately: their code is already in this task's
    /// worktree, where the agent can simply read it. The gap this closes is the cross-repo one,
    /// where merging the blocker put nothing on the dependent's disk.
    /// </summary>
    Task<IReadOnlyList<BlockerApiContract>> ListBlockerApiContractsAsync(
        Ulid taskId, CancellationToken ct = default);

    /// <summary>
    /// Tasks whose work has settled and whose documentation pass has not run yet: Done and
    /// Completed, and — when the task touched a repo — actually merged. Waiting for the merge is
    /// the point: a PR closed without merging must never leave the knowledge base describing
    /// code that was never shipped.
    ///
    /// <paramref name="completedSince"/> bounds the sweep to recent work. Without it, the first
    /// poll after a project enables documentation would fire a model call for every task it has
    /// ever finished.
    ///
    /// <paramref name="runningStaleBefore"/> reclaims passes left mid-flight by a worker that
    /// died: nothing else ever moves a row out of Running, so without this a crash would hide
    /// the task from documentation permanently. Attempts are counted at claim time, so a task
    /// that repeatedly kills its worker still runs out of retries.
    /// </summary>
    Task<IReadOnlyList<DocSyncCandidateTask>> ListTasksAwaitingDocSyncAsync(
        DateTimeOffset completedSince, int maxAttempts, DateTimeOffset runningStaleBefore,
        CancellationToken ct = default);

    /// <summary>Atomically claims a task's documentation pass (eligible → Running, attempts
    /// incremented). False = another replica claimed it first, or it is no longer eligible.
    /// <paramref name="runningStaleBefore"/> matches <see cref="ListTasksAwaitingDocSyncAsync"/>
    /// so the listing and the claim agree on what "abandoned" means.</summary>
    Task<bool> TryClaimDocSyncAsync(
        Ulid taskId, DateTimeOffset runningStaleBefore, CancellationToken ct = default);

    /// <summary>What the task's dev runs changed — paths and counts — from the code-change
    /// snapshots. Null when the task never produced one (no repo work).</summary>
    Task<TaskChangeSummary?> GetTaskChangeSummaryAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>Records how the documentation pass ended. Never touches the task's own lane or
    /// status — documentation is an observer of finished work, not a stage of it.</summary>
    Task CompleteDocSyncAsync(Ulid taskId, DocSyncStatus status, CancellationToken ct = default);

    /// <summary>
    /// Done tasks that have stood there since before <paramref name="completedBefore"/> and are
    /// not archived yet — the auto-archive sweep's candidate set, across every org. Ordered
    /// oldest-completed first and capped at <paramref name="limit"/>, so a deployment turning
    /// the setting on for a long-lived project drains its backlog over several ticks instead of
    /// rewriting thousands of rows in one statement.
    ///
    /// Only SETTLED work is offered. A Done task whose repo work has not landed is still
    /// waiting on the merge poller, and that poller skips archived tasks — archiving one would
    /// strand its PR forever. Repo-less tasks, merged tasks, and tasks whose PR was closed
    /// without merging have nothing left to wait for.
    ///
    /// Whether a candidate is actually archived is the caller's decision: the per-project
    /// setting that authorises it lives in the Projects module.
    /// </summary>
    Task<IReadOnlyList<AutoArchiveCandidateTask>> ListTasksAwaitingAutoArchiveAsync(
        DateTimeOffset completedBefore, int limit, CancellationToken ct = default);

    /// <summary>
    /// Archives the given tasks the way the Archive endpoint does (IsArchived, ArchivedAt,
    /// Status = Archived) and returns the ones this call actually moved. Re-checks eligibility,
    /// so a task archived or reopened between the listing and this call is skipped rather than
    /// clobbered — which is also what keeps two worker replicas sweeping at once harmless.
    /// </summary>
    Task<IReadOnlyList<AutoArchiveCandidateTask>> ArchiveTasksAsync(
        IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default);

    /// <summary>
    /// Approves the task's pending plan without a human and re-queues it for Developing —
    /// the same transition the plan-review endpoint performs on an approval.
    ///
    /// Deliberately re-queues rather than continuing inline: the task goes back through the
    /// queue so the project's <c>MaxParallelTasks</c> still bounds it. Running the developing
    /// phase straight from the planning phase would let autopilot exceed the concurrency the
    /// user configured, on every task it submits.
    ///
    /// Returns false when there was no pending plan to approve.
    /// </summary>
    Task<bool> AutoApprovePlanAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>
    /// Opens a session. Returns null when one is already open for the project — the filtered
    /// unique index makes that a race the caller loses, not a state to reconcile.
    /// </summary>
    /// <param name="capUsd">Cloud spend ceiling for this session. Null = uncapped; the caller
    /// is responsible for having checked that the user may choose that.</param>
    /// <param name="answerEveryQuestion">Answer every question the session's tasks ask, guarded
    /// topics included. The caller is responsible for having checked ProjectsManage.</param>
    Task<Ulid?> StartAutopilotSessionAsync(
        Ulid orgId, Ulid projectId, string startedByUserId, decimal? capUsd,
        bool answerEveryQuestion, CancellationToken ct = default);

    /// <summary>
    /// Autopilot's state for a project, as the board renders it. Falls back to the most recent
    /// closed session when none is open, so the morning view can say how the night ended.
    /// </summary>
    Task<AutopilotStatus> GetAutopilotStatusAsync(
        Ulid orgId, Ulid projectId, CancellationToken ct = default);

    /// <summary>Every open autopilot session, across orgs — the Worker sweep's work list.</summary>
    Task<IReadOnlyList<OpenAutopilotSession>> ListOpenAutopilotSessionsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Stopped sessions whose tasks are still moving (stopping deliberately lets in-flight
    /// work finish) or have moved since the session row was last written. The sweep keeps
    /// refreshing their spend/counters — otherwise every dollar spent after the stop is
    /// billed but never shows in the session, and "Spent" reads permanently low.
    /// </summary>
    Task<IReadOnlyList<OpenAutopilotSession>> ListDrainingAutopilotSessionsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Drafting tasks this session may submit, best first: milestone order, then priority, then
    /// task number. Milestone order leads because the roadmap is the user's stated intent, and
    /// autopilot ignoring it would look broken.
    ///
    /// Excludes tasks with no repo (unsubmittable), archived tasks, and tasks awaiting reset.
    /// Dependencies are NOT filtered here — the queue processor already defers a task whose
    /// blockers are unfinished, and re-implementing that rule in a second place is how the two
    /// drift apart.
    /// </summary>
    Task<IReadOnlyList<AutopilotCandidate>> ListAutopilotCandidatesAsync(
        Ulid orgId, Ulid projectId, int limit, CancellationToken ct = default);

    /// <summary>How many of the session's tasks are queued or running, parked, or finished.</summary>
    Task<AutopilotProgress> GetAutopilotProgressAsync(Ulid sessionId, CancellationToken ct = default);

    /// <summary>True while any milestone of the project is still landing: a task under a
    /// milestone that can still move on its own (running, queued, or parked for its owner's
    /// runner), a finished task whose PR has not merged, or a milestone whose ship is pending
    /// or in progress. A Drafting lane autopilot cannot submit is then WAITING on that
    /// milestone, not drained.</summary>
    Task<bool> HasMilestoneWorkInFlightAsync(Ulid projectId, CancellationToken ct = default);

    /// <summary>Ids of every task this session submitted — the set Billing prices to get the
    /// session's spend. Cloud-only falls out for free: a UserRunner task writes no cost rows.</summary>
    Task<IReadOnlyList<Ulid>> ListAutopilotSessionTaskIdsAsync(
        Ulid sessionId, CancellationToken ct = default);

    /// <summary>Writes the session's counters after a sweep. Bumps UpdatedAt only when something
    /// actually changed, so the drained check's quiet-period clock stays meaningful.</summary>
    Task UpdateAutopilotSessionAsync(
        Ulid sessionId, decimal spentUsd, int tasksSubmitted, int tasksCompleted,
        int tasksFailed, int consecutiveFailures, CancellationToken ct = default);

    /// <summary>Closes a session. Returns false when another replica closed it first — the sweep
    /// treats that as an ordinary race, not an error.</summary>
    Task<bool> StopAutopilotSessionAsync(
        Ulid sessionId, AutopilotStopReason reason, CancellationToken ct = default);

    /// <summary>
    /// Atomically takes the orchestration lease on a task whose lease is absent, expired, or
    /// already held by <paramref name="leaseOwner"/>. Returns false when another worker holds
    /// a live lease. Used before resuming a mid-phase task.
    /// </summary>
    Task<bool> TryAcquireTaskLeaseAsync(
        Ulid taskId, string leaseOwner, TimeSpan leaseDuration, CancellationToken ct = default);

    /// <summary>Heartbeat: extends the lease on the given tasks where held by <paramref name="leaseOwner"/>.</summary>
    Task ExtendTaskLeasesAsync(
        IReadOnlyCollection<Ulid> taskIds, string leaseOwner, TimeSpan leaseDuration,
        CancellationToken ct = default);

    /// <summary>Clears the lease when orchestration ends, only if still held by <paramref name="leaseOwner"/>.</summary>
    Task ReleaseTaskLeaseAsync(Ulid taskId, string leaseOwner, CancellationToken ct = default);

    /// <summary>Set-based InQueue → OutOfTokens for the given tasks (org budget exhausted).</summary>
    Task MarkTasksOutOfTokensAsync(IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default);

    /// <summary>Set-based InQueue → WaitingForRunner (the creator's runner is offline).</summary>
    Task MarkTasksWaitingForRunnerAsync(IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default);

    /// <summary>WaitingForRunner → InQueue for a user's parked tasks (their runner
    /// reconnected). Returns the number of tasks re-queued.</summary>
    Task<int> RequeueWaitingForRunnerTasksAsync(
        Ulid orgId, string runnerUserId, CancellationToken ct = default);

    /// <summary>WaitingForRunner → InQueue for EVERY parked task, whoever owns it.
    /// Orkka Local only: one machine, one runner, and its tasks carry no RunnerUserId
    /// to filter on. Never call this from a multi-tenant host — it would unpark tasks
    /// belonging to runners that are still offline.</summary>
    Task<int> RequeueAllWaitingForRunnerTasksAsync(CancellationToken ct = default);

    /// <summary>
    /// Blocked tasks the auto-recovery sweep may re-queue: not archived, no reset pending, not
    /// interactive, fewer than <paramref name="maxRounds"/> automatic rounds spent, no live
    /// lease, and a failed execution that ended at or before <paramref name="failedBefore"/> —
    /// the backoff that keeps a live provider incident from being hammered. The error text
    /// rides along unclassified; the caller decides what counts as transient.
    /// </summary>
    Task<IReadOnlyList<AutoRecoverableBlockedTask>> ListAutoRecoverableBlockedTasksAsync(
        DateTimeOffset failedBefore, int maxRounds, CancellationToken ct = default);

    /// <summary>
    /// Blocked → InQueue in the task's current lane (the same reset the Retry endpoint
    /// performs), spending one automatic recovery round. The round counter is persisted on the
    /// task — an in-memory count restarts on every deploy and would hand each new pod a fresh
    /// budget. Returns false when the task is no longer eligible (un-blocked, archived, reset
    /// pending, or out of rounds), which two replicas racing resolve as one winner.
    /// </summary>
    Task<bool> TryAutoRecoverBlockedTaskAsync(
        Ulid taskId, int maxRounds, CancellationToken ct = default);

    /// <summary>Fails the execution but parks the task WaitingForRunner instead of
    /// blocking — mid-run runner disconnect; the reconnect sweep re-runs the phase.
    /// <paramref name="sessionId"/> is the dying run's CLI session, kept so that re-run
    /// resumes the transcript instead of starting the phase over.</summary>
    Task<ExecutionSnapshot?> ParkExecutionWaitingForRunnerAsync(
        Ulid taskId, Ulid executionId, string reason,
        int promptTokens = 0, int completionTokens = 0, string? sessionId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Starts a phase execution: Status = Active, CurrentAgentId = <paramref name="agentId"/>,
    /// optionally CurrentLane = <paramref name="lane"/> and StartedAt ??= now, plus a new
    /// Running TaskExecution row — all in one save. Null when the task does not exist.
    /// </summary>
    Task<ExecutionStart?> StartExecutionAsync(
        Ulid taskId, Lane lane, Ulid? agentId,
        bool moveTaskToLane, bool setStartedAt, CancellationToken ct = default);

    /// <summary>
    /// Marks the execution Completed with token usage (and optional agent log) and adds the
    /// tokens to the task's TotalTokensUsed — one save. Null when task/execution is missing.
    /// <paramref name="sessionId"/> is the run's resumable CLI session, when it reported one.
    /// </summary>
    Task<ExecutionSnapshot?> CompleteExecutionAsync(
        Ulid taskId, Ulid executionId, int promptTokens, int completionTokens,
        string? agentLog = null, string? sessionId = null, CancellationToken ct = default);

    /// <summary>
    /// Permanently records the code a dev run changed: parses the unified diff into per-file
    /// stats, inserts a code-change snapshot, and fills the producing execution's FilesChanged —
    /// one save. Skips silently when the task's newest snapshot has an identical diff hash, or
    /// (when <paramref name="onlyIfMissing"/>) when any snapshot already exists for this branch.
    /// </summary>
    Task SaveCodeChangeSnapshotAsync(
        Ulid taskId, Ulid? executionId, Ulid repoId, Lane lane,
        string branchName, string baseBranch, string diff, bool isTruncated,
        bool onlyIfMissing = false, CancellationToken ct = default);

    /// <summary>
    /// Stores the working-tree diff of a run in progress as the task's single <em>live</em>
    /// snapshot (insert on first capture, overwrite after) so a Code View opened mid-run — or
    /// reloaded — paints the current picture at once. Returns null when nothing changed since
    /// the last capture (same diff hash) or the task is missing; otherwise the event to
    /// broadcast. Replaced by the permanent snapshot the phase saves when it ends.
    /// </summary>
    Task<WorkspaceChangedEvent?> UpsertLiveCodeChangeSnapshotAsync(
        Ulid taskId, Ulid? executionId, Ulid repoId, Lane lane,
        string branchName, string baseBranch, string diff, bool isTruncated,
        CancellationToken ct = default);

    /// <summary>
    /// Persists a new PendingReview plan (next version number) AND completes the producing
    /// execution (tokens, task counters) in one save — the planning phase's atomic unit.
    /// </summary>
    Task<PlanSaveResult?> SavePlanAndCompleteExecutionAsync(
        Ulid taskId, Ulid executionId, int promptTokens, int completionTokens,
        string planContentJson, CancellationToken ct = default);

    /// <summary>
    /// Marks the execution Failed (error, CompletedAt, any burned tokens), adds tokens to the
    /// task, and sets the task Blocked with HasAgentWorking = false — one save.
    /// <paramref name="sessionId"/> is the dying run's CLI session, kept so that a retry
    /// resumes the transcript instead of starting the phase over.
    /// </summary>
    Task<ExecutionSnapshot?> FailExecutionAsync(
        Ulid taskId, Ulid executionId, string error,
        int promptTokens = 0, int completionTokens = 0, string? sessionId = null,
        CancellationToken ct = default);

    /// <summary>Marks only the execution row Failed (task state handled separately).</summary>
    Task MarkExecutionFailedAsync(
        Ulid taskId, Ulid executionId, string error, CancellationToken ct = default);

    /// <summary>Appends a task message. Null when the task does not exist.</summary>
    Task<TaskMessageSnapshot?> AddTaskMessageAsync(
        Ulid taskId, string role, string content, MessageType messageType,
        string? metadataJson = null, CancellationToken ct = default);

    /// <summary>
    /// Moves the task to PendingQuestions (WaitingAnswer, QuestionSourceLane recorded,
    /// agent slot freed). Returns the lane the task came from, or null when missing.
    /// </summary>
    Task<Lane?> MoveTaskToPendingQuestionsAsync(
        Ulid taskId, Lane sourceLane, CancellationToken ct = default);

    /// <summary>
    /// Re-queues the task in the given lane with the agent slot freed — the counterpart of
    /// <see cref="MoveTaskToPendingQuestionsAsync"/> for a question answered without parking
    /// (autopilot auto-answer): the queue re-claims the task and re-runs the phase with the
    /// answer in its conversation. Returns false when the task is missing.
    /// </summary>
    Task<bool> RequeueTaskForAnswerAsync(Ulid taskId, Lane lane, CancellationToken ct = default);

    /// <summary>
    /// How many autopilot auto-answers this task has already consumed (messages stamped with
    /// autoAnswered metadata) — the loop guard that stops an agent re-asking forever.
    /// </summary>
    Task<int> CountAutoAnsweredMessagesAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>
    /// Applies the PM review outcome: optional title/description replacement plus the PM's
    /// phase-summary message, in one save (the PM phase's atomic unit).
    /// <paramref name="recommendedModel"/> is the advisory cheaper-model suggestion from the
    /// PM's complexity grade — stored on the task for a human to apply or dismiss, never
    /// applied by the platform.
    /// </summary>
    Task<TaskMessageSnapshot?> ApplyPmReviewAsync(
        Ulid taskId, string? improvedTitle, string? improvedDescription,
        string summaryMessage, string? metadataJson, string? recommendedModel = null,
        CancellationToken ct = default);

    /// <summary>PlanReady + WaitingPlanApproval, agent slot freed (await human plan review).</summary>
    Task MoveTaskToPlanReadyAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>Persists the task's git branch name (idempotent; set during workspace setup).</summary>
    Task SetTaskBranchAsync(Ulid taskId, string branchName, CancellationToken ct = default);

    /// <summary>Records the branch the task's worktree forked from (see TaskEntity.BaseBranch).</summary>
    Task SetTaskBaseBranchAsync(Ulid taskId, string baseBranch, CancellationToken ct = default);

    /// <summary>Records why this task's worktree cannot run the app (the repo's setup
    /// command failed), or clears it with null once setup succeeds. Persisted so the person
    /// who opens the worktree sees it without scrolling the activity feed.</summary>
    Task SetTaskSetupFailureAsync(Ulid taskId, string? reason, CancellationToken ct = default);

    /// <summary>Persists the task's pull-request URL.</summary>
    Task SetTaskPrUrlAsync(Ulid taskId, string prUrl, CancellationToken ct = default);

    /// <summary>Records the forge pull request the developer's runner opened when the task's
    /// reviewed work was published.</summary>
    Task SetTaskPublishedPrUrlAsync(Ulid taskId, string url, CancellationToken ct = default);

    /// <summary>
    /// Finishes the developing phase: Testing/InQueue when <paramref name="routeToTesting"/>
    /// (QA lane enabled), otherwise Testing/WaitingUserVerification when
    /// <paramref name="parkForUserVerification"/> (requester tests manually), otherwise
    /// SkipCodeReview → Done/Completed (CompletedAt = now), otherwise CodeReview/InQueue;
    /// agent slot freed either way — one save.
    /// </summary>
    Task<DevelopingTransition?> CompleteDevelopingPhaseAsync(
        Ulid taskId, bool skipCodeReview, bool routeToTesting = false,
        bool parkForUserVerification = false, CancellationToken ct = default);

    /// <summary>WaitingCodeReview with the agent slot freed (human review takes over).</summary>
    /// <summary>Parks the task for a human reviewer. Returns false, writing nothing, when
    /// the task was already settled (merged, or carried to Done) while the agent ran — a
    /// late verdict must not resurrect finished work into a waiting state nobody can
    /// clear.</summary>
    Task<bool> SetTaskWaitingCodeReviewAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>WaitingQaReview with the agent slot freed (human takes over the QA outcome).</summary>
    /// <summary>QA counterpart of <see cref="SetTaskWaitingCodeReviewAsync"/>, with the
    /// same already-settled guard and the same false-means-nothing-written contract.</summary>
    Task<bool> SetTaskWaitingQaReviewAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>
    /// Parks the task in Testing/WaitingUserVerification with the agent slot freed — the
    /// requester tests the change manually, then approves or sends feedback to resume.
    /// </summary>
    Task SetTaskWaitingUserVerificationAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>
    /// Applies a QA verdict: pass → CodeReview/InQueue (or Testing/WaitingUserVerification
    /// when <paramref name="parkForUserVerification"/>); fail increments QaCycleCount and
    /// either re-queues for Developing or escalates to human (WaitingQaReview) once
    /// <paramref name="maxQaCycles"/> is reached — one save.
    /// </summary>
    Task<QaTransition?> ApplyQaVerdictAsync(
        Ulid taskId, bool passed, int maxQaCycles,
        bool parkForUserVerification = false, CancellationToken ct = default);

    /// <summary>
    /// Applies a failed automated check (the repo's CheckCommand — lint/typecheck/tests —
    /// run after Developing): increments CheckCycleCount and either re-queues for Developing
    /// (the check output goes into the conversation for the dev agent) or, once
    /// <paramref name="maxCheckCycles"/> is reached, parks the task as
    /// Developing/WaitingUserVerification — the status a chat reply resumes, like
    /// <see cref="ApplyNoCommitOutcomeAsync"/>. One save. A passing check needs no call here;
    /// the caller proceeds with the normal post-Developing transition.
    /// </summary>
    Task<CheckTransition?> ApplyCheckFailureAsync(
        Ulid taskId, int maxCheckCycles, CancellationToken ct = default);

    /// <summary>
    /// Applies a reviewer verdict: approve → Done/Completed; request-changes increments
    /// ReviewCycleCount and either re-queues for Developing or escalates to human review
    /// once <paramref name="maxReviewCycles"/> is reached — one save.
    /// </summary>
    Task<CodeReviewTransition?> ApplyCodeReviewVerdictAsync(
        Ulid taskId, bool approved, int maxReviewCycles, CancellationToken ct = default);

    /// <summary>
    /// Sends a task back to Developing to reconcile a base branch that no longer merges
    /// cleanly — increments BaseSyncCycleCount and either re-queues (Developing/InQueue) or,
    /// once <paramref name="maxAttempts"/> is reached, parks it as WaitingCodeReview with
    /// <paramref name="reason"/> on the merge page. One save.
    ///
    /// Separate from the review and QA counters on purpose: a task can legitimately need
    /// several of these without having failed review even once, and spending a review cycle
    /// on someone else's merge would escalate it for the wrong reason.
    /// </summary>
    Task<BaseSyncRequeue?> RequeueForBaseSyncAsync(
        Ulid taskId, int maxAttempts, string reason, CancellationToken ct = default);

    /// <summary>
    /// Applies a "run finished but committed nothing" outcome. Retries like a rejected
    /// review (ReviewCycleCount++, Developing/InQueue), but escalation parks the task as
    /// Developing/WaitingUserVerification rather than WaitingCodeReview: there is no PR to
    /// review, and WaitingUserVerification is the status a chat reply resumes — one save.
    /// </summary>
    Task<CodeReviewTransition?> ApplyNoCommitOutcomeAsync(
        Ulid taskId, int maxAttempts, CancellationToken ct = default);

    /// <summary>SkipPlanning shortcut: Developing/Active with StartedAt ??= now.</summary>
    Task StartSkipPlanningDevelopingAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>Inserts an Approved v1 plan when the task has no approved plan yet.</summary>
    Task EnsureSyntheticApprovedPlanAsync(
        Ulid taskId, string summary, string approach, CancellationToken ct = default);

    /// <summary>
    /// Blocked with the agent slot freed, any Running executions marked Failed with
    /// <paramref name="error"/>, and a system message with the reason appended to the
    /// thread — one save. Null when the task does not exist.
    /// </summary>
    Task<BlockedTaskResult?> BlockTaskWithErrorAsync(
        Ulid taskId, string error, CancellationToken ct = default);

    /// <summary>
    /// Marks Running executions whose task has no agent working as Failed with
    /// <paramref name="error"/> (worker-startup sweep for runs orphaned by a crash or
    /// shutdown). Returns the number of executions closed.
    /// </summary>
    Task<int> FailOrphanedRunningExecutionsAsync(string error, CancellationToken ct = default);

    /// <summary>Adds tokens to TotalTokensUsed (e.g. conflict-resolution runs).</summary>
    Task AddTaskTokensUsedAsync(Ulid taskId, int tokens, CancellationToken ct = default);

    /// <summary>Marks the PR merged; ensures Done/Completed when the task is not in Done yet.
    /// <paramref name="mergeCommitSha"/> is the base-branch merge commit when the caller knows
    /// it (webhook payload, merge API response) — carried on the TaskMerged event for
    /// deployment correlation.</summary>
    Task MarkTaskMergedAsync(Ulid taskId, string? mergeCommitSha = null, CancellationToken ct = default);

    /// <summary>
    /// Records why the last merge attempt failed (MergeFailureReason + a system Error
    /// message in the thread). Change-detecting: the merge poller retries every cycle,
    /// so a repeated identical reason returns Changed=false with no new message.
    /// Null when the task does not exist. Cleared by <see cref="MarkTaskMergedAsync"/>.
    /// </summary>
    /// <param name="kind">How the UI should read this failure — see <see cref="MergeFailureKind"/>.
    /// Defaults to <see cref="MergeFailureKind.Unknown"/>, which means "still on the retry path";
    /// only pass something else when the failure is genuinely terminal for automation.</param>
    Task<MergeFailureUpdate?> SetTaskMergeFailureAsync(
        Ulid taskId, string reason, MergeFailureKind kind = MergeFailureKind.Unknown,
        CancellationToken ct = default);

    /// <summary>
    /// The failure is over without a merge: the conflict the notice described has been
    /// resolved and pushed (the PR merges cleanly again), but the task's group is not ready
    /// to land yet. Clears the reason and kind, and puts a task the notice demoted out of
    /// Done back there — while the notice stands, its group counts the task as stuck and
    /// the gate never opens. True when something changed.
    /// </summary>
    Task<bool> ClearTaskMergeFailureAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>
    /// Merged repo-backed tasks completed since <paramref name="since"/> — input for the
    /// worker's merged-worktree sweep (manual merges happen in the API process, which
    /// cannot reach worker-node disk, so each worker polls and cleans its own).
    /// </summary>
    Task<IReadOnlyList<MergeCandidateTask>> ListRecentlyMergedTasksAsync(
        DateTimeOffset since, CancellationToken ct = default);

    /// <summary>Milestones shipped since <paramref name="since"/> — the deployment sweep's
    /// backstop for a lost MilestoneShipped event.</summary>
    Task<IReadOnlyList<ShippedMilestoneInfo>> ListRecentlyShippedMilestonesAsync(
        DateTimeOffset since, CancellationToken ct = default);

    /// <summary>
    /// "No commits between" PR outcome: Done/Completed (CompletedAt ??= now), merged, branch
    /// cleared — one save.
    /// </summary>
    /// <summary>
    /// The PR was closed on the forge without merging. Stops the merge poller and surfaces a
    /// reason for a human — deliberately NOT the same as merging, which would move the task to
    /// Done, count toward milestone progress, and score the forecast as work that shipped.
    /// </summary>
    Task MarkPrClosedAsync(Ulid taskId, CancellationToken ct = default);

    Task CompleteTaskWithoutPrAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>
    /// Tasks the user asked to reset (ResetRequestedAt set) — input for the worker's reset
    /// sweep (resets happen in the API process, which cannot reach worker-node disk or
    /// cancel worker-local runs, so each worker polls).
    /// </summary>
    Task<IReadOnlyList<ResetPendingTask>> ListResetPendingTasksAsync(CancellationToken ct = default);

    /// <summary>
    /// Completes a reset after the run has exited and the worktree is gone: wipes all
    /// messages and plans, cancels Running/Queued executions (history rows and token
    /// totals are kept for audit), and returns the task to Drafting/Draft with branch,
    /// PR, merge, lease, and cycle state cleared and ResetCount incremented — one
    /// transaction. Guarded on ResetRequestedAt so only one replica's finalize wins;
    /// returns false when the task was already finalized (or no longer exists).
    /// </summary>
    Task<bool> FinalizeTaskResetAsync(Ulid taskId, CancellationToken ct = default);
}
