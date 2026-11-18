namespace Orkka.Modules.Execution.Orchestration;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orkka.Modules.SourceControl.Contracts;

public sealed class TaskQueueProcessor(
    IServiceScopeFactory scopeFactory,
    ITaskOrchestrator orchestrator,
    IDocumentationSyncService documentationSync,
    IExecutionRouter executionRouter,
    ITaskNotifier taskNotifier,
    IAgentAuditLog auditLog,
    Orkka.Modules.Runners.Contracts.IRunnerClaimAffinity claimAffinity,
    WorkerDrain drain,
    IConfiguration configuration,
    IOptions<DocumentationSyncOptions> documentationOptions,
    ILogger<TaskQueueProcessor> logger) : BackgroundService
{
    private readonly DocumentationSyncOptions _documentation = documentationOptions.Value;
    private DateTimeOffset _nextDocumentationSweep = DateTimeOffset.MinValue;

    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(
        configuration.GetValue<int>("Worker:PollIntervalSeconds", 5));

    // ── Idle backoff for the queue sweep ─────────────────────────────────────────────
    //
    // A queued task's dependencies can only become met when some OTHER task completes, and
    // this process has no way to hear that: per the cross-process rule an API-to-worker action
    // lands as a DB flag that only a sweep can see, and the in-process TaskStatusChanged
    // handler (DependentsRefreshOnTaskCompleted) only refreshes board badges — its own comment
    // defers the actual start to "the next queue poll". So the sweep has to stay; it just has
    // no business re-deriving an unchanged answer every five seconds. In prod it did exactly
    // that for eleven hours: two worker StatefulSets, ~15,600 sweeps, the same six deferred
    // tasks every single time.
    //
    // What backs off is the EXPENSIVE half — the dependency re-derivation, the routing reads
    // and the housekeeping. What does not is the loop itself: the lease heartbeat, the
    // reset sweep and interactive-session pickup still run every base interval, because each
    // one is a human waiting on a screen and each is a single indexed query.
    //
    // Responsiveness for newly queued work is preserved by making the cheap query the change
    // detector: ListQueuedTasksAsync runs every cycle regardless, and a queue whose id set
    // moved forces a full sweep immediately, ladder or no ladder. The ladder therefore only
    // ever delays rediscovering something about a queue that has not changed.
    private readonly TimeSpan[] _sweepLadder = BuildSweepLadder(configuration);
    private int _sweepStep;

    /// <summary>Cycles left to coast before the next full queue sweep. Zero means "sweep now".</summary>
    private int _sweepSkips;

    /// <summary>Decided once at the top of each cycle so the gated blocks and the end-of-cycle
    /// bookkeeping all agree about whether this revolution was a sweep.</summary>
    private bool _sweepDue = true;

    /// <summary>Set by anything that moved work forward; read and cleared at the end of the
    /// cycle. Interlocked because an orchestration's completion continuation runs off the
    /// poll thread.</summary>
    private int _pollDidWork;

    /// <summary>How far apart full queue sweeps currently are — the base poll interval while
    /// there is work, stepping up <see cref="_sweepLadder"/> while there is none.</summary>
    internal TimeSpan CurrentSweepInterval => _sweepLadder[_sweepStep];

    /// <summary>Extra ladder rungs between the configured base interval and the ceiling.</summary>
    private static readonly TimeSpan[] IdleSweepSteps = [TimeSpan.FromSeconds(30)];

    private static TimeSpan[] BuildSweepLadder(IConfiguration configuration)
    {
        var basis = TimeSpan.FromSeconds(
            Math.Max(1, configuration.GetValue<int>("Worker:PollIntervalSeconds", 5)));
        var ceiling = TimeSpan.FromSeconds(
            configuration.GetValue<int>("Worker:IdleSweepCeilingSeconds", 60));

        // A ceiling at or below the base interval switches the backoff off entirely — the
        // escape hatch for an operator who wants the old flat cadence back.
        if (ceiling <= basis)
            return [basis];

        List<TimeSpan> ladder = [basis];
        ladder.AddRange(IdleSweepSteps.Where(step => step > basis && step < ceiling));
        ladder.Add(ceiling);
        return [.. ladder];
    }

    private void MarkPollProductive() => Interlocked.Exchange(ref _pollDidWork, 1);

    /// <summary>
    /// End-of-cycle bookkeeping. Real work anywhere in the cycle — or a run still in flight on
    /// this replica, whose completion is the very event the queue is waiting on — puts sweeps
    /// back on the base interval. An idle sweep takes one step up the ladder; a coasting cycle
    /// just burns a skip.
    /// </summary>
    private void UpdateSweepCadence()
    {
        var didWork = Interlocked.Exchange(ref _pollDidWork, 0) != 0;
        int inFlight;
        lock (_runningTasks) { inFlight = _runningTasks.Count; }

        if (didWork || inFlight > 0)
            SetSweepStep(0);
        else if (_sweepDue)
            SetSweepStep(Math.Min(_sweepStep + 1, _sweepLadder.Length - 1));
        else
            _sweepSkips--;
    }

    private void SetSweepStep(int step)
    {
        var previous = _sweepStep;
        _sweepStep = step;

        // How many base-interval cycles to coast so that sweeps land _sweepLadder[step] apart.
        _sweepSkips = (int)Math.Round(_sweepLadder[step] / _pollInterval) - 1;
        if (_sweepSkips < 0)
            _sweepSkips = 0;

        // Once per change, never per sweep — the whole point here is that a steady state
        // stops talking.
        if (step != previous)
            logger.LogInformation(
                "Queue sweep cadence {From}s -> {To}s ({Reason})",
                _sweepLadder[previous].TotalSeconds, _sweepLadder[step].TotalSeconds,
                step == 0 ? "work to do" : "nothing actionable");
    }

    /// <summary>Task ids reported as dependency-blocked by the previous sweep, so the deferral
    /// is announced when the set CHANGES rather than on every revolution.</summary>
    private readonly HashSet<Ulid> _deferredTaskIds = [];

    /// <summary>The queued id set as of the last cycle. Cheap enough to re-read every cycle,
    /// and it is what lets a NEWLY queued task jump the backoff instead of waiting it out.</summary>
    private readonly HashSet<Ulid> _lastQueuedTaskIds = [];

    // Orchestration lease: heartbeat-extended every poll cycle, so the TTL only has to
    // outlive worker pauses (deploys, GC), not whole agent runs. In k8s the StatefulSet
    // pod name makes this stable per replica, so a restarted pod re-acquires its own
    // leases immediately instead of waiting out the TTL.
    private readonly string _leaseOwner = Environment.MachineName;


    private readonly TimeSpan _leaseDuration = TimeSpan.FromMinutes(
        configuration.GetValue<int>("Worker:TaskLeaseMinutes", 5));

    // How far back the merged-task sweep looks; older worktrees are the runner's own GC's job.
    private readonly TimeSpan _worktreeRetention = TimeSpan.FromHours(
        configuration.GetValue<int>("Worker:WorktreeRetentionHours", 72));

    // Merged-task worktree sweep: manual merges (task page / repo page) happen in the API
    // process, which holds no runner link — so each worker polls for recently merged tasks
    // and removes their worktrees on the owner's runner. Cleanup is idempotent; the swept set just
    // avoids re-invoking it for the same task every minute.
    private readonly TimeSpan _mergedSweepInterval = TimeSpan.FromSeconds(
        configuration.GetValue<int>("Worker:MergedWorktreeSweepSeconds", 60));
    private DateTimeOffset _nextMergedSweep = DateTimeOffset.MinValue;
    private readonly HashSet<Ulid> _sweptMergedWorktrees = [];

    // Track tasks currently being orchestrated to avoid duplicate fire-and-forgets
    private readonly HashSet<Ulid> _runningTasks = [];

    /// <summary>Tests only: stand in for a run this replica started (a fired resolver, a resumed
    /// session) so the sweeps can be shown to leave such a task alone.</summary>
    /// <summary>Tasks already reported as held by their group's within-cycle ordering. The merge
    /// sweep runs on one loop per replica, so no lock.</summary>
    private readonly HashSet<Ulid> _cycleHoldLogged = [];

    internal void MarkRunningForTests(Ulid taskId)
    {
        lock (_runningTasks) { _runningTasks.Add(taskId); }
    }

    internal void UnmarkRunningForTests(Ulid taskId)
    {
        lock (_runningTasks) { _runningTasks.Remove(taskId); }
    }

    // Per-run cancellation handles so the reset sweep can stop one task's run without
    // stopping the worker. Entries live exactly as long as the orchestration itself.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Ulid, CancellationTokenSource> _runCancellations = new();

    /// <summary>How long a reset waits for an unreachable runner before it finalizes without
    /// the runner-side cancel/cleanup. Longer than a reboot or a Wi-Fi blink (both back inside
    /// ~90 s), short enough that a user who stopped their runner on purpose is not left staring
    /// at a spinner.</summary>
    internal static readonly TimeSpan ResetRunnerOfflineGrace = TimeSpan.FromMinutes(2);

    // Automatic conflict resolution runs once per task; after that the merge page waits
    // for a human choice (which resets the counter). Prevents unbounded agent spend.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Ulid, int> _autoResolveAttempts = new();

    /// <summary>Story members whose landing last failed on a conflict in ANOTHER repository of
    /// the story. Their own pull request reads mergeable, which must not count as "the conflict
    /// is over" — that would hand the resolver a fresh attempt every cycle.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Ulid, byte> _storyConflicts = new();
    private const int MaxAutoResolveAttempts = 1;

    // Auto-recovery of provider outages: a task whose run exhausted the in-run transient
    // retry budget (see "Transient agent-run failure" in TaskOrchestrator) parks as Blocked
    // with the API error — this sweep re-queues it once the incident has had time to pass.
    // Paced to a minute: the backoff makes sub-minute reaction pointless.
    private readonly bool _autoRecoverApiErrors =
        configuration.GetValue<bool>("Worker:AutoRecoverApiErrors", true);
    private readonly TimeSpan _autoRecoverBackoff = TimeSpan.FromMinutes(
        configuration.GetValue<int>("Worker:AutoRecoverBackoffMinutes", 10));
    private readonly int _autoRecoverMaxRounds =
        configuration.GetValue<int>("Worker:AutoRecoverMaxRounds", 3);
    private DateTimeOffset _nextAutoRecoverSweep = DateTimeOffset.MinValue;
    private static readonly TimeSpan AutoRecoverSweepInterval = TimeSpan.FromMinutes(1);


    // PR creation against a head the forge rejected as missing (or any other create failure)
    // cannot succeed on the next 5-second cycle — back off per task instead of hammering the
    // API 196 times in 17 minutes, which is what an un-pushed runner branch produced in prod.
    // In-memory; a host restart starts the ladder from the bottom, which only means a few
    // extra attempts.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Ulid, (int Failures, DateTimeOffset NextAttempt)> _prCreateBackoff = new();
    private static readonly TimeSpan[] PrCreateBackoffLadder =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];

    private void BumpPrCreateBackoff(Ulid taskId)
    {
        var failures = _prCreateBackoff.TryGetValue(taskId, out var prior) ? prior.Failures + 1 : 1;
        var delay = PrCreateBackoffLadder[Math.Min(failures, PrCreateBackoffLadder.Length) - 1];
        _prCreateBackoff[taskId] = (failures, DateTimeOffset.UtcNow + delay);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "TaskQueueProcessor started with {Interval}s poll interval (idle queue-sweep ladder: {Ladder})",
            _pollInterval.TotalSeconds,
            string.Join(" -> ", _sweepLadder.Select(step => $"{step.TotalSeconds:0}s")));

        await FailOrphanedExecutionsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPollCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing task queue");
            }

            try { await Task.Delay(_pollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        await StopInFlightRunsAsync();
        logger.LogInformation("TaskQueueProcessor stopped");
    }

    /// <summary>
    /// One poll. While the worker is draining (see <see cref="WorkerDrain"/>: a deploy is
    /// letting this pod finish what it holds) only the lease heartbeat and the reset sweep
    /// run — nothing new is claimed, resumed or picked up, and the housekeeping/GC passes
    /// are left to the replicas that stay.
    /// </summary>
    internal async Task RunPollCycleAsync(CancellationToken ct)
    {
        _sweepDue = _sweepSkips <= 0;
        try
        {
            await RunPollCycleCoreAsync(ct);
        }
        finally
        {
            // In the finally so a throw mid-cycle does not freeze the cadence at whatever
            // step it happened to be on.
            UpdateSweepCadence();
        }
    }

    private async Task RunPollCycleCoreAsync(CancellationToken ct)
    {
        await ExtendLocalLeasesAsync(ct);

        // User-requested resets run before queue pickup: cancel in-flight runs
        // early, and never claim work for a task that is about to be wiped.
        await ProcessResetRequestsAsync(ct);

        if (drain.IsDraining)
            return;

        await ProcessQueueAsync(ct);

        // After queue pickup, and on its own path: a session is Active from the moment
        // the human opens it, so it is never "queued" — it just needs a worker.
        await ProcessInteractiveSessionsAsync(ct);

        // Gated with the queue sweep: every pass below is a forge read or a spend, and on an
        // idle host they were all re-asking the same question every five seconds. Each one
        // marks the cycle productive when it acts, so a pipeline that is actually moving keeps
        // running at the base interval.
        if (_sweepDue)
        {
            await CreateMissingPullRequestsAsync(ct);
            await MergeUnmergedPullRequestsAsync(ct);
            await AutoRecoverApiErrorTasksAsync(ct);
            await ProcessDocumentationSyncAsync(ct);
        }

        await SweepMergedTaskWorktreesAsync(ct);
    }

    /// <summary>
    /// The host is stopping — after whatever drain the lifetime ran. Anything still running
    /// is cancelled (its lease is released on exit, and the next worker re-runs the phase —
    /// the pre-drain behaviour) and given a short moment to unwind inside the host's own
    /// shutdown budget.
    /// </summary>
    private async Task StopInFlightRunsAsync()
    {
        int running;
        lock (_runningTasks) { running = _runningTasks.Count; }
        if (running == 0)
            return;

        logger.LogWarning(
            "Stopping with {Count} run(s) still in flight — cancelling; they re-run on the next worker",
            running);
        foreach (var (_, cts) in _runCancellations)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* just exited */ }
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (_runningTasks) { if (_runningTasks.Count == 0) return; }
            await Task.Delay(250);
        }
    }

    /// <summary>
    /// Startup recovery: closes execution rows left Running by a previous worker that
    /// crashed or shut down mid-run. Only executions whose task has no agent working are
    /// swept — tasks still marked HasAgentWorking are resumed by <see cref="ResumeAgentTasksAsync"/>,
    /// which supersedes their stale executions when the new run starts.
    /// </summary>
    private async Task FailOrphanedExecutionsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();

            var closed = await tasksApi.FailOrphanedRunningExecutionsAsync(
                "Interrupted — the worker stopped while this run was in progress.", ct);

            if (closed > 0)
                logger.LogWarning("Closed {Count} orphaned Running execution(s) on startup", closed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sweep orphaned Running executions on startup");
        }
    }

    /// <summary>
    /// Announces the dependency-blocked set only when it CHANGES.
    ///
    /// The old unconditional Info line fired on every revolution, which in prod meant 7,836
    /// identical lines from one worker in eleven hours — 27.5% of that pod's entire log volume,
    /// all of it re-reporting the same six task ids. A steady state is not news, and drowning
    /// the component's real messages is the actual cost. The ids go in the changed line too:
    /// last time this needed diagnosing, the count alone said nothing about WHICH tasks were
    /// stuck, or whether their dependency could ever be met.
    /// </summary>
    private void NoteDeferredTasks(IReadOnlyCollection<Ulid> deferred)
    {
        if (_deferredTaskIds.SetEquals(deferred))
        {
            if (deferred.Count > 0)
                logger.LogDebug(
                    "Still deferring {Count} task(s) with unmet dependencies", deferred.Count);
            return;
        }

        _deferredTaskIds.Clear();
        _deferredTaskIds.UnionWith(deferred);

        if (deferred.Count == 0)
        {
            logger.LogInformation("No task is waiting on an unmet dependency any more");
            return;
        }

        logger.LogInformation(
            "Deferred {Count} task(s) with unmet dependencies: {TaskIds}",
            deferred.Count, string.Join(", ", deferred));
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var projectsApi = scope.ServiceProvider.GetRequiredService<IProjectsApi>();

        // 1) Resume tasks that already have an agent slot allocated (no concurrency check
        // needed). Crash recovery, not a pipeline transition — its worst case is a restarted
        // replica's task waiting one sweep interval, well inside the lease TTL.
        if (_sweepDue)
            await ResumeAgentTasksAsync(tasksApi, ct);

        // 2) Find new tasks that are InQueue and ready to be picked up (highest priority first).
        //
        // Deliberately OUTSIDE the sweep gate. It is one indexed query, and it is the change
        // detector that keeps the backoff honest: a task queued into a quiet system moves this
        // id set, which forces the full sweep below on the very next cycle rather than making
        // the user wait out the ladder.
        var queuedTasks = await tasksApi.ListQueuedTasksAsync(ct);

        var queuedTaskIds = queuedTasks.Select(t => t.Id).ToList();
        var queueChanged = !_lastQueuedTaskIds.SetEquals(queuedTaskIds);
        if (queueChanged)
        {
            _lastQueuedTaskIds.Clear();
            _lastQueuedTaskIds.UnionWith(queuedTaskIds);

            // The queue moving is a real event, so drop back to the base interval: whatever
            // changed deserves the next few cycles at full speed.
            MarkPollProductive();
        }
        else if (!_sweepDue)
        {
            // Same queue as last cycle and the ladder says it is not time yet. Everything
            // below would re-derive an answer that cannot have changed.
            return;
        }

        if (queuedTasks.Count == 0)
        {
            NoteDeferredTasks([]);
            return;
        }

        // 3) Filter out tasks whose dependencies haven't completed yet
        var tasksWithUnmetDeps = await tasksApi.GetTasksWithUnmetDependenciesAsync(queuedTaskIds, ct);

        NoteDeferredTasks(tasksWithUnmetDeps);

        // Autopilot tasks planned ahead of their milestone's gate. Read before the filter below
        // drops the held ones, because a plan held at Developing is still a BANKED plan: it
        // holds the prep depth like any other. Uncounted, the depth would bound nothing while a
        // milestone waits — the whole next milestone would be planned against a base that does
        // not have the previous one yet.
        var autopilotQueuedIds = queuedTasks.Where(t => t.IsAutopilot).Select(t => t.Id).ToList();
        var milestoneHeld = autopilotQueuedIds.Count == 0
            ? (IReadOnlySet<Ulid>)new HashSet<Ulid>()
            : (await tasksApi.GetMilestoneHeldTaskIdsAsync(autopilotQueuedIds, ct)) ?? new HashSet<Ulid>();
        var heldPlansByProject = queuedTasks
            .Where(t => milestoneHeld.Contains(t.Id) && tasksWithUnmetDeps.Contains(t.Id)
                && t.Kind() == TaskPhaseKind.Build)
            .GroupBy(t => t.ProjectId)
            .ToDictionary(g => g.Key, g => g.Count());

        if (tasksWithUnmetDeps.Count > 0)
        {
            queuedTasks = queuedTasks
                .Where(t => !tasksWithUnmetDeps.Contains(t.Id))
                .ToList();

            if (queuedTasks.Count == 0)
                return;
        }

        logger.LogInformation("Found {Count} queued task(s) across projects", queuedTasks.Count);

        // Group by project to check concurrency limits
        var tasksByProject = queuedTasks.GroupBy(t => t.ProjectId);

        var presenceCache = new Dictionary<(Ulid OrgId, string UserId), bool>();

        foreach (var projectGroup in tasksByProject)
        {
            var projectId = projectGroup.Key;

            // Get project settings for max parallel tasks
            var project = await projectsApi.GetProjectSummaryAsync(projectId, ct);

            if (project is null) continue;

            var projectTasks = projectGroup.ToList();
            IEnumerable<QueuedTaskInfo> eligibleTasks = projectTasks;

            // Slot accounting is per-replica, not transactional: with N replicas polling the
            // same project simultaneously, a project can transiently exceed MaxParallelTasks
            // by up to N-1 tasks. The limit is a QoS knob, not a correctness invariant —
            // per-task exclusivity is guaranteed by the atomic claim/lease, never by this count.
            var maxParallel = project.Settings?.MaxParallelTasks ?? 3;
            var allowSameRepo = project.Settings?.SameRepoParallelism ?? false;
            var planAheadDepth = project.Settings?.EffectivePlanAheadDepth
                ?? ProjectSettingsDto.PlanAheadDepthFor(maxParallel);

            // Count currently active tasks in this project
            var activeTasks = await tasksApi.ListActiveTasksAsync(projectId, ct);

            // Three budgets, because the three kinds of run cost different things — and because
            // MaxParallelTasks is about the one resource they do not share: a branch being
            // written. Prep (PM review, planning) and Review only read. Charging them against
            // MaxParallelTasks is what made the board go idle between tasks: the next task's
            // plan could not start until the current one was Done, and the current one's review
            // held the slot the next task needed to start building at all.
            //
            // A task spends the prep budget only if it is ELIGIBLE for it. A hand-submitted
            // task's planning run was admitted against MaxParallelTasks and has to keep being
            // counted there — otherwise the next poll reads its slot as free and admits
            // another, which is unbounded rather than merely wrong.
            bool SpendsPrepBudget(bool isAutopilot, Lane lane, bool skipPlanning)
                => planAheadDepth > 0
                    && isAutopilot
                    && TaskPhases.Of(lane, skipPlanning) == TaskPhaseKind.Prep;

            // The three are exhaustive and disjoint, which is what lets the counts below be a
            // subtraction: a review spends the review budget, an eligible prep spends the prep
            // depth, and everything else — including a hand-submitted task's planning run —
            // holds an execution slot.
            bool HoldsExecutionSlot(bool isAutopilot, Lane lane, bool skipPlanning)
                => TaskPhases.Of(lane, skipPlanning) != TaskPhaseKind.Review
                    && !SpendsPrepBudget(isAutopilot, lane, skipPlanning);

            var activePreps = activeTasks.Count(
                t => SpendsPrepBudget(t.IsAutopilot, t.CurrentLane, t.SkipPlanning));
            var activeReviews = activeTasks.Count(t => t.Kind() == TaskPhaseKind.Review);
            var activeBuilds = activeTasks.Count - activePreps - activeReviews;

            // Plans already banked. An autopilot task queued at a build lane has HAD its
            // planning run and is only waiting for a slot — it is prep-ahead's finished output,
            // so it holds the prep budget until it is picked up.
            //
            // Without this the depth bounds nothing over time: a planning run ends by
            // re-queuing its task, which frees the prep slot for the next task, and the next —
            // so one long Developing run would quietly get the whole backlog planned, every
            // plan written against a base nobody has built yet and paid for whether the session
            // survives to use it or not.
            var plansBanked = projectTasks.Count(
                    t => t.IsAutopilot && t.Kind() == TaskPhaseKind.Build)
                + heldPlansByProject.GetValueOrDefault(projectId);

            // Four ceilings, each answering a different question. MaxParallelTasks bounds work
            // that touches a branch, and — separately, at the same size — the reviews that trail
            // it, since reviews arrive at exactly the rate builds finish. The prep depth bounds
            // how far ahead of both the pipeline reads. And because a running prep or review
            // stops counting against the build budget, a hard in-flight cap keeps the three from
            // compounding into more concurrent runs than the project's shape implies.
            var availableSlots = maxParallel - activeBuilds;
            var availableReviews = maxParallel - activeReviews;
            var availablePrepSlots = planAheadDepth - activePreps - plansBanked;
            var availableInFlight =
                (2 * maxParallel) + planAheadDepth - activeTasks.Count;
            if (availableInFlight <= 0
                || (availableSlots <= 0 && availableReviews <= 0 && availablePrepSlots <= 0))
            {
                logger.LogDebug(
                    "Project {ProjectId} at concurrency limit ({ActiveBuilds}/{MaxParallel} building, "
                    + "{ActiveReviews}/{MaxParallel} reviewing, {ActivePreps} planning ahead + "
                    + "{PlansBanked} planned and waiting, of depth {PlanAhead}), skipping",
                    projectId, activeBuilds, maxParallel, activeReviews, activePreps,
                    plansBanked, planAheadDepth);
                continue;
            }

            logger.LogInformation(
                "Project {ProjectId}: {ActiveBuilds} building ({AvailableSlots} slot(s) of {MaxParallel} "
                + "free), {ActiveReviews} reviewing, {ActivePreps} planning ahead + {PlansBanked} "
                + "planned and waiting ({AvailablePrepSlots} of depth {PlanAhead} free), "
                + "{AvailableInFlight} pickup(s) available",
                projectId, activeBuilds, availableSlots, maxParallel, activeReviews,
                activePreps, plansBanked, availablePrepSlots, planAheadDepth, availableInFlight);

            // Only work that writes the branch reserves a repo. A prep or review run forks its
            // own worktree and reads — the exclusion that keeps two agents off one repo has
            // nothing to protect against either, and applying it would defeat the whole thing on
            // single-repo projects, which are most of them.
            var activeRepoIds = activeTasks
                .Where(t => t.RepoId is not null
                    && HoldsExecutionSlot(t.IsAutopilot, t.CurrentLane, t.SkipPlanning))
                .Select(t => t.RepoId)
                .ToHashSet();

            foreach (var queuedTask in eligibleTasks)
            {
                if (availableInFlight <= 0) break;

                // Prep spends the prep budget, always — never an execution slot that happens to
                // be free. Letting it borrow a slot looks like extra capacity and is not: the
                // borrowed slot is released as soon as the run is under way (an active prep is
                // counted as a prep), which frees it for the NEXT prep to borrow, and the depth
                // stops bounding anything. The depth is sized above MaxParallelTasks precisely
                // so it never has to borrow — see ProjectSettingsDto.PlanAheadDepthFor.
                //
                // Prep-ahead is autopilot-only: a hand-submitted task's plan parks in PlanReady
                // for a human, so running it early buys no speed and bills for a plan nobody may
                // approve. Those — and every task when the depth is 0 — take a slot as before.
                var runsAhead = SpendsPrepBudget(
                    queuedTask.IsAutopilot, queuedTask.CurrentLane, queuedTask.SkipPlanning);

                // Planning ahead of a milestone gate is plan-ahead or nothing: with the depth at
                // 0 it would take an execution slot the unfinished milestone may need.
                if (milestoneHeld.Contains(queuedTask.Id) && !runsAhead)
                    continue;

                // A review never competes for an execution slot in either direction: it does
                // not take one, and it must not be made to wait for one. Waiting is what put
                // the dev lane behind every review — a slot standing empty while the reviewer
                // read a diff it had already been handed.
                var isReview = queuedTask.Kind() == TaskPhaseKind.Review;

                if (isReview)
                {
                    if (availableReviews <= 0) continue;
                }
                else if (runsAhead)
                {
                    if (availablePrepSlots <= 0) continue;
                }
                else if (availableSlots <= 0)
                {
                    continue;
                }

                // Check same-repo parallelism — for work that writes the branch only, matching
                // how activeRepoIds was seeded.
                if (!runsAhead && !isReview && !allowSameRepo && queuedTask.RepoId is not null &&
                    activeRepoIds.Contains(queuedTask.RepoId))
                {
                    logger.LogDebug("Skipping task {TaskId} — repo {RepoId} already has an active task",
                        queuedTask.Id, queuedTask.RepoId);
                    continue;
                }

                // Claim affinity: every task streams to its owner's runner, so only the
                // replica holding that WebSocket may claim it. Connected to ANOTHER replica →
                // skip silently (that replica claims it). Offline EVERYWHERE (DB presence
                // stale), or a task with no owner to run it → park as WaitingForRunner; the
                // hub's Hello sweep re-queues the user's parked tasks on reconnect.
                if (queuedTask.RunnerUserId is null
                    || !claimAffinity.CanExecuteHere(queuedTask.OrgId, queuedTask.RunnerUserId))
                {
                    if (queuedTask.RunnerUserId is not null
                        && await IsRunnerOnlineAnywhereAsync(
                            scope, presenceCache, queuedTask.OrgId, queuedTask.RunnerUserId, ct))
                    {
                        logger.LogDebug(
                            "Skipping task {TaskId} — creator's runner is connected to another replica",
                            queuedTask.Id);
                        continue;
                    }

                    await tasksApi.MarkTasksWaitingForRunnerAsync([queuedTask.Id], ct);
                    MarkPollProductive();
                    logger.LogInformation(
                        "Parked task {TaskId} as WaitingForRunner — creator's runner is offline",
                        queuedTask.Id);
                    continue;
                }

                // Agents run on the developer's own AI subscription. A runner with no AI CLI
                // signed in has nothing to run them on, so the task waits — once, with the
                // remedy in its thread — instead of starting a run that can only fail. The
                // runner's Hello re-queues it as soon as it reports a signed-in CLI.
                if (await tasksApi.GetTaskSnapshotAsync(queuedTask.Id, ct) is { } runnerTask
                    && executionRouter.Resolve(runnerTask).Readiness.Connected.Count == 0)
                {
                    await tasksApi.MarkTasksWaitingForRunnerAsync([queuedTask.Id], ct);
                    await tasksApi.AddTaskMessageAsync(queuedTask.Id, "system",
                        NoProviderSignedIn, MessageType.SystemEvent, ct: ct);
                    MarkPollProductive();
                    logger.LogInformation(
                        "Parked task {TaskId} as WaitingForRunner — no AI CLI is signed in on its runner",
                        queuedTask.Id);
                    continue;
                }

                // Already running on this replica (a conflict resolver, a resumed session): a
                // second run on the same worktree is never right, whatever the lane says.
                bool alreadyRunning;
                lock (_runningTasks) { alreadyRunning = _runningTasks.Contains(queuedTask.Id); }
                if (alreadyRunning)
                {
                    logger.LogDebug("Skipping task {TaskId} — a run for it is already in flight here", queuedTask.Id);
                    continue;
                }

                // Claim the task using optimistic concurrency
                var claimed = await tasksApi.TryClaimTaskAsync(queuedTask.Id, _leaseOwner, _leaseDuration, ct);
                if (!claimed)
                {
                    logger.LogDebug("Failed to claim task {TaskId} — already claimed by another processor",
                        queuedTask.Id);
                    continue;
                }

                availableInFlight--;
                if (isReview)
                {
                    availableReviews--;
                    logger.LogInformation(
                        "Reviewing task {TaskId} alongside the build lane — its branch is written "
                        + "and the reviewer only reads it",
                        queuedTask.Id);
                }
                else if (runsAhead)
                {
                    availablePrepSlots--;
                    logger.LogInformation(
                        "Planning task {TaskId} ahead — its plan will be ready before a slot frees",
                        queuedTask.Id);
                }
                else
                {
                    availableSlots--;
                    // A banked plan starting its build run stops waiting, so it hands the prep
                    // slot it was holding to the next task in the same sweep — otherwise the
                    // handover this feature exists for would stall one poll every time.
                    if (queuedTask.IsAutopilot && queuedTask.Kind() == TaskPhaseKind.Build)
                        availablePrepSlots++;

                    // Only work holding a slot reserves the repo, matching how activeRepoIds
                    // was seeded.
                    if (queuedTask.RepoId is not null)
                        activeRepoIds.Add(queuedTask.RepoId);
                    logger.LogInformation("Picked up task {TaskId}", queuedTask.Id);
                }

                lock (_runningTasks) { _runningTasks.Add(queuedTask.Id); }

                FireOrchestration(queuedTask.Id,
                    token => orchestrator.ExecuteAsync(queuedTask.Id, token), ct);
            }

            // The one number prep-ahead is judged by: an execution slot standing empty while
            // autopilot work exists means the pipeline planned too slowly, and nothing else in
            // the log says so — a quiet sweep looks identical to a finished board.
            //
            // Named rather than counted, so a session that keeps starving can be traced to the
            // tasks doing it. Raising PlanAheadDepth is the fix when this recurs.
            if (availableSlots > 0 && activeBuilds == 0)
            {
                var planning = activeTasks
                    .Where(t => SpendsPrepBudget(t.IsAutopilot, t.CurrentLane, t.SkipPlanning))
                    .Select(t => t.Id)
                    .ToList();
                if (planning.Count > 0)
                    logger.LogInformation(
                        "Project {ProjectId} has {Free} free execution slot(s) and nothing building "
                        + "— still planning {TaskIds} (depth {PlanAhead})",
                        projectId, availableSlots, string.Join(", ", planning), planAheadDepth);
            }
        }
    }

    internal const string NoProviderSignedIn =
        "Waiting for an AI subscription: no AI CLI is signed in on your runner. Sign in to "
        + "Claude Code (`claude`, then /login), Codex (`codex login`), Gemini (`gemini`) or "
        + "GitHub Copilot (`gh auth login`) on that machine — this task starts on its own once one is ready.";

    /// <summary>Online-anywhere check for parking decisions: DB presence says whether the
    /// user's runner holds a live connection on ANY replica (local affinity only answers
    /// "here"). Cached per poll cycle. Hosts without the Runners module (Orkka Local)
    /// have no IRunnerFleet — treat as offline-nowhere-tracked, i.e. park; unreachable
    /// there anyway because Local's claim affinity is always true.</summary>
    private static async Task<bool> IsRunnerOnlineAnywhereAsync(
        IServiceScope scope, Dictionary<(Ulid OrgId, string UserId), bool> cache,
        Ulid orgId, string userId, CancellationToken ct)
    {
        if (cache.TryGetValue((orgId, userId), out var cached))
            return cached;

        var fleet = scope.ServiceProvider.GetService<Orkka.Modules.Runners.Contracts.IRunnerFleet>();
        var online = fleet is not null
            && (await fleet.GetPresenceAsync(orgId, userId, ct))?.Online == true;
        cache[(orgId, userId)] = online;
        return online;
    }

    /// <summary>
    /// Crash recovery: re-fires tasks that were mid-phase (HasAgentWorking = true, Active)
    /// when the worker restarted. Normal transitions go through Status = InQueue instead.
    /// </summary>
    private static readonly Lane[] ActionableLanes = [Lane.Ready, Lane.Planning, Lane.Developing, Lane.Testing, Lane.CodeReview];

    private async Task ResumeAgentTasksAsync(ITasksApi tasksApi, CancellationToken ct)
    {
        // Only tasks with no live lease are listed — a live lease means another replica
        // is still orchestrating them.
        var resumableTasks = await tasksApi.ListResumableTasksAsync(ActionableLanes, ct);

        if (resumableTasks.Count == 0)
            return;

        foreach (var task in resumableTasks)
        {
            // Skip if already being orchestrated locally
            lock (_runningTasks)
            {
                if (!_runningTasks.Add(task.Id))
                    continue;
            }

            // The listing races with other replicas; the lease acquisition is the
            // authoritative claim — exactly one replica's update matches.
            if (!await tasksApi.TryAcquireTaskLeaseAsync(task.Id, _leaseOwner, _leaseDuration, ct))
            {
                lock (_runningTasks) { _runningTasks.Remove(task.Id); }
                continue;
            }

            logger.LogInformation("Resuming agent task {TaskId} in lane {Lane}", task.Id, task.CurrentLane);

            FireOrchestration(task.Id, token => orchestrator.ExecuteAsync(task.Id, token), ct);
        }
    }

    /// <summary>
    /// Runs an orchestration fire-and-forget: tracks it locally (heartbeat + duplicate
    /// suppression), gives it a per-task cancellation handle (reset sweep), and releases
    /// the task's lease when it ends, however it ends.
    /// </summary>
    /// <summary>
    /// Picks up live interactive sessions that no worker is driving — newly opened ones, and ones
    /// whose worker died mid-conversation (lapsed lease). Once claimed, the session's loop runs
    /// here until it finishes, is abandoned, or this worker stops, with
    /// <see cref="ExtendLocalLeasesAsync"/> holding the lease the whole time. That heartbeat is
    /// the affinity mechanism: every turn must land on the machine holding the warm CLI session.
    ///
    /// Deliberately separate from <see cref="ProcessQueueAsync"/>. A session is Active and
    /// human-driven, so it has no queue position, no dependency gating, and no place in the
    /// parallel-slot accounting that decides which queued work starts next — it already started.
    /// </summary>
    private async Task ProcessInteractiveSessionsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var sessions = await tasksApi.ListInteractiveSessionsAwaitingPickupAsync(ct);
        if (sessions.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;

        foreach (var session in sessions)
        {
            lock (_runningTasks)
            {
                // Already driven here.
                if (_runningTasks.Contains(session.Id)) continue;
            }

            // Held by ANOTHER worker whose lease is still good — theirs to drive.
            //
            // A lease in our own name is deliberately not a reason to skip: we just established
            // we are not running it, which means we restarted and left it behind. Waiting for our
            // own stale lease to expire froze the session for the whole TTL after every deploy,
            // with a human watching a chat that had stopped answering.
            var heldByAnother = session.LeaseOwner is not null
                && session.LeaseOwner != _leaseOwner
                && session.LeaseExpiresAt > now;
            if (heldByAnother) continue;

            // A session streams to its creator's runner, so only the replica holding that
            // connection may drive it. Offline everywhere is not parked as WaitingForRunner the
            // way a queued task is: the human is sitting in front of the session, and the
            // honest outcome is to leave it open until their runner comes back.
            if (session.RunnerUserId is null
                || !claimAffinity.CanExecuteHere(session.OrgId, session.RunnerUserId))
            {
                continue;
            }

            if (!await tasksApi.TryAcquireTaskLeaseAsync(session.Id, _leaseOwner, _leaseDuration, ct))
            {
                logger.LogDebug(
                    "Interactive session {TaskId} claimed by another worker", session.Id);
                continue;
            }

            lock (_runningTasks) { _runningTasks.Add(session.Id); }

            logger.LogInformation("Picked up interactive session {TaskId}", session.Id);

            // The session's worktree is on the user's machine, so it may move freely between
            // replicas — and must, since the runner can reconnect to any of them.
            FireOrchestration(session.Id,
                token => orchestrator.RunInteractiveSessionAsync(session.Id, token), ct);
        }
    }

    private void FireOrchestration(
        Ulid taskId, Func<CancellationToken, Task> run, CancellationToken ct)
    {
        // Not started once the host is stopping; but a run that has started is deliberately
        // NOT linked to the stopping token — a deploy drains (WorkerDrain), so runs finish on
        // their own clock and are only cancelled by the reset sweep or, once the host really
        // stops, by StopInFlightRunsAsync.
        if (ct.IsCancellationRequested)
        {
            lock (_runningTasks) { _runningTasks.Remove(taskId); }
            return;
        }

        var cts = new CancellationTokenSource();
        _runCancellations[taskId] = cts;
        var tracked = drain.Track($"task {taskId}");
        MarkPollProductive();

        _ = Task.Run(async () =>
        {
            try
            {
                await run(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                logger.LogInformation("Orchestration for task {TaskId} cancelled", taskId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception in orchestration for task {TaskId}", taskId);
            }
            finally
            {
                _runCancellations.TryRemove(taskId, out _);
                cts.Dispose();
                lock (_runningTasks) { _runningTasks.Remove(taskId); }

                // The local stand-in for the task-completion signal this process has no other
                // way to hear: a run that just ended may have met a dependency, freed a
                // concurrency slot, or produced a PR to create. Poll fast on the next cycle.
                MarkPollProductive();
                await ReleaseLeaseAsync(taskId);
                tracked.Dispose();
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// User-requested resets (two-phase: the API endpoint flags the task, each worker
    /// polls). Cancels a locally running orchestration first and finalizes on a later
    /// poll — the wipe must never race a run that is still writing. Worktree cleanup
    /// precedes the finalize; a proxied cleanup failure (runner offline) leaves the task
    /// reset-pending for retry. Runs on every replica: runner links are per replica, and
    /// FinalizeTaskResetAsync is single-winner via its ResetRequestedAt guard.
    ///
    /// Every task gets one more step before the wipe: the run may still be executing on
    /// the user's runner even though no replica tracks it — the replica that started it was
    /// rolled by a deploy, and its replacement carries the same pod name (so the lease reads
    /// as its own) and an empty _runCancellations. The runner is asked to cancel by TASK id
    /// and waits for the agent process to exit; only then is the worktree removed. An offline
    /// runner fails that step the same way it fails the cleanup, and the task stays
    /// reset-pending — but only for <see cref="ResetRunnerOfflineGrace"/>. A runner that
    /// blinked is back well inside that; one the user STOPPED never comes back on its own,
    /// and a reset that waits for that runner forever traps them. Past the grace the
    /// reset finalizes without the runner-side cleanup: the worktree on that machine is the
    /// user's own disk, and the runner's stale-worktree GC is the backstop for it.
    /// </summary>
    internal async Task ProcessResetRequestsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var pending = await tasksApi.ListResetPendingTasksAsync(ct);
        if (pending.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;

        foreach (var task in pending)
        {
            // In flight on this replica — cancel and let the run exit before touching data.
            if (_runCancellations.TryGetValue(task.Id, out var cts))
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { /* run just exited — finalize next poll */ }

                logger.LogInformation("Reset: cancelling in-flight run for task {TaskId}", task.Id);
                MarkPollProductive();
                continue;
            }

            // Claimed locally but its CTS isn't registered yet, or it is mid-teardown.
            lock (_runningTasks)
            {
                if (_runningTasks.Contains(task.Id))
                    continue;
            }

            // A live lease elsewhere means another replica's run is still winding down.
            if (task.LeaseOwner is not null && task.LeaseOwner != _leaseOwner
                && task.LeaseExpiresAt is { } expiry && expiry > now)
                continue;

            // A Story member's previous attempt stays on the shared branch (it re-plans on top of
            // it): its commit range is read before the clone goes, for the note after the reset.
            (string From, string To)? previousAttempt = null;

            // A null branch means no phase ever created a worktree — nothing to clean.
            if (task.RepoId is { } repoId && task.BranchName is not null)
            {
                try
                {
                    var snapshot = await tasksApi.GetTaskSnapshotAsync(task.Id, ct);
                    if (snapshot is null)
                        continue;

                    // Route like the orchestrator does so the task cleans up on its owner's
                    // machine. The runner proxy throws when the daemon is offline, which
                    // keeps the task reset-pending.
                    var route = executionRouter.Resolve(snapshot);

                    // A runner run outlives every replica, so stop it before the wipe.
                    logger.LogInformation(
                        "Reset: stopping any run of task {TaskId} still executing on the runner before worktree cleanup",
                        task.Id);
                    await route.Invoker.CancelTaskRunsAsync(task.Id, ct);

                    if (snapshot is { StoryOrder: not null, ReviewBaseSha: { } attemptBase })
                    {
                        // The clone's origin/{branch} is this member's last push.
                        var attemptHead = await route.Git.ResolveReviewBaseAsync(
                            task.OrgId, repoId, task.Id, task.BranchName, snapshot.BaseBranch ?? "main", ct);
                        if (attemptHead is not null && attemptHead != attemptBase)
                            previousAttempt = (attemptBase, attemptHead);
                    }

                    await route.Git.CleanupWorktreeAsync(task.OrgId, repoId, task.Id);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (
                    task.ResetRequestedAt is { } requestedAt
                    && now - requestedAt >= ResetRunnerOfflineGrace)
                {
                    logger.LogWarning(ex,
                        "Reset: task {TaskId}'s runner has not answered for {Elapsed:F0}s since the reset was "
                        + "requested — finalizing WITHOUT the runner-side cancel/cleanup; the worktree on that "
                        + "machine is left for its stale-worktree GC",
                        task.Id, (now - requestedAt).TotalSeconds);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Reset: run cancel / worktree cleanup failed for task {TaskId} — will retry next poll", task.Id);
                    continue;
                }
            }

            if (!await tasksApi.FinalizeTaskResetAsync(task.Id, ct))
                continue; // another replica finalized first

            logger.LogInformation("Reset: task {TaskId} finalized back to Drafting", task.Id);
            MarkPollProductive();

            if (task.BranchName is { } storyBranch
                && await tasksApi.GetStoryForTaskAsync(task.Id, ct) is not null)
            {
                await tasksApi.AddTaskMessageAsync(task.Id, "system",
                    StoryResetNote(storyBranch, previousAttempt), MessageType.SystemEvent, ct: ct);
            }

            await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                task.Id, task.Title, task.CurrentLane.ToString(), Lane.Drafting.ToString(),
                Orkka.Modules.Tasks.Contracts.TaskStatus.Draft.ToString(), DateTimeOffset.UtcNow));
            await taskNotifier.SendStatusChangedAsync(task.ProjectId, new StatusChangedEvent(
                task.Id, Orkka.Modules.Tasks.Contracts.TaskStatus.Draft.ToString(), DateTimeOffset.UtcNow));
        }
    }

    /// <summary>
    /// The thread note a reset Story member starts over with. Its earlier commits are NOT taken
    /// off the shared branch: they were reviewed and pushed, and Orkka never rewrites a shared
    /// branch (no force-push) — the new attempt re-plans on top of it, so the developer is told
    /// which commits the previous attempt left there and how to have them undone.
    /// </summary>
    internal static string StoryResetNote(string branch, (string From, string To)? previousAttempt)
    {
        var range = previousAttempt is { } r
            ? $"Its previous attempt's commits stay on `{branch}`: `{Short(r.From)}..{Short(r.To)}` "
              + $"(`git log {Short(r.From)}..{Short(r.To)}`)."
            : $"Anything its previous attempt pushed stays on `{branch}`.";
        return $"This sub-task was reset. {range} The story's branch is shared, so Orkka never rewrites it: the next "
            + "attempt re-plans on top of the branch as it is now, previous commits included. To drop that work, say so "
            + "in the task (for example \"revert the previous attempt's commits first\") and the agent undoes it with new "
            + "commits, reviewed like any other change.";

        static string Short(string sha) => sha.Length > 10 ? sha[..10] : sha;
    }

    /// <summary>
    /// Re-queues tasks the Anthropic API knocked over: a run that exhausts the in-run
    /// transient retry budget (3 resume attempts, see "Transient agent-run failure" in
    /// <see cref="TaskOrchestrator"/>) parks its task as Blocked, and during an unstable
    /// stretch every such task then waits for a human to press Retry — for a failure the
    /// human can do nothing about except press Retry. This sweep presses it for them, with
    /// the same semantics: Status back to InQueue in the current lane, and the next run
    /// resumes the failed run's CLI session, so no work is lost.
    ///
    /// Three things bound it. Only errors the in-run retry itself classified as transient
    /// (<see cref="Orkka.SharedKernel.Ai.ClaudeCliErrors.IsTransient"/>) qualify — a genuine
    /// defect must stay Blocked in front of a human, because re-running it re-bills a run
    /// that fails the same way. The failure must be at least the backoff old, so a live
    /// incident is waited out rather than hammered. And the rounds are counted on the task
    /// row — after the cap it stays Blocked for a human, whose Retry resets the budget.
    /// </summary>
    internal async Task AutoRecoverApiErrorTasksAsync(CancellationToken ct)
    {
        if (!_autoRecoverApiErrors)
            return;

        var now = DateTimeOffset.UtcNow;
        if (now < _nextAutoRecoverSweep)
            return;
        _nextAutoRecoverSweep = now + AutoRecoverSweepInterval;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();

            var candidates = await tasksApi.ListAutoRecoverableBlockedTasksAsync(
                now - _autoRecoverBackoff, _autoRecoverMaxRounds, ct);

            foreach (var task in candidates)
            {
                // A failure the human can only press Retry on: a provider blip the in-run retry
                // could not outlast.
                // A transient failure of the REPOSITORY, not the provider: a mirror another task
                // was refreshing at that instant, a ref lock, a dropped fetch. The run's own
                // retry would have called it transient; a task parked on it needs a human to
                // press Retry for nothing (prod, 2026-09-16: two tasks refreshed one mirror in
                // the same second, one failed "Permission denied" and sat Blocked for an hour).
                var transientWorkspace = IsTransientWorkspaceFailure(task.LastError);
                if (!Orkka.SharedKernel.Ai.ClaudeCliErrors.IsTransient(task.LastError)
                    && !transientWorkspace)
                    continue;

                // Belt and braces — a Blocked task has no local run, but never re-queue
                // one this replica somehow still tracks.
                lock (_runningTasks)
                {
                    if (_runningTasks.Contains(task.Id))
                        continue;
                }

                if (!await tasksApi.TryAutoRecoverBlockedTaskAsync(task.Id, _autoRecoverMaxRounds, ct))
                    continue;

                MarkPollProductive();

                var round = task.AutoRecoveryCount + 1;
                logger.LogWarning(
                    "Auto-recovered task {TaskId} from {Kind} (round {Round}/{Max}): {Error}",
                    task.Id,
                    transientWorkspace ? "a transient repository failure"
                        : "a transient provider failure",
                    round, _autoRecoverMaxRounds, task.LastError);

                try
                {
                    await tasksApi.AddTaskMessageAsync(task.Id, "system",
                        (transientWorkspace
                            ? "The machine running this task could not open its repository for a "
                              + "moment; the platform re-queued it automatically "
                            : "The AI provider had an outage that interrupted this task; the platform "
                              + "re-queued it automatically ")
                        + $"(attempt {round} of {_autoRecoverMaxRounds}). "
                        + "The run continues where it left off.",
                        MessageType.SystemEvent, ct: ct);
                }
                catch (Exception ex)
                {
                    // The recovery already happened and the board shows it; a missing
                    // timeline line must not make the sweep spend another round on it.
                    logger.LogWarning(ex,
                        "Could not write the auto-recovery note for task {TaskId}", task.Id);
                }

                await taskNotifier.SendStatusChangedAsync(task.ProjectId, new StatusChangedEvent(
                    task.Id, Orkka.Modules.Tasks.Contracts.TaskStatus.InQueue.ToString(),
                    DateTimeOffset.UtcNow));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-recovery sweep failed");
        }
    }

    /// <summary>
    /// A last error the git engine would call transient: either it carries an engine code
    /// prefix ("Network: …", "Timeout: …" — the wording every workspace failure reaches the
    /// task with), or its text classifies as one.
    /// </summary>
    internal static bool IsTransientWorkspaceFailure(string? error)
    {
        if (string.IsNullOrEmpty(error))
            return false;
        foreach (var code in Enum.GetValues<Orkka.Git.GitErrorCode>())
        {
            if (Orkka.Git.GitErrorClassifier.IsTransient(code)
                && error.Contains($"{code}:", StringComparison.Ordinal))
                return true;
        }
        return Orkka.Git.GitErrorClassifier.IsTransient(Orkka.Git.GitErrorClassifier.Classify(error));
    }

    /// <summary>
    /// Runs the documentation pass over tasks whose work has settled.
    ///
    /// Deliberately a pull, not a push. The nine code paths that put a task in Done live in the
    /// API process, the Worker, and a forge webhook, and none of them can be trusted to remember
    /// a side effect that has nothing to do with what they were doing; a sweep that reads the
    /// same settled state they all converge on cannot forget.
    ///
    /// The lookback window and the per-sweep cap together bound the blast radius of enabling the
    /// setting on a busy project: recent work is documented, history is left alone, and a backlog
    /// drains a few tasks at a time instead of all at once.
    /// </summary>
    private async Task ProcessDocumentationSyncAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextDocumentationSweep)
            return;

        _nextDocumentationSweep = now + TimeSpan.FromSeconds(
            Math.Max(1, _documentation.SweepIntervalSeconds));

        try
        {
            using var scope = scopeFactory.CreateScope();
            var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();

            // A pass is two model calls at most; anything still Running well past that is a
            // worker that died holding the claim, not one still working.
            var staleBefore = now - TimeSpan.FromMinutes(
                Math.Max(5, _documentation.AbandonedAfterMinutes));

            var candidates = await tasksApi.ListTasksAwaitingDocSyncAsync(
                now - TimeSpan.FromHours(Math.Max(1, _documentation.LookbackHours)),
                _documentation.MaxAttempts, staleBefore, ct);

            if (candidates.Count == 0)
                return;

            var started = 0;
            foreach (var candidate in candidates)
            {
                if (started >= _documentation.MaxTasksPerSweep)
                {
                    logger.LogDebug(
                        "Documentation sweep capped at {Cap} task(s); {Remaining} wait for the next cycle",
                        _documentation.MaxTasksPerSweep, candidates.Count - started);
                    break;
                }

                // Never for a task this replica is already orchestrating: a documentation run
                // alongside a live phase would read a task mid-write.
                lock (_runningTasks)
                {
                    if (_runningTasks.Contains(candidate.Id))
                        continue;
                }

                if (!await tasksApi.TryClaimDocSyncAsync(candidate.Id, staleBefore, ct))
                    continue;

                started++;
                MarkPollProductive();

                // Fire-and-forget like every other orchestration, but WITHOUT the task lease:
                // documentation reads a finished task and writes only to the Documents module,
                // so holding the task's lease would block a human reopening it for no reason.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await documentationSync.RunAsync(candidate, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
                            "Unhandled exception in documentation sync for task {TaskId}", candidate.Id);
                    }
                }, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Documentation sweep failed");
        }
    }

    private async Task ReleaseLeaseAsync(Ulid taskId)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
            await tasksApi.ReleaseTaskLeaseAsync(taskId, _leaseOwner, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Best effort — an unreleased lease simply expires after the TTL.
            logger.LogWarning(ex, "Failed to release lease for task {TaskId}", taskId);
        }
    }

    /// <summary>Heartbeat: keeps this replica's in-flight orchestrations leased.</summary>
    private async Task ExtendLocalLeasesAsync(CancellationToken ct)
    {
        List<Ulid> running;
        lock (_runningTasks) { running = [.. _runningTasks]; }

        if (running.Count == 0)
            return;

        using var scope = scopeFactory.CreateScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        await tasksApi.ExtendTaskLeasesAsync(running, _leaseOwner, _leaseDuration, ct);
    }

    /// <summary>
    /// Finds tasks in CodeReview/Done that have a branch but no pull request, and opens Orkka's
    /// own. The orchestrator opens it at the end of Developing; this is the backstop for a run
    /// that ended between its push and the create.
    /// </summary>
    private async Task CreateMissingPullRequestsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var tasks = await tasksApi.ListTasksAwaitingPrAsync(ct);

        if (tasks.Count == 0) return;

        var pullRequests = scope.ServiceProvider.GetRequiredService<IPullRequestService>();
        var projectsApi = scope.ServiceProvider.GetRequiredService<IProjectsApi>();
        var repoCache = new Dictionary<Ulid, RepoSummary?>();

        foreach (var task in tasks)
        {
            // A create that just failed cannot succeed seconds later — the causes change on
            // human or network timescales.
            if (_prCreateBackoff.TryGetValue(task.Id, out var backoff)
                && backoff.NextAttempt > DateTimeOffset.UtcNow)
                continue;

            var repo = task.RepoId is { } repoId ? await GetRepoCachedAsync(projectsApi, repoCache, repoId, ct) : null;
            if (repo is null || string.IsNullOrEmpty(task.BranchName))
            {
                // The list query already filters on RepoId/BranchName, so landing here means
                // the repo row itself is gone — a permanent condition this sweep can never
                // clear. Silently `continue`-ing was how tasks sat "in code review without an
                // open PR" with no visible reason.
                await MergeFailureNotifications.NotifyMergeFailureAsync(tasksApi, taskNotifier,
                    task.Id, "A pull request cannot be opened: the task's repository is no "
                    + "longer linked to the project.", ct);
                continue;
            }

            try
            {
                // The task named explicitly (a Story's members share one branch), with a member's
                // starting commit so its review shows only its own change.
                var reviewBaseSha = (await tasksApi.GetTaskSnapshotAsync(task.Id, ct))?.ReviewBaseSha;
                var prResult = await pullRequests.CreateForTaskAsync(
                    repo.Id, task.Id, task.BranchName, task.BaseBranch ?? repo.DefaultBranch,
                    task.Title, $"""
                        ## Task
                        **{task.Title}** ({task.Type})

                        {task.Description ?? "No description provided."}
                        """, reviewBaseSha, ct);

                if (prResult.IsSuccess)
                {
                    _prCreateBackoff.TryRemove(task.Id, out _);
                    var prUrl = prResult.Value!.HtmlUrl;
                    await tasksApi.SetTaskPrUrlAsync(task.Id, prUrl, ct);
                    logger.LogInformation("Created missing PR for task {TaskId}: {PrUrl}", task.Id, prUrl);
                    MarkPollProductive();
                    await AuditPrCreatedAsync(task.OrgId, task.ProjectId, task.Id, prUrl);
                }
                else
                {
                    BumpPrCreateBackoff(task.Id);
                    logger.LogWarning("Failed to create PR for task {TaskId}: {Error}", task.Id, prResult.Error);
                    await MergeFailureNotifications.NotifyMergeFailureAsync(tasksApi, taskNotifier,
                        task.Id, $"The pull request could not be created: {prResult.Error}", ct);
                }
            }
            catch (Exception ex)
            {
                BumpPrCreateBackoff(task.Id);
                logger.LogWarning(ex, "Error creating PR for task {TaskId}", task.Id);
                await MergeFailureNotifications.NotifyMergeFailureAsync(tasksApi, taskNotifier,
                    task.Id, $"The pull request could not be created: {ex.Message}", ct);
            }
        }
    }

    /// <summary>
    /// Sort key for the merge cycle: gated group members first, in dependency order, then
    /// everything else. Two members of the same group compare by their position in the group's
    /// own merge order, so the side that defines an interface lands before the side that calls it.
    /// </summary>
    private static (int Tier, int Position) MergeRank(
        MergeCandidateTask task, IReadOnlyDictionary<Ulid, TaskGroupMergeState> states)
    {
        if (task.GroupId is not { } groupId || !states.TryGetValue(groupId, out var state))
            return (1, 0);

        // IReadOnlyList has no IndexOf, and the spans overload does not apply to it.
        var position = -1;
        for (var i = 0; i < state.MergeOrder.Count; i++)
        {
            if (state.MergeOrder[i] == task.Id)
            {
                position = i;
                break;
            }
        }

        return (0, position < 0 ? int.MaxValue : position);
    }

    /// <summary>
    /// Snapshots a PR's conflicts and starts one automatic resolution run.
    ///
    /// Shared by the two callers that reach it from opposite directions: a merge that failed,
    /// and a group member whose merge was never attempted because the gate is holding it. The
    /// second is why this had to stop being inline — a held member still has to have its
    /// conflicts found, or the group waits on a break nobody looked for.
    /// </summary>
    private async Task HandleMergeConflictsAsync(
        IServiceScope scope, ITasksApi tasksApi, ITaskNotifier taskNotifier,
        ITaskOrchestrator orchestrator, MergeCandidateTask task, RepoSummary repo,
        CancellationToken ct)
    {
        // A run for this task is in flight on this replica — the resolver fired a cycle ago, or
        // a dev pass is reworking the collision. Nothing here may act on it: the one-shot cap
        // below would otherwise hand the task back to Developing on the very next cycle while
        // the resolver is still working the same worktree (prod, 2026-09-15: two agents in one
        // worktree, the resolver's merge cleaned the tree from under the dev run, and a task
        // whose code was merged ended Blocked). The resolver reports its own outcome when it
        // ends; the sweep acts on that, not before.
        lock (_runningTasks)
        {
            if (_runningTasks.Contains(task.Id)) return;
        }

        // The last automatic attempt never ran: the environment cut it off (the runner dropped
        // mid-resolution — the resolver records that as EnvironmentBlocked). It gets its attempt
        // back. Counting it sent the conflict to a full dev pass the moment the runner returned,
        // for a collision the resolver had not been allowed to look at (prod, 2026-09-17).
        if (task.HasMergeFailure && task.MergeFailureKind == MergeFailureKind.EnvironmentBlocked
            && _autoResolveAttempts.TryRemove(task.Id, out _))
        {
            logger.LogInformation(
                "Task {TaskId}: its last automatic resolution was cut off by the environment — trying again",
                task.Id);
        }

        // Capture what collided (files + plain-language summary) for the merge page.
        await SnapshotMergeConflictsAsync(scope, tasksApi, task, repo, ct);

        // The standalone resolver gets one shot. It is a cold agent — it sees two sides of a
        // diff and nothing about why either exists — so when it cannot land the merge, the next
        // move is not to park the task and wait for someone to notice. It is to give the
        // collision back to a dev agent, which reads it as work: the worktree is still there,
        // the session still resumes, and SetupGitWorkspaceAsync stages the conflicted merge for
        // it on the way in. RequeueForBaseSyncAsync bounds the laps and parks the task on the
        // merge page, exactly as before, once they are spent.
        if (_autoResolveAttempts.TryGetValue(task.Id, out var autoAttempts)
            && autoAttempts >= MaxAutoResolveAttempts)
        {
            // A Story's conflict in another repository is not this member's code: a dev pass on
            // it cannot reach that repository. It waits on the merge page for the developer
            // (the story's pull requests stay drafts meanwhile).
            if (_storyConflicts.ContainsKey(task.Id))
            {
                await MergeFailureNotifications.NotifyMergeFailureAsync(tasksApi, taskNotifier, task.Id,
                    "The story's branch still does not merge into the default branch of another of its repositories "
                    + "after automatic resolution. Merge the default branch into the story's branch there, push it, "
                    + "and the story's pull requests are marked ready on the next cycle.", ct);
                return;
            }
            await RequeueForConflictReworkAsync(tasksApi, taskNotifier, task, repo, ct);
            return;
        }

        logger.LogInformation("PR for task {TaskId} has merge conflicts — starting conflict resolution", task.Id);

        await MergeFailureNotifications.NotifyMergeFailureAsync(tasksApi, taskNotifier, task.Id,
            "Merge conflicts with the base branch — attempting automatic resolution", ct);

        lock (_runningTasks)
        {
            if (!_runningTasks.Add(task.Id)) return;
        }

        if (!await tasksApi.TryAcquireTaskLeaseAsync(task.Id, _leaseOwner, _leaseDuration, ct))
        {
            lock (_runningTasks) { _runningTasks.Remove(task.Id); }
            return;
        }

        _autoResolveAttempts.AddOrUpdate(task.Id, 1, (_, n) => n + 1);
        FireOrchestration(task.Id,
            token => orchestrator.ResolveConflictsAndMergeAsync(task.Id, token), ct);
    }

    /// <summary>
    /// Hands an unresolved merge collision back to a dev agent as ordinary work.
    ///
    /// The conflict is written into the conversation rather than only onto the merge page,
    /// because a re-entered Developing run resumes its previous session and reads what is new
    /// there — this is the message that tells it why it woke up. What to actually DO about it
    /// arrives separately and more usefully: the phase's base-branch sync stages the conflicted
    /// merge in the worktree before the agent's first turn.
    /// </summary>
    private async Task RequeueForConflictReworkAsync(
        ITasksApi tasksApi, ITaskNotifier taskNotifier,
        MergeCandidateTask task, RepoSummary repo, CancellationToken ct)
    {
        var snapshot = await tasksApi.GetTaskSnapshotAsync(task.Id, ct);
        if (snapshot is null) return;

        var files = ReadConflictedPaths(snapshot.MergeConflictsJson);
        var fileList = files.Count == 0
            ? ""
            : "\n\nConflicted files:\n" + string.Join("\n", files.Select(f => $"- `{f}`"));

        var reason =
            $"Automatic conflict resolution could not merge '{repo.DefaultBranch}' into this branch.";

        var requeue = await tasksApi.RequeueForBaseSyncAsync(
            task.Id, TaskOrchestrator.MaxBaseSyncCycles, reason, ct);
        if (requeue is null) return;

        _autoResolveAttempts.TryRemove(task.Id, out _);

        if (requeue.EscalatedToHuman)
        {
            logger.LogWarning(
                "Task {TaskId} has spent its {Max} conflict-rework passes — parked on the merge page",
                task.Id, TaskOrchestrator.MaxBaseSyncCycles);
            return;
        }

        await tasksApi.AddTaskMessageAsync(
            task.Id, "system",
            $"This branch no longer merges into `{repo.DefaultBranch}` and the automatic resolver "
            + $"could not finish it. Re-opened for another development pass (attempt "
            + $"{requeue.BaseSyncCycleCount}/{TaskOrchestrator.MaxBaseSyncCycles}): reconcile your work "
            + $"with what has landed on `{repo.DefaultBranch}` since you branched, then finish the task."
            + fileList,
            MessageType.SystemEvent, ct: ct);

        await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
            task.Id, snapshot.Title, requeue.FromLane.ToString(), requeue.NewLane.ToString(),
            requeue.NewStatus.ToString(), DateTimeOffset.UtcNow));

        logger.LogInformation(
            "Task {TaskId}: unresolved merge conflict handed back to Developing (pass {Attempt}/{Max})",
            task.Id, requeue.BaseSyncCycleCount, TaskOrchestrator.MaxBaseSyncCycles);
    }

    /// <summary>Conflicted paths from the snapshot the merge page reads. Empty on any problem —
    /// this only enriches a message, and none of it is worth failing a requeue over.</summary>
    private static IReadOnlyList<string> ReadConflictedPaths(string? conflictsJson)
    {
        try
        {
            if (string.IsNullOrEmpty(conflictsJson))
                return [];

            var parsed = System.Text.Json.JsonSerializer.Deserialize<MergeConflictSnapshot>(
                conflictsJson, ConflictJsonOptions);
            return parsed is null ? [] : [.. parsed.Files.Select(f => f.Path)];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Finds tasks whose pull request passed review and lands them: a squash merge into a
    /// folder, or — for a remote repository — publishing the work as a forge pull request the
    /// developer's runner opens with their own CLI. One per repo per cycle, oldest first.
    /// </summary>
    internal async Task MergeUnmergedPullRequestsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var pullRequests = scope.ServiceProvider.GetRequiredService<IPullRequestService>();
        var projectsApi = scope.ServiceProvider.GetRequiredService<IProjectsApi>();
        var taskNotifier = scope.ServiceProvider.GetRequiredService<ITaskNotifier>();
        var repoCache = new Dictionary<Ulid, RepoSummary?>();

        // Only tasks that already passed review — merging one still under review would
        // bypass the review stage entirely. That is Done, plus the ones a failed merge sent
        // back out of Done (they carry a failure reason or an armed resolution, neither of
        // which a task awaiting its first review ever has).
        var tasks = await tasksApi.ListTasksAwaitingMergeAsync(ct);

        if (tasks.Count == 0) return;

        // Cross-repo groups land all-or-nothing: a member's PR is held until every sibling can
        // land too, then they go in dependency order. Loaded once per cycle rather than per task
        // — a two-member group would otherwise ask the same question twice.
        var groupStates = (await tasksApi.GetGroupMergeStatesAsync(
                tasks.Where(t => t.GroupId is not null).Select(t => t.GroupId!.Value).Distinct().ToList(), ct))
            .ToDictionary(s => s.GroupId);

        // Gated members sort provider-first, and ahead of ungrouped work. They are in distinct
        // repos by construction, so the one-merge-per-repo rule below never splits a group
        // across cycles — the whole set lands in this one.
        tasks = [.. tasks.OrderBy(t => MergeRank(t, groupStates))];

        // Track which repos we've already processed this cycle to merge one at a time per repo
        var processedRepos = new HashSet<Ulid>();
        var mergedThisCycle = new HashSet<Ulid>();
        // Tasks the within-cycle group hold released since last cycle: logged again next time.
        _cycleHoldLogged.IntersectWith(tasks.Select(t => t.Id));

        for (var i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];

            // Under a Gated policy the group holds this PR until every sibling is landable.
            // Deliberately NOT a `continue`: the checks below still run, because a member whose
            // branch has drifted or conflicted has to be found and fixed now. Skipping the task
            // outright would leave the group waiting on a break nobody went looking for.
            var heldByGate = task.GroupId is { } gid
                && groupStates.TryGetValue(gid, out var gateState)
                && gateState.MergePolicy == TaskGroupMergePolicy.Gated
                && !gateState.Ready;

            // All-or-nothing WITHIN the cycle as well. The gate reads the siblings' state —
            // finished, not stuck — and cannot see that the half ahead of this one is failing
            // to land right now on the retry path: a dirty working copy on the user's machine
            // (prod, 2026-09-16, StoreFront #15) held the API half while the UI half went onto
            // main alone. A gated member lands only after every sibling ahead of it in the
            // merge order landed this cycle; until then it is held exactly like a gated member.
            if (!heldByGate && task.GroupId is { } sg
                && groupStates.TryGetValue(sg, out var siblingState)
                && siblingState.MergePolicy == TaskGroupMergePolicy.Gated
                && tasks.Take(i).Any(t => t.GroupId == sg && !mergedThisCycle.Contains(t.Id)))
            {
                // Once per hold, not once per cycle: a sibling in a rework pass holds this task
                // for as long as that pass runs, and the sweep comes round every few seconds.
                if (_cycleHoldLogged.Add(task.Id))
                    logger.LogInformation(
                        "Task {TaskId} is held — a sibling in its cross-repo group has not landed ahead of it",
                        task.Id);
                heldByGate = true;
            }

            var repo = task.RepoId is { } repoId ? await GetRepoCachedAsync(projectsApi, repoCache, repoId, ct) : null;
            if (repo is null || string.IsNullOrEmpty(task.PrUrl)) continue;

            // Sent back out of Done to rework a collision (or a failed check) and not back yet:
            // that lap has to finish before the sweep looks at its PR again. Acting now re-flags
            // the same conflict every cycle while the agent is mid-fix — prod, 2026-09-15: eight
            // "starting conflict resolution" lines in forty seconds for one task, each writing a
            // merge-failure notice. A human-armed resolution is the one thing that still runs
            // from any lane; an escalated task stays in Done and is still looked at.
            // Judged by STATUS, not lane: a rework pass is InQueue/Active; a task parked on the
            // merge page (WaitingCodeReview in CodeReview) is exactly what the sweep must keep
            // looking at — "retrying every cycle" depends on it.
            if (task.Status is Orkka.Modules.Tasks.Contracts.TaskStatus.InQueue or Orkka.Modules.Tasks.Contracts.TaskStatus.Active
                && task.MergeResolutionStrategy is null)
                continue;

            // Only merge one PR per repo per cycle to avoid conflicts from parallel merges.
            //
            // A member the group gate is holding lands nothing this cycle, so it must not take the
            // repo's one merge. It did, every cycle: a held API half completed earlier than a READY
            // pair's API half in the same repository, sorted ahead of it, and the ready half was
            // skipped forever — while the held half's own partner waited, through a dependency, on
            // exactly that merge (prod, 2026-09-17, StoreFront: #30 held, #28 never merged, #29,
            // #34 and #35 never started). A held member still steps aside for a repo a real merge
            // has already claimed.
            if (task.RepoId is { } claimRepoId
                && (heldByGate ? processedRepos.Contains(claimRepoId) : !processedRepos.Add(claimRepoId)))
            {
                logger.LogDebug("Skipping merge for task {TaskId} — repo {RepoId} already had a merge this cycle",
                    task.Id, task.RepoId);
                continue;
            }

            // The task's own run owns its merge. A finished task whose orchestration is running
            // on this replica is in conflict resolution: the resolver pushes the resolved branch
            // and then merges it itself, and in the seconds between the two the PR reads
            // mergeable again. The sweep merged it first (prod, 2026-09-16, StoreFront #19), so
            // the resolver's own push into the user's folder was refused NonFastForward and a
            // "Merge failed" notice landed on a task that had just merged. Leave the task to its
            // run — and, since the repo is claimed above, its repository too: a sibling landing
            // in the same folder at that moment races the resolver's push.
            bool inFlightHere;
            lock (_runningTasks) { inFlightHere = _runningTasks.Contains(task.Id); }
            if (inFlightHere)
            {
                logger.LogDebug("Skipping merge for task {TaskId} — its own run is in flight on this replica", task.Id);
                continue;
            }

            // A human picked a resolution on the merge page — run it now. Bypasses the
            // auto-attempt cap: an explicit choice always deserves a fresh run.
            if (task.MergeResolutionStrategy is not null)
            {
                lock (_runningTasks)
                {
                    if (!_runningTasks.Add(task.Id)) continue;
                }
                if (!await tasksApi.TryAcquireTaskLeaseAsync(task.Id, _leaseOwner, _leaseDuration, ct))
                {
                    lock (_runningTasks) { _runningTasks.Remove(task.Id); }
                    continue;
                }

                logger.LogInformation(
                    "Running human-picked merge resolution '{Strategy}' for task {TaskId}",
                    task.MergeResolutionStrategy, task.Id);
                _autoResolveAttempts.TryRemove(task.Id, out _);
                FireOrchestration(task.Id, token => orchestrator.ResolveConflictsAndMergeAsync(task.Id, token), ct);
                continue;
            }

            try
            {
                var prNumber = ParsePrNumber(task.PrUrl);
                if (prNumber is null) continue;

                var prDetail = await pullRequests.GetAsync(repo.Id, prNumber.Value, ct);
                if (prDetail.IsFailure) continue;

                var pr = prDetail.Value!;

                // A conflict the PR no longer has is over. The next one is a new collision — the
                // base moved again — and gets its own automatic resolution. Without this the one
                // attempt stayed spent after the resolver had SUCCEEDED: a gated member held for
                // its partner resolved a conflict at 06:00, main moved at 07:31, and the new
                // conflict went straight to a dev pass (prod, 2026-09-17, StoreFront #30). A member
                // held long enough burns all three dev passes that way and parks for a human.
                var otherRepoConflict = _storyConflicts.ContainsKey(task.Id);
                if (pr.Mergeable == true && !otherRepoConflict)
                    _autoResolveAttempts.TryRemove(task.Id, out _);

                // A conflict notice the resolver has since cleared up: the PR merges cleanly
                // again, and the standing notice would keep counting the task as stuck for its
                // group's gate (and keep it out of Done). Clear it; the gate re-reads next cycle.
                if (pr.Mergeable == true && !otherRepoConflict && task.HasMergeFailure
                    && (task.MergeFailureKind ?? MergeFailureKind.Unknown) == MergeFailureKind.Unknown)
                {
                    if (await tasksApi.ClearTaskMergeFailureAsync(task.Id, ct))
                        logger.LogInformation(
                            "Task {TaskId}: its pull request merges cleanly again — cleared the stale conflict notice",
                            task.Id);
                }

                // Landed elsewhere (merged from the task or repo page) — record it and clean up.
                if (pr.Merged)
                {
                    // Published from elsewhere (the task page's own Merge click): keep its forge PR.
                    if (pr.ForgeUrl is { } landedForgeUrl)
                        await tasksApi.SetTaskPublishedPrUrlAsync(task.Id, landedForgeUrl, ct);
                    await tasksApi.MarkTaskMergedAsync(task.Id, pr.MergeCommitSha, ct);
                    mergedThisCycle.Add(task.Id);
                    _autoResolveAttempts.TryRemove(task.Id, out _);
                    logger.LogInformation(
                        "PR for task {TaskId} already landed — marked as merged", task.Id);
                    await AuditMergedAsync(task);
                    await CleanupWorktreeAsync(tasksApi, task, ct);
                    if (task.GroupId is { } externallyMergedGroupId)
                        await tasksApi.RefreshGroupMergeStatusAsync(externallyMergedGroupId, ct);
                    continue;
                }

                // Closed WITHOUT merging. This used to be folded into the branch above, which
                // recorded rejected work as shipped: the task moved to Done, counted toward its
                // milestone, and scored the forecast as a completed task. Stop watching and hand
                // it to a human instead — and keep the worktree, since deciding what to do next
                // usually means looking at it. The age-based sweep reclaims it either way.
                if (pr.State == "closed")
                {
                    await tasksApi.MarkPrClosedAsync(task.Id, ct);
                    _autoResolveAttempts.TryRemove(task.Id, out _);
                    logger.LogInformation(
                        "PR for task {TaskId} was closed without merging — no longer watching it",
                        task.Id);
                    continue;
                }

                // Held by the group gate: do everything except land it. Conflicts are read
                // straight off the dry-run mergeability rather than inferred from a failed
                // merge, so a broken member is still surfaced and still gets its one
                // automatic resolution attempt while its siblings finish.
                if (heldByGate)
                {
                    if (pr.Mergeable == false)
                        await HandleMergeConflictsAsync(scope, tasksApi, taskNotifier, orchestrator, task, repo, ct);
                    else
                        logger.LogDebug(
                            "Task {TaskId} is ready but held by its cross-repo group gate", task.Id);
                    continue;
                }

                // PR is open — land it (merge into a folder, publish to a forge)
                var mergeResult = await pullRequests.MergeAsync(repo.Id, prNumber.Value, ct);

                // Both land the task branch FROM where the platform's push after each run put
                // it: the user's folder, or the remote. A branch that is not there is a push
                // that never happened (a run that ended with the runner offline); the worktree
                // on the machine that ran it still has the commits, so push from there and try
                // once more, rather than block the task on a human retry for work that is done.
                if (mergeResult.IsFailure
                    && MergeBlockedReasons.IsMissingHeadBranch(mergeResult.Error)
                    && await TryPushTaskBranchAsync(tasksApi, task, repo, pr.HeadBranch, ct))
                {
                    mergeResult = await pullRequests.MergeAsync(repo.Id, prNumber.Value, ct);
                }

                if (StoryLandingErrors.IsStoryConflict(mergeResult.Error))
                    _storyConflicts[task.Id] = 0;
                else
                    _storyConflicts.TryRemove(task.Id, out _);

                if (mergeResult.IsSuccess)
                {
                    // Forge PR first: "merged with no forge PR" reads as a folder landing.
                    if (mergeResult.Value!.ForgeUrl is { } forgeUrl)
                        await tasksApi.SetTaskPublishedPrUrlAsync(task.Id, forgeUrl, ct);
                    await tasksApi.MarkTaskMergedAsync(task.Id, mergeResult.Value.MergeCommitSha, ct);
                    _autoResolveAttempts.TryRemove(task.Id, out _);
                    logger.LogInformation(mergeResult.Value.ForgeUrl is null
                        ? "Merged PR for task {TaskId}"
                        : "Published PR for task {TaskId}: {ForgeUrl}", task.Id, mergeResult.Value.ForgeUrl);
                    mergedThisCycle.Add(task.Id);
                    MarkPollProductive();
                    await AuditMergedAsync(task);
                    await CleanupWorktreeAsync(tasksApi, task, ct);
                    if (task.GroupId is { } mergedGroupId)
                        await tasksApi.RefreshGroupMergeStatusAsync(mergedGroupId, ct);
                    continue;
                }

                // An environment-blocked failure (dirty working repo, wrong branch checked
                // out, runner offline, a forge CLI that is not signed in) outranks conflict
                // handling: no resolver run can succeed until the user fixes it, so record
                // the reason (merge page shows a "fix this first" screen) and keep polling.
                if (MergeBlockedReasons.IsEnvironmentBlocked(mergeResult.Error))
                {
                    var newlyBlocked = await MergeFailureNotifications.NotifyMergeFailureAsync(
                        tasksApi, taskNotifier, task.Id, mergeResult.Error!, ct,
                        MergeFailureKind.EnvironmentBlocked);
                    if (newlyBlocked)
                        logger.LogWarning("PR merge blocked for task {TaskId}: {Error} — retrying every cycle",
                            task.Id, mergeResult.Error);
                    else
                        logger.LogDebug("PR merge still blocked for task {TaskId}: {Error}",
                            task.Id, mergeResult.Error);
                    continue;
                }

                // Merge failed — only attempt conflict resolution if the dry run confirms conflicts.
                // A pull request whose mergeability could not be read still says so in the
                // merge's own words; that is confirmation too, and without it an unknown reading
                // would retry a conflicted merge every cycle forever.
                // A Story's end conflicts in a repository other than this member's own too (its
                // PR reads mergeable): that is still a conflict for the resolver, which works
                // through every conflicting repository of the story.
                if (pr.Mergeable == false
                    || StoryLandingErrors.IsStoryConflict(mergeResult.Error)
                    || (pr.Mergeable is null
                        && mergeResult.Error?.Contains("Merge conflict between", StringComparison.OrdinalIgnoreCase) == true))
                {
                    await HandleMergeConflictsAsync(scope, tasksApi, taskNotifier, orchestrator, task, repo, ct);
                }
                else if (pr.Mergeable is null)
                {
                    // The machine could not answer — skip and retry next cycle
                    logger.LogDebug("PR mergeability unknown for task {TaskId} — will retry next cycle", task.Id);
                }
                else
                {
                    // Mergeable is true but landing still failed (a squash-merge conflict in the
                    // user's folder, or the forge CLI refusing to open the pull request).
                    // Persist the reason so the task surfaces it instead of log-only;
                    // warn only when the reason is new (this path retries every cycle).
                    var newReason = await MergeFailureNotifications.NotifyMergeFailureAsync(
                        tasksApi, taskNotifier, task.Id, mergeResult.Error ?? "Merge failed", ct,
                        MergeFailureKind.ForgeRejected);
                    if (newReason)
                        logger.LogWarning("PR merge failed for task {TaskId} (mergeable but blocked): {Error}",
                            task.Id, mergeResult.Error);
                    else
                        logger.LogDebug("PR merge still failing for task {TaskId} (mergeable but blocked): {Error}",
                            task.Id, mergeResult.Error);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error checking/merging PR for task {TaskId}", task.Id);
            }
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions ConflictJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Best-effort capture of what collided — conflicted files (dry-run merge) plus a
    /// plain-language summary from the fast utility model — for the task's merge page.
    /// Never throws; an empty snapshot still lets the page offer the resolution choices.
    /// </summary>
    private async Task SnapshotMergeConflictsAsync(
        IServiceScope scope, ITasksApi tasksApi, MergeCandidateTask task, RepoSummary repo,
        CancellationToken ct)
    {
        try
        {
            var snapshot = await tasksApi.GetTaskSnapshotAsync(task.Id, ct);
            if (snapshot?.BranchName is null) return;
            // Already captured, or a human choice is pending inside the snapshot.
            if (snapshot.MergeConflictsJson is not null || snapshot.MergeResolutionStrategy is not null)
                return;

            // The task's route, not this pod's manager: a runner task's repository — and every
            // local folder — lives on the user's machine, and asking the platform mirror for it
            // answered "Bare repo does not exist" and left the merge page without its file list.
            var git = executionRouter.Resolve(snapshot).Git;
            var report = await git.GetMergeConflictsAsync(
                task.OrgId, repo.Id, snapshot.BranchName, repo.DefaultBranch, ct);

            List<MergeConflictSnapshotFile> files = report.IsSuccess
                ? [.. report.Value!.Files.Select(f => new MergeConflictSnapshotFile(f.Path, f.Detail))]
                : [];

            var summary = files.Count > 0
                ? await TryGenerateConflictSummaryAsync(scope, snapshot, files, ct)
                : null;

            var json = System.Text.Json.JsonSerializer.Serialize(
                new MergeConflictSnapshot(files, summary, DateTimeOffset.UtcNow), ConflictJsonOptions);
            await tasksApi.SetTaskMergeConflictsAsync(task.Id, json, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not snapshot merge conflicts for task {TaskId}", task.Id);
        }
    }

    /// <summary>One or two friendly sentences for non-technical users; null on any failure.</summary>
    private async Task<string?> TryGenerateConflictSummaryAsync(
        IServiceScope scope, TaskSnapshot task, IReadOnlyList<MergeConflictSnapshotFile> files,
        CancellationToken ct)
    {
        try
        {
            var runnerUtility = scope.ServiceProvider
                .GetRequiredService<Orkka.SharedKernel.Ai.IUserRunnerUtility>();
            var options = scope.ServiceProvider
                .GetRequiredService<IOptions<Orkka.SharedKernel.Ai.AnthropicOptions>>().Value;

            var fileList = string.Join("\n", files.Take(30).Select(f => $"- {f.Path}: {f.Detail}"));
            var request = new Orkka.SharedKernel.Ai.AnthropicRequest
            {
                Model = options.FastUtilityModel,
                MaxTokens = 250,
                System =
                    "You explain git merge conflicts to people who do not know programming. " +
                    "Write 1-2 short sentences describing, in everyday product terms, what part of " +
                    "the app both changes touched (e.g. 'the settings screen', 'how the app starts up'). " +
                    "Never use the words git, branch, merge, conflict marker, or file paths. " +
                    "Do not suggest a fix. Respond with the sentences only.",
                Messages = [new Orkka.SharedKernel.Ai.AnthropicMessage("user",
                    $"Task: {task.Title}\nConflicting files:\n{fileList}")],
            };

            // On the owner's runner (their subscription, $0), like the run whose branch just
            // failed to merge. No platform-key fallback: a miss leaves the merge page with its
            // file list and no summary.
            var response = await runnerUtility.TryCompleteAsync(
                task.OrgId, task.RunnerUserId ?? task.CreatedByUserId, request, ct);
            if (response is null)
            {
                logger.LogInformation(
                    "No conflict summary for task {TaskId} — {Reason}", task.Id,
                    Orkka.SharedKernel.Ai.RunnerUtilityUnavailableException.MessageFor(
                        "summarize merge conflicts"));
                return null;
            }

            if (response.InputTokens > 0 || response.OutputTokens > 0)
            {
                var usageRecorder = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
                await usageRecorder.RecordUsageAsync(new RecordUsageCommand(
                    task.OrgId, task.ProjectId, task.Id, AgentId: null, ExecutionId: null,
                    options.FastUtilityModel, response.InputTokens, response.OutputTokens,
                    response.CacheReadTokens, response.CacheWriteTokens,
                    FlatCostBase: 0m,
                    UserId: task.CreatedByUserId), ct);
            }

            var text = response.Text?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Conflict summary generation failed — merge page falls back to the file list");
            return null;
        }
    }

    private Task AuditPrCreatedAsync(Ulid orgId, Ulid projectId, Ulid taskId, string prUrl) =>
        auditLog.RecordAsync(new AgentAuditRecord(
            orgId, projectId, AgentAuditEventType.PullRequestCreated,
            $"PR created for task {taskId}: {prUrl}",
            TaskId: taskId, Actor: "platform"));

    private Task AuditMergedAsync(MergeCandidateTask task) =>
        auditLog.RecordAsync(new AgentAuditRecord(
            task.OrgId, task.ProjectId, AgentAuditEventType.PullRequestMerged,
            $"PR merged to base for task {task.Id}: {task.PrUrl}",
            TaskId: task.Id, Actor: "platform"));

    /// <summary>
    /// Pushes a task's branch from its worktree to the repository's origin — the after-run
    /// push, redone from the merge sweep when landing found the branch missing. Tokenless (the
    /// developer's own git credentials), on the route that holds the worktree. True when the
    /// branch is there now; false when the worktree is gone, the runner is offline, or the
    /// push failed — the merge's own message then stands.
    /// </summary>
    private async Task<bool> TryPushTaskBranchAsync(
        ITasksApi tasksApi, MergeCandidateTask task, RepoSummary repo, string? branchName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(branchName))
            return false;
        try
        {
            var snapshot = await tasksApi.GetTaskSnapshotAsync(task.Id, ct);
            if (snapshot is null)
                return false;
            var git = executionRouter.Resolve(snapshot).Git;
            var push = await git.EnsurePushedAsync(task.OrgId, repo.Id, task.Id, branchName, ct);
            if (push.IsSuccess)
            {
                logger.LogInformation(
                    "Task {TaskId}: pushed {Branch} from its worktree — landing again",
                    task.Id, branchName);
                return true;
            }

            logger.LogWarning(
                "Task {TaskId}: {Branch} is missing from the repository and could not be pushed from its worktree: {Error}",
                task.Id, branchName, push.Error);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Task {TaskId}: re-push of {Branch} failed", task.Id, branchName);
            return false;
        }
    }

    private async Task CleanupWorktreeAsync(
        ITasksApi tasksApi, MergeCandidateTask task, CancellationToken ct)
    {
        if (task.RepoId is not { } repoId)
            return;

        try
        {
            // Route like the orchestrator does (see the reset sweep above), so the task's
            // worktree is removed on its owner's machine.
            var snapshot = await tasksApi.GetTaskSnapshotAsync(task.Id, ct);

            // Never pull the worktree out from under a running agent. A merge can land while a
            // reviewer is still reading the diff — a human approves, this sweep merges, and the
            // agent's Cwd is deleted mid-run. What comes back then is a truncated or empty
            // answer, which surfaces to the user as "the reviewer agent produced an unreadable
            // verdict" on a task that had in fact just shipped.
            //
            // Skipping costs nothing: the runner's own stale-worktree GC is the backstop for
            // exactly this, and it will reclaim the worktree once the run is over.
            if (snapshot?.HasAgentWorking == true)
            {
                logger.LogInformation(
                    "Task {TaskId} merged while an agent is still running — leaving the worktree "
                    + "for the GC sweep rather than deleting it underneath the run", task.Id);
                return;
            }

            if (snapshot is null)
                return;
            await executionRouter.Resolve(snapshot).Git.CleanupWorktreeAsync(task.OrgId, repoId, task.Id);
        }
        catch (Exception ex)
        {
            // Cleanup is best-effort — the runner's GC sweep is the backstop.
            logger.LogWarning(ex, "Failed to clean up worktree for merged task {TaskId}", task.Id);
        }
    }

    /// <summary>
    /// Removes worktrees of tasks that merged outside the poller's own merge path — manual
    /// merges from the task page or repo page set IsMerged in the API process, so the
    /// poller never revisits them and their worktrees used to linger until the 72h GC.
    /// Runs on every replica: the cleanup goes through the owner's runner link.
    /// </summary>
    private async Task SweepMergedTaskWorktreesAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextMergedSweep)
            return;

        _nextMergedSweep = now + _mergedSweepInterval;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();

            // Anything older than the retention window is the GC sweep's job.
            var merged = await tasksApi.ListRecentlyMergedTasksAsync(now - _worktreeRetention, ct);

            foreach (var task in merged)
            {
                if (!_sweptMergedWorktrees.Add(task.Id))
                    continue;
                await CleanupWorktreeAsync(tasksApi, task, ct);
            }

            // Keep the swept set bounded: only ids still inside the lookback window matter.
            _sweptMergedWorktrees.IntersectWith(merged.Select(t => t.Id));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Merged-worktree sweep failed");
        }
    }

    /// <summary>Per-cycle repo lookup cache so polling loops don't refetch the same repo per task.</summary>
    private static async Task<RepoSummary?> GetRepoCachedAsync(
        IProjectsApi projectsApi,
        Dictionary<Ulid, RepoSummary?> cache,
        Ulid repoId, CancellationToken ct)
    {
        if (!cache.TryGetValue(repoId, out var repo))
        {
            repo = await projectsApi.GetRepoAsync(repoId, ct);
            cache[repoId] = repo;
        }

        return repo;
    }

    private static int? ParsePrNumber(string prUrl)
    {
        var lastSlash = prUrl.LastIndexOf('/');
        if (lastSlash < 0) return null;
        var numberStr = prUrl[(lastSlash + 1)..];
        return int.TryParse(numberStr, out var number) ? number : null;
    }
}
