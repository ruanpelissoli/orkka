namespace Orkka.Tests.Worker;

using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Infrastructure;
using Orkka.Modules.Execution.Agents;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Runners.Server;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

public sealed class TaskQueueProcessorTests
{
    private static ServiceProvider BuildServiceProvider(TasksDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var scopeFactory = Mock.Of<IServiceScopeFactory>();
        var orchestrator = Mock.Of<ITaskOrchestrator>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:PollIntervalSeconds"] = "10"
            })
            .Build();

        var processor = new TaskQueueProcessor(
            scopeFactory, orchestrator, Mock.Of<IDocumentationSyncService>(),
            Mock.Of<IExecutionRouter>(), Mock.Of<ITaskNotifier>(),
            Mock.Of<IAgentAuditLog>(),
            Mock.Of<Orkka.Modules.Runners.Contracts.IRunnerClaimAffinity>(), new WorkerDrain(), config,
            Options.Create(new DocumentationSyncOptions()),
            Mock.Of<ILogger<TaskQueueProcessor>>());

        processor.Should().NotBeNull();
    }

    // ── Reset sweep: runner-side run must be stopped before the worktree goes ─────────
    //
    // The FAN-9 shape: a deploy rolled the replica that started the run; the replacement
    // has the same pod name (the lease reads as its own), an empty _runCancellations, and
    // an agent still reading the worktree on the user's runner.

    /// <summary>The housekeeping passes (PR backstop, merge sweep, auto-recovery, doc sync) run
    /// on every sweep — Orkka is one process, so there is no leader to skip them. Empty answers
    /// keep them no-ops in tests about something else.</summary>
    private static void StubHousekeeping(Mock<ITasksApi> api)
    {
        api.Setup(a => a.ListTasksAwaitingPrAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        api.Setup(a => a.ListTasksAwaitingMergeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        api.Setup(a => a.ListAutoRecoverableBlockedTasksAsync(
            It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        api.Setup(a => a.ListTasksAwaitingDocSyncAsync(
            It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private sealed class Sweep
    {
        public readonly Ulid TaskId = Ulid.NewUlid();
        public readonly Ulid OrgId = Ulid.NewUlid();
        public readonly Ulid ProjectId = Ulid.NewUlid();
        public readonly Ulid RepoId = Ulid.NewUlid();
        public readonly Mock<ITasksApi> TasksApi = new(MockBehavior.Loose);
        public readonly Mock<IAiAgentInvoker> Invoker = new();
        public readonly Mock<IGitWorkspaceManager> Git = new();
        public readonly Mock<ITaskNotifier> Notifier = new();
        public readonly WorkerDrain Drain = new();
        public readonly List<string> Calls = [];
        public readonly TaskQueueProcessor Processor;

        public Sweep(DateTimeOffset? resetRequestedAt = null)
        {
            const string runnerUserId = "user_runner";

            TasksApi.Setup(a => a.ListResetPendingTasksAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([new ResetPendingTask(
                    TaskId, OrgId, ProjectId, RepoId, "FAN-9", Lane.CodeReview, "orkka/FAN-9",
                    // Same pod name as this process: the lease reads as our own.
                    LeaseOwner: Environment.MachineName, LeaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(4),
                    RunnerUserId: runnerUserId,
                    ResetRequestedAt: resetRequestedAt ?? DateTimeOffset.UtcNow)]);
            TasksApi.Setup(a => a.GetTaskSnapshotAsync(TaskId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Snapshot(runnerUserId));
            TasksApi.Setup(a => a.FinalizeTaskResetAsync(TaskId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true)
                .Callback(() => Calls.Add("finalize"));

            Invoker.Setup(i => i.CancelTaskRunsAsync(TaskId, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask)
                .Callback(() => Calls.Add("cancel-runs"));
            Git.Setup(g => g.CleanupWorktreeAsync(OrgId, RepoId, TaskId))
                .Returns(Task.CompletedTask)
                .Callback(() => Calls.Add("cleanup-worktree"));

            var route = new ExecutionRoute(
                Invoker.Object, Git.Object, Mock.Of<IProviderReadiness>(),
                Repo: Mock.Of<Orkka.Modules.SourceControl.Contracts.IGitRepoReader>());
            var router = new Mock<IExecutionRouter>();
            router.Setup(r => r.Resolve(It.IsAny<TaskSnapshot>())).Returns(route);

            // The normal poll path resolves these too; empty answers keep it a no-op.
            StubHousekeeping(TasksApi);
            TasksApi.Setup(a => a.ListResumableTasksAsync(It.IsAny<IReadOnlyCollection<Lane>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            TasksApi.Setup(a => a.ListQueuedTasksAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            TasksApi.Setup(a => a.ListInteractiveSessionsAwaitingPickupAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            var services = new ServiceCollection();
            services.AddSingleton(TasksApi.Object);
            services.AddSingleton(Mock.Of<Orkka.Modules.SourceControl.Contracts.IPullRequestService>());
            services.AddSingleton(Mock.Of<ITaskNotifier>());
            services.AddSingleton(Mock.Of<IProjectsApi>());
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            Processor = new TaskQueueProcessor(
                scopeFactory, Mock.Of<ITaskOrchestrator>(), Mock.Of<IDocumentationSyncService>(),
                router.Object, Notifier.Object,
                Mock.Of<IAgentAuditLog>(),
                Mock.Of<Orkka.Modules.Runners.Contracts.IRunnerClaimAffinity>(),
                Drain,
                new ConfigurationBuilder().Build(),
                Options.Create(new DocumentationSyncOptions()),
                NullLogger<TaskQueueProcessor>.Instance);
        }

        private TaskSnapshot Snapshot(string? runnerUserId) => new(
            TaskId, OrgId, ProjectId, RepoId, 9, "FAN-9", null, Priority.Medium, TaskType.Feature,
            Lane.CodeReview, TaskStatus.Active, false, null, 0, false, "orkka/FAN-9", null, null, null,
            null, false, false, false, false, 0, 0, null, null, null, null, null, null,
            RunnerUserId: runnerUserId);
    }

    [Fact]
    public async Task ResetSweep_CancelsRunnerRunsBeforeRemovingTheWorktree()
    {
        var sweep = new Sweep();

        await sweep.Processor.ProcessResetRequestsAsync(CancellationToken.None);

        sweep.Calls.Should().Equal("cancel-runs", "cleanup-worktree", "finalize");
        sweep.Notifier.Verify(n => n.SendLaneChangedAsync(sweep.ProjectId, It.IsAny<LaneChangedEvent>()), Times.Once);
    }

    // ── Drain: a deploying pod keeps its heartbeat and reset sweep, claims nothing new ───

    [Fact]
    public async Task PollCycle_WhileDraining_SweepsResetsButClaimsNothing()
    {
        var sweep = new Sweep();
        sweep.TasksApi.Setup(a => a.ListResetPendingTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        sweep.Drain.Begin();

        await sweep.Processor.RunPollCycleAsync(CancellationToken.None);

        sweep.TasksApi.Verify(a => a.ListResetPendingTasksAsync(It.IsAny<CancellationToken>()), Times.Once,
            "a user's reset must still be honoured on a draining pod (it may hold that very run)");
        sweep.TasksApi.Verify(a => a.ListQueuedTasksAsync(It.IsAny<CancellationToken>()), Times.Never,
            "nothing new is claimed while draining");
        sweep.TasksApi.Verify(a => a.ListResumableTasksAsync(It.IsAny<IReadOnlyCollection<Lane>>(), It.IsAny<CancellationToken>()), Times.Never);
        sweep.TasksApi.Verify(a => a.ListInteractiveSessionsAwaitingPickupAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollCycle_NotDraining_ClaimsAsUsual()
    {
        var sweep = new Sweep();
        sweep.TasksApi.Setup(a => a.ListResetPendingTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await sweep.Processor.RunPollCycleAsync(CancellationToken.None);

        sweep.TasksApi.Verify(a => a.ListQueuedTasksAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Auto-recovery sweep: provider outages un-block themselves, bounded ─────────────
    //
    // A run that exhausts the in-run transient retry budget parks Blocked with the API
    // error; during an unstable stretch every such task then waits for a human whose only
    // move is pressing Retry. The sweep presses it — but only for errors the in-run retry
    // itself would have called transient.

    private sealed class Recovery
    {
        public readonly Ulid TaskId = Ulid.NewUlid();
        public readonly Ulid ProjectId = Ulid.NewUlid();
        public readonly Mock<ITasksApi> TasksApi = new(MockBehavior.Loose);
        public readonly Mock<ITaskNotifier> Notifier = new();
        public readonly TaskQueueProcessor Processor;

        public Recovery(string? lastError, bool enabled = true)
        {
            TasksApi.Setup(a => a.ListAutoRecoverableBlockedTasksAsync(
                    It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([new AutoRecoverableBlockedTask(
                    TaskId, Ulid.NewUlid(), ProjectId, "OUT-1",
                    AutoRecoveryCount: 0, LastError: lastError)]);
            TasksApi.Setup(a => a.TryAutoRecoverBlockedTaskAsync(
                    TaskId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var services = new ServiceCollection();
            services.AddSingleton(TasksApi.Object);
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Worker:AutoRecoverApiErrors"] = enabled ? "true" : "false",
                })
                .Build();

            Processor = new TaskQueueProcessor(
                scopeFactory, Mock.Of<ITaskOrchestrator>(), Mock.Of<IDocumentationSyncService>(),
                Mock.Of<IExecutionRouter>(), Notifier.Object,
                Mock.Of<IAgentAuditLog>(),
                Mock.Of<Orkka.Modules.Runners.Contracts.IRunnerClaimAffinity>(), new WorkerDrain(),
                config, Options.Create(new DocumentationSyncOptions()),
                NullLogger<TaskQueueProcessor>.Instance);
        }
    }

    [Fact]
    public async Task AutoRecover_TransientProviderError_RequeuesAndTellsTheTimeline()
    {
        var recovery = new Recovery("API Error: Connection lost mid-response. The response above may be incomplete.");

        await recovery.Processor.AutoRecoverApiErrorTasksAsync(CancellationToken.None);

        recovery.TasksApi.Verify(a => a.TryAutoRecoverBlockedTaskAsync(
            recovery.TaskId, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        recovery.TasksApi.Verify(a => a.AddTaskMessageAsync(
            recovery.TaskId, "system", It.Is<string>(s => s.Contains("outage")),
            MessageType.SystemEvent, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once,
            "silent re-routing of a failed run is not something to discover in the activity feed later");
        recovery.Notifier.Verify(n => n.SendStatusChangedAsync(
            recovery.ProjectId,
            It.Is<StatusChangedEvent>(e => e.TaskId == recovery.TaskId && e.NewStatus == "InQueue")),
            Times.Once);
    }

    /// <summary>A transient failure of the repository, not the provider — a mirror another task
    /// was refreshing at that instant (prod, 2026-09-16). The run's own retry would have called
    /// it transient; the sweep does too, instead of parking the task on a human Retry.</summary>
    [Fact]
    public async Task AutoRecover_TransientRepositoryFailure_RequeuesToo()
    {
        var recovery = new Recovery(
            "Could not open storefront-ui: it couldn't be opened on the machine running this work. "
            + "(The repository mirror could not be refreshed: Network: warning: unable to access 'config': "
            + "Permission denied fatal: unknown error occurred while reading the configuration files)");

        await recovery.Processor.AutoRecoverApiErrorTasksAsync(CancellationToken.None);

        recovery.TasksApi.Verify(a => a.TryAutoRecoverBlockedTaskAsync(
            recovery.TaskId, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        recovery.TasksApi.Verify(a => a.AddTaskMessageAsync(
            recovery.TaskId, "system", It.Is<string>(s => s.Contains("repository")),
            MessageType.SystemEvent, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void TransientWorkspaceFailure_IsReadFromTheEngineCode_OrTheText()
    {
        TaskQueueProcessor.IsTransientWorkspaceFailure("The repository mirror could not be refreshed: Network: something").Should().BeTrue();
        TaskQueueProcessor.IsTransientWorkspaceFailure("Timeout: Timed out waiting to fetch").Should().BeTrue();
        TaskQueueProcessor.IsTransientWorkspaceFailure("error: cannot lock ref 'refs/remotes/origin/main'").Should().BeTrue();
        TaskQueueProcessor.IsTransientWorkspaceFailure("NoCredential: Orkka has no credential for this repository.").Should().BeFalse();
        TaskQueueProcessor.IsTransientWorkspaceFailure("The agent asked a question").Should().BeFalse();
        TaskQueueProcessor.IsTransientWorkspaceFailure(null).Should().BeFalse();
    }

    [Fact]
    public async Task AutoRecover_GenuineFailure_StaysBlockedForAHuman()
    {
        // Not a provider blip — re-running it re-bills a run guaranteed to fail the same way.
        var recovery = new Recovery("The plan references a file that does not exist.");

        await recovery.Processor.AutoRecoverApiErrorTasksAsync(CancellationToken.None);

        recovery.TasksApi.Verify(a => a.TryAutoRecoverBlockedTaskAsync(
            It.IsAny<Ulid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        recovery.Notifier.Verify(n => n.SendStatusChangedAsync(
            It.IsAny<Ulid>(), It.IsAny<StatusChangedEvent>()), Times.Never);
    }

    [Fact]
    public async Task AutoRecover_RaceLostToAHumanRetry_AnnouncesNothing()
    {
        var recovery = new Recovery("Overloaded");
        recovery.TasksApi.Setup(a => a.TryAutoRecoverBlockedTaskAsync(
                recovery.TaskId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await recovery.Processor.AutoRecoverApiErrorTasksAsync(CancellationToken.None);

        // The human's Retry already re-queued it — a second timeline message and broadcast
        // would describe a recovery that never happened.
        recovery.TasksApi.Verify(a => a.AddTaskMessageAsync(
            It.IsAny<Ulid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageType>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        recovery.Notifier.Verify(n => n.SendStatusChangedAsync(
            It.IsAny<Ulid>(), It.IsAny<StatusChangedEvent>()), Times.Never);
    }

    [Fact]
    public async Task AutoRecover_RawProviderBillingWording_StaysBlocked()
    {
        // The provider's billing text on the developer's own subscription is THEIR limit, and
        // a re-run fails identically until they top up.
        var recovery = new Recovery("Credit balance too low · Add funds: https://platform.claude.com/settings/billing");

        await recovery.Processor.AutoRecoverApiErrorTasksAsync(CancellationToken.None);

        recovery.TasksApi.Verify(a => a.TryAutoRecoverBlockedTaskAsync(
            It.IsAny<Ulid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AutoRecover_SwitchedOff_DoesNotEvenLook()
    {
        var recovery = new Recovery("Overloaded", enabled: false);

        await recovery.Processor.AutoRecoverApiErrorTasksAsync(CancellationToken.None);

        recovery.TasksApi.Verify(a => a.ListAutoRecoverableBlockedTasksAsync(
            It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResetSweep_RunnerOffline_InsideTheGrace_LeavesTheTaskResetPendingAndTheWorktreeAlone()
    {
        // A runner that dropped thirty seconds ago is rebooting, not gone: the run's fate is
        // still unknown, so nothing proceeds.
        var sweep = new Sweep(resetRequestedAt: DateTimeOffset.UtcNow.AddSeconds(-30));
        sweep.Invoker.Setup(i => i.CancelTaskRunsAsync(sweep.TaskId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RunnerUnavailableException("Your runner is not connected."));

        var act = () => sweep.Processor.ProcessResetRequestsAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("an offline runner is a retry, not a crash of the sweep");
        sweep.Calls.Should().BeEmpty("neither the worktree nor the finalize may proceed while the run's fate is unknown");
        sweep.TasksApi.Verify(a => a.FinalizeTaskResetAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResetSweep_RunnerOffline_PastTheGrace_FinalizesWithoutTheRunnerSideCleanup()
    {
        // The user stopped their runner, then reset the task. That runner is never coming
        // back on its own — a reset that waits for it traps
        // them in reset-pending forever (ESF-1, 2026-09-04).
        var sweep = new Sweep(
            resetRequestedAt: DateTimeOffset.UtcNow - TaskQueueProcessor.ResetRunnerOfflineGrace - TimeSpan.FromSeconds(1));
        sweep.Invoker.Setup(i => i.CancelTaskRunsAsync(sweep.TaskId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RunnerUnavailableException("Your runner is not connected."));

        await sweep.Processor.ProcessResetRequestsAsync(CancellationToken.None);

        sweep.Calls.Should().Equal(["finalize"],
            "the runner-side cleanup is skipped (that worktree is the user's own disk) and the task goes back to Drafting");
    }

    // -- Poll cadence: a steady state stops talking, and stops asking -------------------
    //
    // Measured in prod over eleven hours: two worker StatefulSets logged
    // "Deferred 6 task(s) with unmet dependencies" 7,836 and 7,787 times -- one line every
    // five seconds, the same six task ids every single time, and 27.5% of one pod's entire
    // log volume. The deferred set is only unblocked by an event this process cannot see, so
    // re-deriving it on a five-second clock bought nothing and hid everything else the
    // component had to say.

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        public IEnumerable<string> InfoLinesContaining(string fragment) => Entries
            .Where(e => e.Level == LogLevel.Information && e.Message.Contains(fragment))
            .Select(e => e.Message);

        public IEnumerable<string> WarningLinesContaining(string fragment) => Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains(fragment))
            .Select(e => e.Message);
    }

    /// <summary>A processor wired for whole-poll-cycle tests, with the queue and the blocked
    /// set swappable between sweeps so a test can move the world the way prod does.</summary>
    private sealed class Cadence
    {
        public readonly Ulid OrgId = Ulid.NewUlid();
        public readonly Ulid ProjectId = Ulid.NewUlid();
        public readonly Mock<ITasksApi> TasksApi = new(MockBehavior.Loose);
        public readonly Mock<IProjectsApi> ProjectsApi = new(MockBehavior.Loose);
        public readonly Mock<ITaskOrchestrator> Orchestrator = new();
        public readonly RecordingLogger<TaskQueueProcessor> Logger = new();
        public readonly TaskQueueProcessor Processor;

        /// <summary>Completes whatever run the orchestrator was handed -- until then the task
        /// counts as in flight on this replica.</summary>
        public readonly TaskCompletionSource RunGate = new();

        public List<QueuedTaskInfo> Queued = [];
        public HashSet<Ulid> Blocked = [];

        /// <summary>Tasks whose milestone still waits on one that has not shipped.</summary>
        public HashSet<Ulid> MilestoneHeld = [];

        /// <summary>What the project already has running — the other half of the admission
        /// decision, and the half plan-ahead changes.</summary>
        public List<ActiveTaskInfo> Active = [];

        /// <summary>Whose runner every queued task runs on — connected to this replica, with
        /// an AI CLI signed in, so pickup is decided by slots alone.</summary>
        public const string Owner = "user_cadence";

        public Cadence(
            int? idlePollCeilingSeconds = null, bool holdRuns = false,
            ProjectSettingsDto? projectSettings = null)
        {
            StubHousekeeping(TasksApi);
            TasksApi.Setup(a => a.ListResetPendingTasksAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            TasksApi.Setup(a => a.ListResumableTasksAsync(
                    It.IsAny<IReadOnlyCollection<Lane>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            TasksApi.Setup(a => a.ListInteractiveSessionsAwaitingPickupAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            TasksApi.Setup(a => a.ListQueuedTasksAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Queued);
            TasksApi.Setup(a => a.GetTasksWithUnmetDependenciesAsync(
                    It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Blocked);
            TasksApi.Setup(a => a.ListActiveTasksAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Active);
            TasksApi.Setup(a => a.GetMilestoneHeldTaskIdsAsync(
                    It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Ulid> ids, CancellationToken _) =>
                    ids.Where(MilestoneHeld.Contains).ToHashSet());
            TasksApi.Setup(a => a.TryClaimTaskAsync(
                    It.IsAny<Ulid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            TasksApi.Setup(a => a.GetTaskSnapshotAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Ulid id, CancellationToken _) => new TaskSnapshot(
                    id, OrgId, ProjectId, null, 1, "Queued", null, Priority.Medium, TaskType.Feature,
                    Lane.Ready, TaskStatus.InQueue, false, null, 0, false, null, null, null, null,
                    null, false, false, false, false, 0, 0, null, null, null, null, null, null,
                    RunnerUserId: Owner));

            ProjectsApi.Setup(a => a.GetProjectSummaryAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProjectSummary(
                    ProjectId, OrgId, "Cadence", "cadence", "CAD",
                    projectSettings));
            ProjectsApi.Setup(a => a.GetRepoStacksAsync(
                    It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<Ulid, string?>());

            Orchestrator.Setup(o => o.ExecuteAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>()))
                .Returns(holdRuns ? RunGate.Task : Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddSingleton(TasksApi.Object);
            services.AddSingleton(Mock.Of<Orkka.Modules.SourceControl.Contracts.IPullRequestService>());
            services.AddSingleton(Mock.Of<ITaskNotifier>());
            services.AddSingleton(ProjectsApi.Object);
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            var readiness = new Mock<IProviderReadiness>();
            readiness.SetupGet(r => r.Connected).Returns([AiProvider.Anthropic]);
            var router = new Mock<IExecutionRouter>();
            router.Setup(r => r.Resolve(It.IsAny<TaskSnapshot>())).Returns(new ExecutionRoute(
                Mock.Of<IAiAgentInvoker>(), Mock.Of<IGitWorkspaceManager>(), readiness.Object,
                Mock.Of<Orkka.Modules.SourceControl.Contracts.IGitRepoReader>()));

            var settings = new Dictionary<string, string?>
            {
                ["Worker:PollIntervalSeconds"] = "5",
            };
            if (idlePollCeilingSeconds is { } ceiling)
                settings["Worker:IdleSweepCeilingSeconds"] = ceiling.ToString();

            Processor = new TaskQueueProcessor(
                scopeFactory, Orchestrator.Object, Mock.Of<IDocumentationSyncService>(),
                router.Object, Mock.Of<ITaskNotifier>(),
                Mock.Of<IAgentAuditLog>(),
                new AlwaysHereRunnerClaimAffinity(), new WorkerDrain(),
                new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
                Options.Create(new DocumentationSyncOptions()),
                Logger);
        }

        /// <summary>Queues N tasks that nothing blocks -- the "work appears" case.</summary>
        public void QueueRunnableTasks(int count) => Queued =
            [.. Enumerable.Range(0, count).Select(_ => new QueuedTaskInfo(
                Ulid.NewUlid(), OrgId, ProjectId, RepoId: null, RunnerUserId: Owner))];

        /// <summary>Queues N tasks and declares every one of them dependency-blocked.</summary>
        public void QueueBlockedTasks(int count)
        {
            QueueRunnableTasks(count);
            Blocked = [.. Queued.Select(t => t.Id)];
        }

        public async Task SweepAsync(int times = 1)
        {
            for (var i = 0; i < times; i++)
                await Processor.RunPollCycleAsync(CancellationToken.None);
        }

        /// <summary>How many times the dependency re-derivation — the query the backoff exists
        /// to stop repeating — actually reached the database.</summary>
        public int DependencyQueries => TasksApi.Invocations.Count(i =>
            i.Method.Name == nameof(ITasksApi.GetTasksWithUnmetDependenciesAsync));
    }

    [Fact]
    public async Task DeferredDependencies_SameBlockedSetEverySweep_IsAnnouncedOnce()
    {
        var cadence = new Cadence();
        cadence.QueueBlockedTasks(6);

        await cadence.SweepAsync(times: 12);

        cadence.Logger.InfoLinesContaining("unmet dependencies").Should().HaveCount(1,
            "the same six tasks staying blocked is the same fact, not twelve facts -- "
            + "logging it per sweep is what made this message 27.5% of a worker's log volume");
        cadence.TasksApi.Verify(a => a.ListQueuedTasksAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(12),
            "the queue is still read every cycle -- only the re-derivation and its narration stop");
    }

    [Fact]
    public async Task DeferredDependencies_AnnouncedLineNamesTheTasks()
    {
        var cadence = new Cadence();
        cadence.QueueBlockedTasks(2);

        await cadence.SweepAsync();

        var line = cadence.Logger.InfoLinesContaining("unmet dependencies").Single();
        foreach (var blocked in cadence.Blocked)
            line.Should().Contain(blocked.ToString(),
                "a count alone cannot say WHICH task is stuck, which is what diagnosing this needed");
    }

    [Fact]
    public async Task DeferredDependencies_SetChanges_IsAnnouncedAgain()
    {
        var cadence = new Cadence();
        cadence.QueueBlockedTasks(3);
        await cadence.SweepAsync(times: 4);

        // One of the three had its dependency met. That does not move the QUEUED set, so the
        // change surfaces on the ladder's own schedule -- within the 60s ceiling, 12 cycles.
        cadence.Blocked = [.. cadence.Blocked.Take(2)];
        await cadence.SweepAsync(times: 24);

        cadence.Logger.InfoLinesContaining("unmet dependencies").Should().HaveCount(2,
            "a change to the blocked set IS news, and must not be swallowed by the deduplication");
    }

    [Fact]
    public async Task DeferredDependencies_LastOneUnblocks_SaysSoOnceAndReArms()
    {
        var cadence = new Cadence();
        cadence.QueueBlockedTasks(2);
        await cadence.SweepAsync(times: 24);

        // Both dependencies met: the tasks are claimed, so the queue empties.
        cadence.Blocked = [];
        cadence.Queued = [];
        await cadence.SweepAsync(times: 24);

        cadence.Logger.InfoLinesContaining("waiting on an unmet dependency").Should().HaveCount(1);

        // Re-armed: a fresh block after the all-clear is news again.
        cadence.QueueBlockedTasks(1);
        await cadence.SweepAsync(times: 24);

        cadence.Logger.InfoLinesContaining("unmet dependencies").Should().HaveCount(2);
    }

    [Fact]
    public async Task SweepCadence_IdleSweeps_StepUpTheLadderAndStopAtTheCeiling()
    {
        var cadence = new Cadence();
        cadence.Processor.CurrentSweepInterval.Should().Be(TimeSpan.FromSeconds(5));

        await cadence.SweepAsync();
        cadence.Processor.CurrentSweepInterval.Should().Be(TimeSpan.FromSeconds(30));

        // 30s at a 5s poll = one sweep every sixth cycle, so it takes six more to step again.
        await cadence.SweepAsync(times: 6);
        cadence.Processor.CurrentSweepInterval.Should().Be(TimeSpan.FromSeconds(60));

        await cadence.SweepAsync(times: 40);
        cadence.Processor.CurrentSweepInterval.Should().Be(TimeSpan.FromSeconds(60),
            "the ladder has a ceiling -- an idle worker must still re-check within a minute");
    }

    [Fact]
    public async Task SweepCadence_QueueBlockedAllNight_StopsReDerivingTheSameAnswer()
    {
        var cadence = new Cadence();
        cadence.QueueBlockedTasks(6);

        // 24 cycles = two minutes of prod's 5s poll, against the exact prod state: six tasks
        // blocked, nothing else moving.
        await cadence.SweepAsync(times: 24);

        cadence.Processor.CurrentSweepInterval.Should().Be(TimeSpan.FromSeconds(60));
        cadence.DependencyQueries.Should().BeLessThan(6,
            "unchanged queue + unchanged blocked set = nothing to re-derive; flat-out this "
            + "would have been 24 queries, which over 11 hours was ~15,600 across the fleet");
        cadence.TasksApi.Verify(a => a.ListQueuedTasksAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(24),
            "the cheap query stays on every cycle -- it is the change detector");
    }

    [Fact]
    public async Task SweepCadence_NewlyQueuedTask_JumpsTheBackoffInsteadOfWaitingItOut()
    {
        var cadence = new Cadence();
        cadence.QueueBlockedTasks(6);
        await cadence.SweepAsync(times: 24);
        cadence.Processor.CurrentSweepInterval.Should().Be(TimeSpan.FromSeconds(60));

        // Someone submits a task into a system that has been quiet all night.
        var fresh = new QueuedTaskInfo(
            Ulid.NewUlid(), cadence.OrgId, cadence.ProjectId, RepoId: null, RunnerUserId: Cadence.Owner);
        cadence.Queued = [.. cadence.Queued, fresh];

        await cadence.SweepAsync();

        cadence.TasksApi.Verify(a => a.TryClaimTaskAsync(
            fresh.Id, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the queued id set moved, so the sweep runs on this very cycle -- a human must not "
            + "wait out a 60s ladder that exists only for unchanged state");
        cadence.Processor.CurrentSweepInterval.Should().Be(TimeSpan.FromSeconds(5),
            "and the cadence is back at base for whatever follows");
    }

    [Fact]
    public async Task SweepCadence_WorkAppears_SnapsBackToTheBaseInterval()
    {
        var cadence = new Cadence();
        await cadence.SweepAsync(times: 24);
        cadence.Processor.CurrentSweepInterval.Should().Be(TimeSpan.FromSeconds(60));

        cadence.QueueRunnableTasks(1);
        await cadence.SweepAsync();

        cadence.Processor.CurrentSweepInterval.Should().Be(TimeSpan.FromSeconds(5),
            "a newly queued task must not inherit the idle cadence");
        // Claimed inline by the sweep; the orchestration itself is fire-and-forget, so the
        // claim is the synchronous evidence that the task was actually picked up.
        cadence.TasksApi.Verify(a => a.TryClaimTaskAsync(
            cadence.Queued[0].Id, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SweepCadence_RunStillInFlight_HoldsTheBaseIntervalEvenWithNothingQueued()
    {
        var cadence = new Cadence(holdRuns: true);
        cadence.QueueRunnableTasks(1);
        await cadence.SweepAsync();

        // The queue is empty now -- but the run this replica started has not finished, and its
        // completion is the very event a blocked dependency is waiting for.
        cadence.Queued = [];
        await cadence.SweepAsync(times: 5);

        cadence.Processor.CurrentSweepInterval.Should().Be(TimeSpan.FromSeconds(5));

        cadence.RunGate.SetResult();
    }

    [Fact]
    public async Task SweepCadence_HumanFacingSweeps_NeverBackOff()
    {
        var cadence = new Cadence();
        await cadence.SweepAsync(times: 24);
        cadence.Processor.CurrentSweepInterval.Should().Be(TimeSpan.FromSeconds(60));

        // A reset click and an opened chat session are humans waiting on a screen, and both
        // reach the worker only through this loop. They stay on the base interval.
        cadence.TasksApi.Verify(a => a.ListResetPendingTasksAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(24));
        cadence.TasksApi.Verify(
            a => a.ListInteractiveSessionsAwaitingPickupAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(24));
    }

    [Fact]
    public async Task SweepCadence_CeilingAtOrBelowTheBase_KeepsTheFlatCadence()
    {
        var cadence = new Cadence(idlePollCeilingSeconds: 5);
        cadence.QueueBlockedTasks(2);

        await cadence.SweepAsync(times: 10);

        cadence.Processor.CurrentSweepInterval.Should().Be(TimeSpan.FromSeconds(5));
        cadence.DependencyQueries.Should().Be(10,
            "the escape hatch: an operator who wants the old flat sweep back sets the ceiling to the base");
    }

    // ── Plan-ahead: the next task's plan is written while this one is still building ──
    //
    // The shape being fixed: a single-repo project with MaxParallelTasks=1 runs one task
    // end-to-end, and the NEXT task's planning run — minutes of codebase exploration that
    // writes nothing — could not start until the current one reached Done. Every handover
    // paid for a whole planning run in dead time with no task running.

    /// <summary>One execution slot, one task planned ahead of it. The depth is PINNED rather
    /// than derived, so these cases keep exercising the mechanism at a known depth however the
    /// default moves.</summary>
    private static readonly ProjectSettingsDto OneAtATime = new(
        MaxParallelTasks: 1, SameRepoParallelism: false, PlanAheadDepth: 1);

    /// <summary>A project whose only execution slot is already spent on a task writing code.</summary>
    private static Cadence Building(ProjectSettingsDto settings, Ulid repoId)
    {
        var cadence = new Cadence(projectSettings: settings);
        cadence.Active = [new ActiveTaskInfo(Ulid.NewUlid(), repoId, Lane.Developing)];
        return cadence;
    }

    /// <summary>An autopilot task already running its read-only prep phases.</summary>
    private static ActiveTaskInfo Planning(Ulid repoId)
        => new(Ulid.NewUlid(), repoId, Lane.Planning, SkipPlanning: false, IsAutopilot: true);

    private static QueuedTaskInfo Waiting(
        Cadence cadence, Ulid repoId, Lane lane = Lane.Ready, bool autopilot = true,
        bool skipPlanning = false)
        => new(Ulid.NewUlid(), cadence.OrgId, cadence.ProjectId, repoId, RunnerUserId: Cadence.Owner,
            CurrentLane: lane, SkipPlanning: skipPlanning, IsAutopilot: autopilot);

    private static void VerifyClaimed(Cadence cadence, QueuedTaskInfo task, Times times, string because)
        => cadence.TasksApi.Verify(a => a.TryClaimTaskAsync(
            task.Id, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            times, because);

    [Fact]
    public async Task PlanAhead_TaskBuildingOnTheOnlySlot_StillLetsTheNextOnePlan()
    {
        var repoId = Ulid.NewUlid();
        var cadence = Building(OneAtATime, repoId);
        var next = Waiting(cadence, repoId);
        cadence.Queued = [next];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, next, Times.Once(),
            "planning writes nothing but its own worktree, so it has no business waiting on "
            + "the execution slot -- this is the dead time between tasks the change removes");
    }

    [Fact]
    public async Task PlanAhead_DepthOne_PlansExactlyOneTaskAhead()
    {
        var repoId = Ulid.NewUlid();
        var cadence = Building(OneAtATime, repoId);
        var first = Waiting(cadence, repoId);
        var second = Waiting(cadence, repoId);
        cadence.Queued = [first, second];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, first, Times.Once(), "the first one runs ahead");
        VerifyClaimed(cadence, second, Times.Never(),
            "depth 1 means ONE plan ahead -- planning the whole backlog against a base that "
            + "has not settled buys no more speed and bills for every one of them");
    }

    [Fact]
    public async Task PlanAhead_PrepSlotAlreadyTaken_WaitsForIt()
    {
        var repoId = Ulid.NewUlid();
        var cadence = Building(OneAtATime, repoId);
        cadence.Active = [.. cadence.Active, Planning(repoId)];
        var next = Waiting(cadence, repoId);
        cadence.Queued = [next];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, next, Times.Never(),
            "one building plus one planning ahead is the whole in-flight budget at depth 1");
    }

    [Fact]
    public async Task PlanAhead_PlannedTaskComesBackForDevelopment_TakesTheBuildSlot()
    {
        var repoId = Ulid.NewUlid();
        var cadence = new Cadence(projectSettings: OneAtATime);
        // The pipeline's whole point: planning finished, the plan was auto-approved, and the
        // task re-queued at Developing while nothing else is building.
        var ready = Waiting(cadence, repoId, Lane.Developing);
        cadence.Queued = [ready];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, ready, Times.Once(),
            "it goes straight to Developing with a plan already written -- no planning run "
            + "stands between the slot freeing and code being written");
    }

    [Fact]
    public async Task PlanAhead_IsNotASecondBuildSlot()
    {
        var repoId = Ulid.NewUlid();
        var cadence = Building(OneAtATime, repoId);
        var alsoBuilding = Waiting(cadence, repoId, Lane.Developing);
        cadence.Queued = [alsoBuilding];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, alsoBuilding, Times.Never(),
            "MaxParallelTasks still bounds work that touches the branch -- prep-ahead widens "
            + "the read-only head of the pipeline, never the build budget");
    }

    [Fact]
    public async Task PlanAhead_SkipPlanningTask_IsBuildWorkFromTheClaim()
    {
        var repoId = Ulid.NewUlid();
        var cadence = Building(OneAtATime, repoId);
        var direct = Waiting(cadence, repoId, Lane.Ready, skipPlanning: true);
        cadence.Queued = [direct];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, direct, Times.Never(),
            "SkipPlanning drops straight into Developing out of the Ready lane, so its claim "
            + "starts code-writing work -- running it 'ahead' would run two builds at once");
    }

    [Fact]
    public async Task PlanAhead_HandSubmittedTask_StillWaitsForASlot()
    {
        var repoId = Ulid.NewUlid();
        var cadence = Building(OneAtATime, repoId);
        var manual = Waiting(cadence, repoId, autopilot: false);
        cadence.Queued = [manual];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, manual, Times.Never(),
            "its plan parks in PlanReady for a human either way, so planning it early buys no "
            + "speed and bills for a plan that may never be approved");
    }

    [Fact]
    public async Task PlanAhead_ParallelProject_IsNotThrottledToTheDepth()
    {
        var repoId = Ulid.NewUlid();
        // Derived depth for three slots is four, so a three-parallel project starts at least as
        // many plans as it ever did -- the reason the depth is sized above MaxParallelTasks
        // rather than letting prep borrow execution slots it would then release.
        var cadence = new Cadence(projectSettings: new ProjectSettingsDto(
            MaxParallelTasks: 3, SameRepoParallelism: true));
        var queued = Enumerable.Range(0, 4).Select(_ => Waiting(cadence, repoId)).ToList();
        cadence.Queued = [.. queued];

        await cadence.SweepAsync();

        foreach (var task in queued)
            VerifyClaimed(cadence, task, Times.Once(),
                "all four plan at once, and every execution slot stays free for the builds "
                + "those plans are about to produce");
    }

    [Fact]
    public async Task PlanAhead_HandSubmittedTaskPlanning_StillHoldsItsExecutionSlot()
    {
        var repoId = Ulid.NewUlid();
        var cadence = new Cadence(projectSettings: OneAtATime);
        // A hand-submitted task, claimed against MaxParallelTasks, now in its planning run.
        cadence.Active = [new ActiveTaskInfo(Ulid.NewUlid(), repoId, Lane.Planning)];
        var next = Waiting(cadence, repoId, autopilot: false);
        cadence.Queued = [next];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, next, Times.Never(),
            "it took a slot, so it keeps one -- reading its lane as free-of-charge prep would "
            + "hand the slot to the next task, and the next, with nothing bounding the pile");
    }

    [Fact]
    public async Task PlanAhead_EverythingInFlightIsBounded()
    {
        var repoId = Ulid.NewUlid();
        var cadence = new Cadence(projectSettings: OneAtATime);
        // Both in-flight tasks are planning, so neither is counted against the build budget --
        // the case where an unbounded design would happily start a third, and a fourth.
        cadence.Active = [Planning(repoId), Planning(repoId)];
        var next = Waiting(cadence, repoId);
        cadence.Queued = [next];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, next, Times.Never(),
            "MaxParallelTasks + PlanAheadDepth is the ceiling on everything running at once");
    }

    // ── Planning ahead of a milestone gate (prod, 2026-09-17) ──

    [Fact]
    public async Task MilestoneGate_APlanningRunAheadOfTheGate_UsesThePlanAheadSlot()
    {
        var repoId = Ulid.NewUlid();
        var cadence = Building(OneAtATime, repoId);
        var nextMilestone = Waiting(cadence, repoId, Lane.Ready);
        cadence.Queued = [nextMilestone];
        cadence.MilestoneHeld = [nextMilestone.Id];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, nextMilestone, Times.Once(),
            "the previous milestone's last task is building; the next one's plan is written meanwhile");
    }

    [Fact]
    public async Task MilestoneGate_APlanHeldAtTheGate_IsABankedPlan()
    {
        var repoId = Ulid.NewUlid();
        var cadence = new Cadence(projectSettings: OneAtATime);
        // Planned ahead, approved, and held at Developing until the milestone ships.
        var held = Waiting(cadence, repoId, Lane.Developing);
        var another = Waiting(cadence, repoId, Lane.Ready);
        cadence.Queued = [held, another];
        cadence.MilestoneHeld = [held.Id, another.Id];
        cadence.Blocked = [held.Id];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, held, Times.Never(), "its build waits for the milestone to ship");
        VerifyClaimed(cadence, another, Times.Never(),
            "the held plan holds the depth -- otherwise the whole next milestone would be planned "
            + "against a base that does not have the previous one yet");
    }

    [Fact]
    public async Task MilestoneGate_WithNoPlanAheadDepth_NothingPlansAheadOfTheGate()
    {
        var repoId = Ulid.NewUlid();
        var cadence = new Cadence(projectSettings: new ProjectSettingsDto(
            MaxParallelTasks: 1, SameRepoParallelism: false, PlanAheadDepth: 0));
        var nextMilestone = Waiting(cadence, repoId, Lane.Ready);
        cadence.Queued = [nextMilestone];
        cadence.MilestoneHeld = [nextMilestone.Id];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, nextMilestone, Times.Never(),
            "planning ahead of a gate is plan-ahead or nothing -- it never takes an execution slot");
    }

    [Fact]
    public async Task PlanAhead_PlannedTaskWaitingForASlot_StopsTheBacklogPlanningItself()
    {
        var repoId = Ulid.NewUlid();
        var cadence = Building(OneAtATime, repoId);
        // The one banked plan: it finished planning, auto-approved, and re-queued at Developing
        // where it now waits for the slot the building task holds.
        var banked = Waiting(cadence, repoId, Lane.Developing);
        var after = Waiting(cadence, repoId);
        cadence.Queued = [banked, after];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, after, Times.Never(),
            "a planning run ends by re-queuing its task, which frees the prep slot -- so "
            + "without counting banked plans one long Developing run would get the WHOLE "
            + "backlog planned, every plan against a base nobody has built yet");
    }

    [Fact]
    public async Task PlanAhead_SlotFrees_HandsOffAndStartsTheNextPlanInOneSweep()
    {
        var repoId = Ulid.NewUlid();
        // The moment the change exists for: nothing is building any more, one task is planned
        // and waiting, one more is queued behind it.
        var cadence = new Cadence(projectSettings: OneAtATime);
        var banked = Waiting(cadence, repoId, Lane.Developing);
        var after = Waiting(cadence, repoId);
        cadence.Queued = [banked, after];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, banked, Times.Once(),
            "it goes straight to Developing on the freed slot -- no planning run stands "
            + "between the handover and code being written");
        VerifyClaimed(cadence, after, Times.Once(),
            "and the prep slot it just released starts the next plan in the SAME sweep, so "
            + "the pipeline never spends a poll with nothing queued up behind it");
    }

    [Fact]
    public async Task PlanAhead_DefaultDepth_KeepsAPlanInReserveBehindTheBankedOne()
    {
        var repoId = Ulid.NewUlid();
        // No PlanAheadDepth set: the project takes the derived default for one execution slot.
        var cadence = Building(new ProjectSettingsDto(
            MaxParallelTasks: 1, SameRepoParallelism: false), repoId);
        var banked = Waiting(cadence, repoId, Lane.Developing);
        var reserve = Waiting(cadence, repoId);
        cadence.Queued = [banked, reserve];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, reserve, Times.Once(),
            "planning only as fast as the project builds is gap-free ONLY while planning is "
            + "quicker than development -- one short dev run and the slot goes idle waiting. "
            + "The default keeps a plan in progress behind the banked one to absorb that");
    }

    [Fact]
    public async Task PlanAhead_DefaultDepth_ScalesWithParallelism()
    {
        ProjectSettingsDto.PlanAheadDepthFor(1).Should().Be(2);
        ProjectSettingsDto.PlanAheadDepthFor(3).Should().Be(4,
            "a project building three at a time needs three plans' worth of throughput to "
            + "keep them fed, plus the same one-run reserve");

        new ProjectSettingsDto(MaxParallelTasks: 3, SameRepoParallelism: false)
            .EffectivePlanAheadDepth.Should().Be(4, "unset means derived");
        new ProjectSettingsDto(MaxParallelTasks: 3, SameRepoParallelism: false, PlanAheadDepth: 0)
            .EffectivePlanAheadDepth.Should().Be(0, "an explicit 0 is an off switch, not 'unset'");
    }

    // ── Review overlap: the reviewer reads a diff, so it must not hold the build lane ──

    [Fact]
    public async Task ReviewOverlap_TaskInReview_DoesNotBlockTheNextOneFromBuilding()
    {
        var repoId = Ulid.NewUlid();
        var cadence = new Cadence(projectSettings: OneAtATime);
        cadence.Active = [new ActiveTaskInfo(Ulid.NewUlid(), repoId, Lane.CodeReview)];
        var next = Waiting(cadence, repoId, Lane.Developing);
        cadence.Queued = [next];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, next, Times.Once(),
            "a review reads a branch that is already written -- holding the execution slot for "
            + "it left the dev lane empty for the length of every review, which no amount of "
            + "planning ahead can fix");
    }

    [Fact]
    public async Task ReviewOverlap_BuildLaneBusy_DoesNotMakeTheReviewWaitEither()
    {
        var repoId = Ulid.NewUlid();
        var cadence = Building(OneAtATime, repoId);
        var toReview = Waiting(cadence, repoId, Lane.CodeReview);
        cadence.Queued = [toReview];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, toReview, Times.Once(),
            "and it works in both directions -- a finished task must not sit unreviewed (and "
            + "unmerged) behind whatever is building now");
    }

    [Fact]
    public async Task ReviewOverlap_IsBoundedByMaxParallel()
    {
        var repoId = Ulid.NewUlid();
        var cadence = new Cadence(projectSettings: OneAtATime);
        cadence.Active = [new ActiveTaskInfo(Ulid.NewUlid(), repoId, Lane.CodeReview)];
        var second = Waiting(cadence, repoId, Lane.CodeReview);
        cadence.Queued = [second];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, second, Times.Never(),
            "reviews arrive at the rate builds finish, so they get their own budget of the same "
            + "size -- not an unbounded lane that could crowd out the building it trails");
    }

    [Fact]
    public async Task ReviewOverlap_HandSubmittedPlanningRun_StillReservesItsRepo()
    {
        var repoId = Ulid.NewUlid();
        var cadence = new Cadence(projectSettings: OneAtATime with { MaxParallelTasks = 2 });
        // Not autopilot, so this planning run was admitted against MaxParallelTasks and holds a
        // slot -- and with it, its repo.
        cadence.Active = [new ActiveTaskInfo(Ulid.NewUlid(), repoId, Lane.Planning)];
        var sameRepo = Waiting(cadence, repoId, Lane.Developing);
        cadence.Queued = [sameRepo];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, sameRepo, Times.Never(),
            "the same-repo exclusion is waived for runs that do not hold a slot, and this one "
            + "does -- reading the lane alone would have quietly waived it for every "
            + "hand-submitted project too");
    }

    [Fact]
    public async Task PlanAhead_DepthZero_RestoresTheStrictlySerialPipeline()
    {
        var repoId = Ulid.NewUlid();
        var cadence = Building(OneAtATime with { PlanAheadDepth = 0 }, repoId);
        var next = Waiting(cadence, repoId);
        cadence.Queued = [next];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, next, Times.Never(), "the off switch actually switches it off");
    }

    [Fact]
    public async Task PlanAhead_PlanningTask_DoesNotReserveItsRepo()
    {
        var repoId = Ulid.NewUlid();
        var cadence = new Cadence(projectSettings: OneAtATime);
        // Nothing is building; one task is planning ahead on this repo.
        cadence.Active = [Planning(repoId)];
        var building = Waiting(cadence, repoId, Lane.Developing);
        cadence.Queued = [building];

        await cadence.SweepAsync();

        VerifyClaimed(cadence, building, Times.Once(),
            "the same-repo exclusion keeps two agents off one branch; a prep run forks its own "
            + "worktree and touches neither, so holding the repo would deadlock the pipeline "
            + "plan-ahead exists to keep moving");
    }
}
