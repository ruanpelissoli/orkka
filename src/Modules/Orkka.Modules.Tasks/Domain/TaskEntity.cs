namespace Orkka.Modules.Tasks.Domain;

using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Domain;


public sealed class TaskEntity : TenantEntity
{
    public Ulid ProjectId { get; set; }
    public Ulid? RepoId { get; set; }
    // Nullable on purpose, and that is the feature: tasks with no milestone are the Backlog.
    // Ad-hoc work should never be forced under an outcome it doesn't belong to.
    public Ulid? MilestoneId { get; set; }

    /// <summary>
    /// The cross-repo group this task is one repo's half of. Null for the overwhelming majority
    /// of tasks, which stand alone.
    ///
    /// Membership changes two things and nothing else: a dependency on a SIBLING is met when the
    /// sibling reaches Completed rather than when it merges (see TaskDependencyQueries.IsUnmet),
    /// and the merge poller holds this task's PR until every sibling can merge too. The task is
    /// otherwise an ordinary single-repo task — same worktree, same branch, same PR.
    /// </summary>
    public Ulid? GroupId { get; set; }
    public int Number { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public Priority Priority { get; set; } = Priority.Medium;
    public TaskType Type { get; set; } = TaskType.Feature;
    public Lane CurrentLane { get; set; } = Lane.Drafting;
    public Contracts.TaskStatus Status { get; set; } = Contracts.TaskStatus.Draft;
    public bool IsArchived { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public string? CreatedByUserId { get; set; }
    public string? RequesterLocale { get; set; }  // UI locale of the human driving this task

    /// <summary>
    /// Set when the task is submitted and cleared when AI dependency analysis finishes.
    ///
    /// Submission marks a task InQueue and saves, then analyses in the background — so for a
    /// few seconds the task looks ready while having none of its edges yet. Submit a whole
    /// milestone at once and the queue claims tasks that depend on each other in parallel.
    /// The queue treats a task in this state as having unmet dependencies, bounded by a time
    /// window so a worker that dies mid-analysis unblocks its tasks rather than stranding them.
    /// </summary>
    public DateTimeOffset? DependencyAnalysisStartedAt { get; set; }

    public int TotalTokensUsed { get; set; }
    public bool HasAgentWorking { get; set; }
    public string? BranchName { get; set; }
    /// <summary>A Story member's place in its story (tracker order, 1-based). Null otherwise.</summary>
    public int? StoryOrder { get; set; }
    /// <summary>A Story member's starting point on the shared branch, recorded when its worktree
    /// is created: the head of origin/{branch} when the branch already existed, else the
    /// merge-base with the default branch. Orkka's review and diff views show only
    /// <c>ReviewBaseSha..HEAD</c>, i.e. this member's own change.</summary>
    public string? ReviewBaseSha { get; set; }
    /// <summary>The branch this task's worktree actually forked from — the milestone's
    /// integration branch, or the repo default when that branch could not be made to exist.
    /// Recorded with BranchName so the PR later opens against the base the work was built on,
    /// not against the base a fresh resolution would pick (which once pointed a runner-built
    /// task at a milestone branch that never reached GitHub: 422 "base invalid", forever).</summary>
    public string? BaseBranch { get; set; }

    public string? PrUrl { get; set; }
    /// <summary>The pull request the developer's runner opened on the forge when the reviewed
    /// work was published. <see cref="PrUrl"/> stays Orkka's own review record.</summary>
    public string? PublishedPrUrl { get; set; }
    public int? QueuePosition { get; set; }
    public string? Tags { get; set; }
    /// <summary>Stable id of the external thing that created this task (e.g.
    /// "github:owner/repo#123" for an issue, "github-ci:owner/repo/run/456"), so a trigger
    /// that fires twice makes one task.</summary>
    public string? ExternalRef { get; set; }
    /// <summary>The tracker ticket's own key (SHOP-123 / 4567) — shown on the card.</summary>
    public string? ExternalKey { get; set; }
    /// <summary>The ticket in its tracker.</summary>
    public string? ExternalUrl { get; set; }
    /// <summary>The configured epic the ticket sits under ("SHOP-1 Checkout").</summary>
    public string? ExternalEpic { get; set; }
    /// <summary>The folder inside the repository the ticket's epic is mapped to (a monorepo
    /// package). Agents are told to keep their changes inside it.</summary>
    public string? WorkingSubpath { get; set; }
    /// <summary>Why Orkka could not move the ticket's status in its tracker ("Couldn't move
    /// SHOP-12 to In Review"), shown on the card until a later move succeeds. Null otherwise.</summary>
    public string? ExternalSyncError { get; set; }
    public string? ContextFiles { get; set; }  // JSON array of repo file paths
    public string? ContextDocumentIds { get; set; }  // JSON array of Documents-module document ULIDs
    // Human opt-out of the planning lane: the PM still refines the task, then it goes
    // straight to Developing on a synthetic approved plan (no plan-approval gate).
    public bool SkipPlanning { get; set; }
    // System-generated tasks (onboarding seeds, deploy fixes) already carry an
    // agent-written description — reviewing it again costs tokens and can park the task
    // in PendingQuestions. Not settable through the API.
    public bool SkipPmReview { get; set; }
    public bool SkipTesting { get; set; }
    public bool SkipCodeReview { get; set; }
    public bool IsMerged { get; set; }
    // Non-null = the last merge attempt failed (conflicts, branch protection, CI checks).
    // Cleared when the PR finally merges. Orthogonal to Status — the task stays in Done.
    public string? MergeFailureReason { get; set; }
    // Machine-readable companion to the reason above: whether automation is still retrying or
    // has run out of moves. Nullable so existing rows stay unclassified rather than being
    // back-dated into a category nobody computed for them.
    public MergeFailureKind? MergeFailureKind { get; set; }
    // Set when the PR was closed on the forge without merging. Distinct from IsMerged on
    // purpose: closed usually means rejected, and recording it as merged would move the task
    // to Done, inflate milestone progress, and score the forecast as a task that shipped.
    // Its real job is to stop the merge poller — the candidate query keys on
    // (Done && !IsMerged), so without this the task would be re-polled against GitHub forever.
    // Cleared whenever a new PR is attached.
    public DateTimeOffset? PrClosedAt { get; set; }
    // Non-null = the repo's setup command failed for this task's worktree, so the app
    // cannot start in it. Cleared on the next successful (or skipped) setup. Persisted
    // rather than left in the timeline because the person it matters to — whoever opens
    // the worktree to run the app — may never scroll the task's activity feed.
    public string? SetupFailure { get; set; }
    // Snapshot of the conflicting files + plain-language summary for the merge page
    // (JSON, see MergeConflictSnapshot). Set when conflicts are detected; cleared on merge.
    public string? MergeConflictsJson { get; set; }
    // Non-null = a human picked a resolution on the merge page and it hasn't run yet.
    // Values: "combine" | "task" | "current" | "perFile" (with PerFile choices in the JSON).
    public string? MergeResolutionStrategy { get; set; }
    public string? MergeResolutionGuidance { get; set; }

    /// <summary>
    /// When the resolution attempt currently owning this task began — stamped when a strategy
    /// is armed, and by the orchestrator for the automatic pass (which arms no strategy).
    /// Cleared with the attempt.
    ///
    /// Exists so the merge page can say how long the merge has been running. The page used to
    /// count from when the browser first noticed, which restarted on every reload and made a
    /// long resolution look like it had just begun.
    /// </summary>
    public DateTimeOffset? MergeResolutionStartedAt { get; set; }

    /// <summary>
    /// A follow-up the user asked for WHILE an attempt was running: it starts when the current
    /// one finishes without merging, and is dropped when it merges (there is nothing left to
    /// resolve). Same value set as <see cref="MergeResolutionStrategy"/> minus "perFile",
    /// whose per-file choices live in the conflict snapshot the running attempt may rewrite.
    ///
    /// A queue of one, deliberately. Steering is "no, do it this way instead" — the last
    /// instruction is the one that counts, and a backlog of superseded attempts would spend an
    /// agent run each to arrive where the newest one already points.
    /// </summary>
    public string? QueuedMergeStrategy { get; set; }
    public string? QueuedMergeGuidance { get; set; }
    public int ReviewCycleCount { get; set; }
    public int QaCycleCount { get; set; }
    // Dev passes spent on a failed automated check (the repo's CheckCommand: lint/typecheck/
    // tests) after Developing. Bounded like the other counters, for the same reason.
    public int CheckCycleCount { get; set; }
    // Dev passes spent absorbing the base branch: incremented whenever a read-only phase
    // (review, QA) found the base no longer merges cleanly and sent the task back to a dev
    // agent to reconcile it. Bounded like the other two counters, because a dev agent that
    // keeps failing to finish the merge would otherwise bounce between lanes forever, paying
    // for a full run each lap.
    public int BaseSyncCycleCount { get; set; }
    // US dollars of estimated list-price spend the requester has explicitly approved for this
    // task beyond the project's task spend limit — set to "spent so far + one more allotment"
    // each time they answer Continue at a spend checkpoint. Null = nothing approved yet; the
    // project limit alone applies.
    public decimal? SpendApprovedUsd { get; set; }
    // Automatic un-blocks spent on this task after transient provider failures (the worker's
    // auto-recovery sweep re-queuing a task the in-run retry budget gave up on). Persisted,
    // not counted per pod: an in-memory counter resets on every deploy, and a rolling fleet
    // would grant a stuck task a fresh budget per replica forever. A human Retry resets it —
    // someone looked, so automation may try again.
    public int AutoRecoveryCount { get; set; }
    public Lane? QuestionSourceLane { get; set; }
    public Ulid? AssignedAgentId { get; set; }
    // Per-task model override picked at creation. null = run the dev agent on its own
    // Team-page model. Only honored for dev-role agents (Planning/Developing) — PM, QA
    // and Code Reviewer always keep their own models.
    public string? Model { get; set; }
    // Advisory cheaper-model suggestion from the PM review's complexity grade. Purely a
    // proposal: Apply copies it into Model (the per-task override) and Dismiss clears it —
    // the platform never applies it on its own and never touches the agent's configuration.
    public string? RecommendedModel { get; set; }
    public Ulid? CurrentAgentId { get; set; }
    public Ulid? SourceMessageId { get; set; }  // Agent chat message this task was created from
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // Whose runner executes the agent phases (their machine, their AI subscriptions), stamped
    // at creation; the task is claim-affine to the Worker replica holding that runner's
    // connection. Null on a task no member created — it then falls back to CreatedByUserId.
    public string? RunnerUserId { get; set; }

    /// <summary>
    /// What interface this task's work exposes to other repositories — endpoints, methods,
    /// request/response shapes — in markdown, extracted from its diff.
    ///
    /// Ordering client work after the API it calls fixes the sequence but not the knowledge:
    /// merging a backend PR puts nothing into the frontend repo's worktree, so without this the
    /// client agent starts correctly ordered and still cannot read a single route signature —
    /// and invents one.
    ///
    /// Only populated for tasks something in ANOTHER repo actually depends on. A contract nobody
    /// reads is a model call nobody asked to pay for.
    /// </summary>
    public string? ApiSurface { get; set; }

    /// <summary>
    /// Progress of the post-completion documentation pass — see <see cref="Contracts.DocSyncStatus"/>.
    ///
    /// Deliberately a pull state rather than a flag set at completion time. Nine different code
    /// paths write (Done, Completed): the code-review verdict, the merge poller, the human merge
    /// and review endpoints, manual verification, a board drag, and two forge-webhook routes.
    /// Asking every one of them to remember to enqueue documentation would guarantee that one
    /// of them eventually forgets; a sweep that reads the same condition they all produce cannot.
    /// </summary>
    public DocSyncStatus DocSyncStatus { get; set; } = DocSyncStatus.None;

    /// <summary>When the documentation pass last finished, however it finished.</summary>
    public DateTimeOffset? DocSyncCompletedAt { get; set; }

    /// <summary>Runs attempted. Bounds retries of a pass that keeps erroring, so a task with a
    /// diff the model chokes on cannot bill the org once a minute forever.</summary>
    public int DocSyncAttempts { get; set; }

    /// <summary>
    /// Non-null = autopilot submitted this task, under that session.
    ///
    /// This is the hands-free override signal, and deliberately the ONLY one: autopilot never
    /// writes project settings. Flipping <c>AutoApproveCodeReview</c> or <c>QaEnabled</c> on the
    /// project row would mean toggling off has to restore the user's originals, and a crash
    /// mid-session — or two people toggling — would silently destroy their configuration. A
    /// per-task stamp cannot: the orchestrator resolves its gates through it, and a task
    /// submitted by hand keeps today's behaviour exactly.
    ///
    /// It also carries the session's task counters and the spend join, which is why it is one
    /// column rather than a bool plus a separate link table.
    /// </summary>
    public Ulid? AutopilotSessionId { get; set; }

    /// <summary>
    /// This task is driven by a human in a live interactive session, not by the queue.
    ///
    /// Deliberately a flag rather than a new <see cref="Lane"/>: Lane has no HasConversion, so
    /// EF persists it as an int — inserting a value mid-enum renumbers every existing row, and
    /// appending after Done puts it last on a board where lane order IS the pipeline. An
    /// interactive session sits in Developing/Active like the work it actually is.
    ///
    /// Load-bearing in exactly one place: ListQueuedTasksAsync excludes it, so the autonomous
    /// sweep can never claim a task somebody is typing into. Belt and braces — such a task is
    /// never set to InQueue either, and the queue keys on that status.
    /// </summary>
    public bool IsInteractive { get; set; }

    /// <summary>
    /// Non-null while an interactive session is live (mirrors AgentConversation.ActiveRunStartedAt).
    ///
    /// While set, the orchestration lease below is renewed by the session heartbeat and held for
    /// the whole SESSION rather than per turn: the CLI session file lives on the machine that ran
    /// the previous turn, so losing affinity would cold-boot — and re-pay full context — on every
    /// turn the user takes. See .claude/docs/INTERACTIVE.md.
    /// </summary>
    public DateTimeOffset? InteractiveSessionStartedAt { get; set; }

    /// <summary>
    /// Watermark: user messages created at or before this instant have been answered.
    ///
    /// Deliberately NOT derived from the last execution's CompletedAt, which looks equivalent and
    /// silently loses messages: a follow-up typed while the agent is working is older than the
    /// run that finishes after it, so it would be counted as already answered and never replied
    /// to. The turn stamps the newest message it actually consumed instead.
    /// </summary>
    public DateTimeOffset? InteractiveAnsweredThroughAt { get; set; }

    /// <summary>
    /// Non-null = the human pressed Finish and the session should wrap up: commit, push, and hand
    /// off to the normal PR → Code Review path.
    ///
    /// Distinct from clearing <see cref="InteractiveSessionStartedAt"/>, which is abandonment.
    /// Without two signals the loop cannot tell "ship this" from "throw it away", and Finish is
    /// always an explicit human action — an agent deciding on its own that a session is over and
    /// opening a PR is a bad surprise.
    /// </summary>
    public DateTimeOffset? InteractiveFinishRequestedAt { get; set; }

    /// <summary>
    /// When this task's interactive session ended — the durable "was a session" marker.
    ///
    /// Needed because <see cref="IsInteractive"/> is CLEARED at close (a finished session's
    /// task belongs to the ordinary pipeline, and the flag's queue exclusion stranded it
    /// otherwise). Without this, "finish an already-ended session" and "finish a task that
    /// never was one" would be indistinguishable — a 400 the user can understand versus a
    /// 404 that gaslights them about a session they were just in.
    /// </summary>
    public DateTimeOffset? InteractiveSessionEndedAt { get; set; }

    // Orchestration lease: which worker instance owns this task's in-flight run.
    // An expired lease means that worker died — any worker may re-claim and resume.
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    // Reset-to-Drafting: stamped when the user requests a reset (Status = Resetting);
    // cleared when the worker sweep finalizes. ResetCount suffixes the fallback branch
    // name (-r{n}) so a re-run can never resume the previous run's pushed branch.
    public DateTimeOffset? ResetRequestedAt { get; set; }
    public int ResetCount { get; set; }

    // Navigation (Project/Repo live in the Projects module — plain ProjectId/RepoId columns only)
    public Milestone? Milestone { get; set; }
    public TaskGroup? Group { get; set; }
    public ICollection<TaskAttachment> Attachments { get; set; } = [];
    public ICollection<TaskPlan> Plans { get; set; } = [];
    public ICollection<TaskMessage> Messages { get; set; } = [];
    public ICollection<TaskExecution> Executions { get; set; } = [];
    public ICollection<TaskDependency> Dependencies { get; set; } = [];
    public ICollection<TaskDependency> Dependents { get; set; } = [];
}
