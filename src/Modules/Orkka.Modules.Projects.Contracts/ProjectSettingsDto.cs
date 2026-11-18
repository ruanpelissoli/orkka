namespace Orkka.Modules.Projects.Contracts;

/// <summary>Per-project settings stored as JSON on the project row; parsed module-side.
/// New fields need a default so previously stored JSON keeps deserializing.</summary>
public sealed record ProjectSettingsDto(
    int MaxParallelTasks,
    bool SameRepoParallelism,
    int DefaultTokenLimit = 100000,
    string? DefaultModel = null,
    // Pipeline automation (opt-in, true = automated): when true, an agent-approved code
    // review completes the task and merges hands-free; when false (default) the approval
    // is recorded and a human confirms before Done/merge.
    bool AutoApproveCodeReview = false,
    // Pipeline automation (opt-in): when true, the plan the dev agent writes is approved on
    // its own and the task goes straight to Developing; when false (default) it parks in
    // PlanReady for a person. The plan is still written and followed — this is never
    // SkipPlanning. Available to every project type, hosted apps included.
    bool AutoApprovePlan = false,
    // Orkka Local only: after a merge lands in the user's repo, push it to origin.
    bool AutoPushOnMerge = false,
    // QA testing lane (opt-in, off by default — browser-driving QA runs are token-heavy):
    // when true, finished development work moves to a Testing lane where the QA agent
    // starts the app and verifies it end-to-end via Playwright MCP before code review.
    bool QaEnabled = false,
    // Command the QA agent runs (via Bash, in the task worktree) to start the app under
    // test, e.g. "npm run dev". When unset, QA tasks escalate to a human instead of running.
    string? QaRunCommand = null,
    // Manual verification (opt-in): finished development work parks in the Testing lane
    // waiting for the requester to test it themselves before code review. Independent of
    // QaEnabled — when both are on, the QA agent runs first and the task parks on a pass.
    bool ManualVerificationEnabled = false,
    // Show raw tool-call events in the task Agent Activity feed. Off by default —
    // tool invocations are noise for non-technical users; thinking/progress still show.
    bool ShowAgentToolCalls = false,
    // Autopilot preferences. Deliberately NOT an on/off flag: whether autopilot is running
    // is "does this project have an open AutopilotSession", and duplicating that here would
    // create a second source of truth that can disagree with the first. These are user
    // preferences that outlive a session, which is why they belong on the project.
    //
    // Cloud spend ceiling for ONE session, in USD. Null = uncapped ("just go"), which
    // requires BillingManage to set. Deliberately Cloud-only: UserRunner tasks execute on
    // the creator's own AI subscriptions at $0, so bounding them by dollars is meaningless.
    decimal? AutopilotCloudCapUsd = null,
    // How far ahead of MaxParallelTasks autopilot keeps the queue stocked. Dumping the whole
    // backlog into InQueue would make QueuePosition meaningless, turn the board into a wall
    // of identical cards, and fire one dependency-analysis model call per submitted task.
    int AutopilotQueueDepthFactor = 2,
    // Live documentation (opt-in, off by default — every completed task costs at least one
    // model call). When true, a task whose work has settled is checked against the project's
    // markdown documents: work worth explaining gets a new document, work that invalidated an
    // existing one gets it rewritten. Both are recorded against the task that caused them.
    bool DocumentationSyncEnabled = false,
    // Automation master switch (schedules). Off pauses every schedule without deleting
    // anything — the "autonomy" toggle for the project. Manual runs still work.
    bool AutomationEnabled = true,
    // Keep the app running: when the user opens the project and nothing of it is running,
    // start its auto-select run targets on their machine. What turns "press Run and wait"
    // into "the app is always up, and every merge is visible in the tab you left open".
    // Null = not decided; the Run dock treats it as on for projects Orkka bootstrapped
    // (they ship with a run manifest) and off for repos the user brought.
    bool? AutoRunOnOpen = null,
    // Board tidying (opt-in, off by default): a task that has stood in Done for two weeks is
    // archived automatically — the same write the Archive button makes by hand. The window
    // lives in worker configuration (Worker:AutoArchiveDoneAfterDays) rather than here: this
    // setting is the project's consent to the sweep, and a deployment is far likelier to want
    // one period for everyone than each project to pick its own.
    bool AutoArchiveDoneTasks = false,
    // Task runtime limit (opt-in, off by default). Off, an agent run is bounded only by the
    // deployment's per-phase wall-clock budgets (PhaseBudgets), which are sized to the heaviest
    // legitimate run of each phase — the task takes as long as the work takes. On, every agent
    // run in this project is additionally capped at TaskRuntimeLimitMinutes of wall clock and
    // the task fails when it is exceeded.
    //
    // Off by default on purpose: a flat ceiling that fires on legitimate work throws away the
    // entire run's spend and leaves the task failed with nothing to show. Projects that would
    // rather bound the bill than finish the task can opt in.
    bool TaskRuntimeLimitEnabled = false,
    // Minutes an agent run may take when TaskRuntimeLimitEnabled is on. Ignored when it is off.
    // Literal rather than DefaultTaskRuntimeLimitMinutes below: a primary-constructor default
    // cannot see the type's own members. Keep the two in step.
    int TaskRuntimeLimitMinutes = 30,
    // Working language of the project's agents: the language they chat, narrate their
    // reasoning, write plans/reviews/questions and generate documents in. Set at creation
    // from the creator's interface language and changed only here — the interface language
    // picked in the navbar is for UI text alone. A two-letter code from
    // Orkka.SharedKernel.Utilities.AgentLanguage ("en", "pt", …). Null = a project created
    // before this setting existed; those keep following the requester's interface locale
    // (see ProjectLanguage.Resolve) until someone saves a language for them.
    string? Language = null,
    // How many queued tasks may run their READ-ONLY prep phases (PM review + planning) while
    // the project's execution slots are all busy building something else.
    //
    // Planning is minutes of codebase exploration that touches nothing but its own worktree,
    // and it used to happen strictly between tasks: the board sat with no task running from the
    // moment one finished until the next one's plan was written. Running it ahead moves that
    // window off the critical path — when a slot frees, the next task already has an approved
    // plan (and a warmed worktree) and goes straight to Developing.
    //
    // Prep-ahead runs do NOT consume an execution slot and are exempt from the same-repo
    // exclusion: they only ever read, worktrees are per task, and the git layer already
    // serialises the one thing that is shared (the ref store, via its per-repo fetch gate).
    //
    // Null — the default — derives it from MaxParallelTasks (see PlanAheadDepthFor), which is
    // the answer that keeps the execution slots fed at any parallelism. 0 disables prep-ahead
    // and restores the strictly-serial behaviour; a number pins it.
    int? PlanAheadDepth = null,
    // Task spend limit (opt-in, OFF by default — the shipped behaviour is "no ceiling": a task
    // costs what the work costs). Orkka bills nothing: the figure is the task's ESTIMATED
    // LIST-PRICE SPEND — each agent run priced at the cost the CLI reported, else at the model's
    // list price for its tokens (IUsageQuery.GetTaskEstimatedSpendAsync). For a developer on an
    // API key or Bedrock that is close to what the provider charges; on a subscription it is
    // what the work would have cost on an API key, so the limit is moot there (and off unless
    // they turn it on). On, before each pipeline phase starts the orchestrator compares the
    // task's estimate with TaskSpendLimitUsd (or the larger allowance the requester approved at
    // a checkpoint) and, at or above it, parks the task in Pending Questions asking whether to
    // continue (one more allotment of TaskSpendLimitUsd) or stop — never killing a run, never
    // pushing half-done work forward. Checked between phases, not mid-run: runs execute on the
    // developer's runner and the runner protocol carries no per-run cost ceiling. Pipeline
    // phases only (PM review, planning, development, testing, code review), on purpose: an
    // interactive session has a human at the keyboard ending turns at will, and merge-conflict
    // resolution is a short platform step that has to finish for the merge to land.
    bool TaskSpendLimitEnabled = false,
    // US dollars of estimated list-price spend a task may reach before it asks. Ignored while
    // the limit is off. Literal for the same reason as the runtime minutes.
    decimal TaskSpendLimitUsd = 5m)
{
    /// <summary>The value the UI proposes when the spend limit is first switched on.</summary>
    public const decimal DefaultTaskSpendLimitUsd = 5m;

    /// <summary>Bounds accepted for <see cref="TaskSpendLimitUsd"/>. Under 10 cents no run can
    /// finish a turn; above $10,000 a limit is theatre.</summary>
    public const decimal MinTaskSpendLimitUsd = 0.10m;
    public const decimal MaxTaskSpendLimitUsd = 10_000m;

    /// <summary>US dollars of estimated list-price spend one task may reach in this project, or
    /// null when the project has not asked for a limit. Clamped for the same reason
    /// <see cref="TaskRuntimeLimit"/> is.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal? TaskSpendLimit =>
        TaskSpendLimitEnabled
            ? Math.Clamp(TaskSpendLimitUsd, MinTaskSpendLimitUsd, MaxTaskSpendLimitUsd)
            : null;

    /// <summary>The value the UI proposes when the limit is first switched on.</summary>
    public const int DefaultTaskRuntimeLimitMinutes = 30;

    /// <summary>Upper bound on <see cref="PlanAheadDepth"/>. Every prep-ahead run is a paid
    /// agent run against a base that has not settled yet, so planning far ahead buys less and
    /// less speed for a linearly growing bill — and a stopped session throws all of it away.</summary>
    public const int MaxPlanAheadDepth = 10;

    /// <summary>
    /// The default depth for a project running <paramref name="maxParallelTasks"/> tasks at
    /// once: enough concurrent planning to match the build lane's own throughput, plus one
    /// finished plan held in reserve.
    ///
    /// The reserve is the part that matters and the part that is easy to leave out. At depth
    /// <c>MaxParallelTasks</c> the pipeline plans exactly as fast as it builds, which is only
    /// gap-free while planning stays quicker than development — the moment a task's dev run
    /// finishes faster than the next task's plan (a one-line fix, a big repo), the execution
    /// slot goes idle waiting, which is the whole thing prep-ahead exists to prevent. The +1 is
    /// one full planning run of slack against that.
    /// </summary>
    public static int PlanAheadDepthFor(int maxParallelTasks)
        => Math.Clamp(Math.Max(1, maxParallelTasks) + 1, 0, MaxPlanAheadDepth);

    /// <summary>How far ahead this project actually plans. Clamped, and derived from
    /// <see cref="MaxParallelTasks"/> when unset — which covers settings JSON written before
    /// the field existed, so an existing project gets the same answer as a new one.</summary>
    public int EffectivePlanAheadDepth => PlanAheadDepth is { } depth
        ? Math.Clamp(depth, 0, MaxPlanAheadDepth)
        : PlanAheadDepthFor(MaxParallelTasks);

    /// <summary>Lower/upper bounds accepted for <see cref="TaskRuntimeLimitMinutes"/>. A run
    /// shorter than a minute cannot finish anything; a day is well past the heaviest phase
    /// budget, so anything larger is indistinguishable from having no limit at all.</summary>
    public const int MinTaskRuntimeLimitMinutes = 1;
    public const int MaxTaskRuntimeLimitMinutes = 1440;

    /// <summary>
    /// Wall-clock ceiling for one agent run in this project, or null when the project has not
    /// asked for one. Clamped rather than validated here: settings JSON stored before the
    /// bounds existed (or written by an older client) must still resolve to something runnable.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan? TaskRuntimeLimit =>
        TaskRuntimeLimitEnabled
            ? TimeSpan.FromMinutes(Math.Clamp(
                TaskRuntimeLimitMinutes, MinTaskRuntimeLimitMinutes, MaxTaskRuntimeLimitMinutes))
            : null;
}
