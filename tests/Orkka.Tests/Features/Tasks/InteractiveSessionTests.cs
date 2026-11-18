namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Tasks.Infrastructure.Services;
using Orkka.Modules.Tasks.PublicApi;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Interactive development sessions (see .claude/docs/INTERACTIVE.md). An interactive session is
/// a real task driven by a human, so the invariant that matters most is that the autonomous queue
/// sweep can never claim one — a worker picking up a task somebody is typing into would run a
/// second agent in the same worktree.
/// </summary>
public sealed class InteractiveSessionTests : IDisposable
{
    private readonly TasksDbContext _db;
    private readonly TasksApi _api;

    public InteractiveSessionTests()
    {
        var options = new DbContextOptionsBuilder<TasksDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new TasksDbContext(options, currentUser: null, eventBus: null);
        _api = new TasksApi(
            _db,
            Mock.Of<ITaskNumberService>(),
            Mock.Of<IProjectsApi>(),
            Mock.Of<ITaskNotifier>(),
            Mock.Of<Orkka.SharedKernel.Events.IEventBus>(),
            new TaskSubmissionService(
                _db,
                Mock.Of<ITaskNotifier>(),
                new MilestoneProgressUpdater(
                    _db,
                    Mock.Of<ITaskNotifier>(),
                    Mock.Of<Orkka.SharedKernel.Events.IEventBus>(),
                    NullLogger<MilestoneProgressUpdater>.Instance),
                Mock.Of<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                NullLogger<TaskSubmissionService>.Instance),
            NullLogger<TasksApi>.Instance);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>One project for every seeded task, so the project-scoped queries under test
    /// (concurrency-slot accounting) actually see them together.</summary>
    private readonly Ulid _projectId = Ulid.NewUlid();

    private async Task<TaskEntity> SeedTaskAsync(
        TaskStatus status, bool isInteractive = false, int number = 1, Ulid? repoId = null)
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = Ulid.NewUlid(),
            ProjectId = _projectId,
            RepoId = repoId,
            Number = number,
            Title = $"Task {number}",
            Status = status,
            CurrentLane = isInteractive ? Lane.Developing : Lane.Ready,
            IsInteractive = isInteractive,
            InteractiveSessionStartedAt = isInteractive ? DateTimeOffset.UtcNow : null,
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        return task;
    }

    [Fact]
    public async Task ListQueuedTasks_ReturnsOrdinaryQueuedTask()
    {
        var task = await SeedTaskAsync(TaskStatus.InQueue);

        var queued = await _api.ListQueuedTasksAsync();

        queued.Select(q => q.Id).Should().ContainSingle().Which.Should().Be(task.Id);
    }

    [Fact]
    public async Task ListQueuedTasks_ExcludesInteractiveTask_EvenWhenQueued()
    {
        // InQueue is not a state an interactive task should ever reach — the exclusion is the
        // guarantee that a stray transition cannot hand a live session to the sweep.
        await SeedTaskAsync(TaskStatus.InQueue, isInteractive: true);

        var queued = await _api.ListQueuedTasksAsync();

        queued.Should().BeEmpty();
    }

    [Fact]
    public async Task ListQueuedTasks_ExcludesInteractiveTask_AlongsideOrdinaryOnes()
    {
        var ordinary = await SeedTaskAsync(TaskStatus.InQueue, number: 1);
        await SeedTaskAsync(TaskStatus.InQueue, isInteractive: true, number: 2);

        var queued = await _api.ListQueuedTasksAsync();

        queued.Select(q => q.Id).Should().ContainSingle().Which.Should().Be(ordinary.Id);
    }

    private async Task<TaskEntity> SeedSessionAsync(
        DateTimeOffset? startedAt = null,
        string? leaseOwner = null,
        DateTimeOffset? leaseExpiresAt = null,
        bool isArchived = false,
        DateTimeOffset? resetRequestedAt = null,
        int number = 1)
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            Number = number,
            Title = $"Session {number}",
            Status = TaskStatus.Active,
            CurrentLane = Lane.Developing,
            IsInteractive = true,
            InteractiveSessionStartedAt = startedAt ?? DateTimeOffset.UtcNow,
            LeaseOwner = leaseOwner,
            LeaseExpiresAt = leaseExpiresAt,
            IsArchived = isArchived,
            ResetRequestedAt = resetRequestedAt,
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        return task;
    }

    [Fact]
    public async Task AwaitingPickup_IncludesUnheldSession()
    {
        var session = await SeedSessionAsync();

        var pending = await _api.ListInteractiveSessionsAwaitingPickupAsync();

        pending.Select(p => p.Id).Should().ContainSingle().Which.Should().Be(session.Id);
    }

    [Fact]
    public async Task AwaitingPickup_CarriesLeaseState_SoTheCallerCanDecide()
    {
        // The query deliberately does NOT filter on the lease. A worker that restarted still owns
        // leases for sessions nothing is driving any more, and only that worker knows which ones
        // it is actually running — filtering in SQL stranded those for the whole lease TTL after
        // every deploy. TaskQueueProcessor applies the rule; this just supplies the facts.
        var held = DateTimeOffset.UtcNow.AddMinutes(5);
        var session = await SeedSessionAsync(leaseOwner: "worker-1", leaseExpiresAt: held);

        var pending = await _api.ListInteractiveSessionsAwaitingPickupAsync();

        var info = pending.Should().ContainSingle().Subject;
        info.Id.Should().Be(session.Id);
        info.LeaseOwner.Should().Be("worker-1");
        info.LeaseExpiresAt.Should().BeCloseTo(held, TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public async Task AwaitingPickup_IncludesSessionWhoseWorkerDied()
    {
        // The recovery path: a lapsed lease means the holder is gone and the user is sitting in
        // front of a session nobody is driving.
        var session = await SeedSessionAsync(
            leaseOwner: "worker-1", leaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var pending = await _api.ListInteractiveSessionsAwaitingPickupAsync();

        pending.Select(p => p.Id).Should().ContainSingle().Which.Should().Be(session.Id);
    }

    [Fact]
    public async Task AwaitingPickup_ExcludesClosedArchivedAndResettingSessions()
    {
        // Session closed — the marker is what makes a session live.
        var closed = await SeedSessionAsync(number: 1);
        closed.InteractiveSessionStartedAt = null;
        await SeedSessionAsync(isArchived: true, number: 2);
        await SeedSessionAsync(resetRequestedAt: DateTimeOffset.UtcNow, number: 3);
        await _db.SaveChangesAsync();

        var pending = await _api.ListInteractiveSessionsAwaitingPickupAsync();

        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task AwaitingPickup_ExcludesOrdinaryTasks()
    {
        await SeedTaskAsync(TaskStatus.Active);
        await SeedTaskAsync(TaskStatus.InQueue, number: 2);

        var pending = await _api.ListInteractiveSessionsAwaitingPickupAsync();

        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task AwaitingPickup_OrdersLongestUnattendedFirst()
    {
        var newer = await SeedSessionAsync(
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-1), number: 1);
        var older = await SeedSessionAsync(
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-30), number: 2);

        var pending = await _api.ListInteractiveSessionsAwaitingPickupAsync();

        pending.Select(p => p.Id).Should().ContainInOrder(older.Id, newer.Id);
    }

    /// <summary>Seeds a message at a controlled instant. CreatedAt has to be applied AFTER the
    /// insert: ModuleDbContext stamps it on Added, so a value set up front is overwritten and
    /// every message ends up with the same "now" — which silently defeats any ordering or
    /// watermark assertion.</summary>
    private async Task SeedMessageAsync(
        TaskEntity task, string role, string content, DateTimeOffset createdAt)
    {
        var message = new TaskMessage
        {
            Id = Ulid.NewUlid(),
            OrgId = task.OrgId,
            TaskId = task.Id,
            Role = role,
            Content = content,
            MessageType = role == "user" ? MessageType.Answer : MessageType.Text,
        };
        _db.TaskMessages.Add(message);
        await _db.SaveChangesAsync();

        message.CreatedAt = createdAt;
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task UnansweredMessages_WithNoWatermark_ReturnsEveryHumanMessageOldestFirst()
    {
        var task = await SeedSessionAsync();
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        await SeedMessageAsync(task, "user", "second", start.AddMinutes(2));
        await SeedMessageAsync(task, "user", "first", start);

        var pending = await _api.ListUnansweredInteractiveMessagesAsync(task.Id, answeredThrough: null);

        pending.Select(p => p.Content).Should().ContainInOrder("first", "second");
    }

    [Fact]
    public async Task UnansweredMessages_ExcludesAgentAndSystemMessages()
    {
        var task = await SeedSessionAsync();
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        await SeedMessageAsync(task, "user", "human", start);
        await SeedMessageAsync(task, "agent", "agent reply", start.AddMinutes(1));
        await SeedMessageAsync(task, "system", "session ready", start.AddMinutes(2));

        var pending = await _api.ListUnansweredInteractiveMessagesAsync(task.Id, answeredThrough: null);

        pending.Select(p => p.Content).Should().ContainSingle().Which.Should().Be("human");
    }

    [Fact]
    public async Task UnansweredMessages_ExcludesEverythingAtOrBeforeTheWatermark()
    {
        var task = await SeedSessionAsync();
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        await SeedMessageAsync(task, "user", "answered", start);
        await SeedMessageAsync(task, "user", "new", start.AddMinutes(5));

        var pending = await _api.ListUnansweredInteractiveMessagesAsync(task.Id, answeredThrough: start);

        pending.Select(p => p.Content).Should().ContainSingle().Which.Should().Be("new");
    }

    // SetInteractiveAnsweredThroughAsync and CloseInteractiveSessionAsync are ExecuteUpdate
    // paths, which the in-memory provider cannot run. They are covered against the real database in
    // Orkka.IntegrationTests/InteractiveSessionTests.cs — including the follow-up-typed-while-
    // the-agent-was-working case, which is the whole reason the watermark is message-based.

    [Fact]
    public async Task ActiveTasks_ExcludeInteractiveSessions_SoTheyHoldNoSlot()
    {
        // MaxParallelTasks budgets autonomous work. A session is human-paced and mostly idle, so
        // holding a slot would stop the pipeline dead on the common setting of 1.
        await SeedTaskAsync(TaskStatus.Active, isInteractive: true, number: 1);

        var active = await _api.ListActiveTasksAsync(_projectId);

        active.Should().BeEmpty();
    }

    [Fact]
    public async Task ActiveTasks_StillCountOrdinaryRunningTasks()
    {
        var ordinary = await SeedTaskAsync(TaskStatus.Active, number: 1);
        await SeedTaskAsync(TaskStatus.Active, isInteractive: true, number: 2);

        var active = await _api.ListActiveTasksAsync(_projectId);

        active.Select(a => a.Id).Should().ContainSingle().Which.Should().Be(ordinary.Id);
    }

    [Fact]
    public async Task ActiveTasks_ExcludeASessionsRepo_SoSameRepoWorkIsNotBlocked()
    {
        // Without this, the exclusion would do nothing for single-repo projects — the queue would
        // still refuse to start a task on the repo the session happens to be using.
        var repoId = Ulid.NewUlid();
        await SeedTaskAsync(TaskStatus.Active, isInteractive: true, number: 1, repoId: repoId);

        var active = await _api.ListActiveTasksAsync(_projectId);

        active.Select(a => a.RepoId).Should().NotContain(repoId);
    }

    // TrySetGeneratedSessionTitleAsync is an ExecuteUpdate path (its guard lives in the WHERE
    // clause, which is the point) and the in-memory provider cannot run it. Covered against real
    // the real database in Orkka.IntegrationTests/InteractiveSessionTests.cs.

    [Fact]
    public async Task TaskSnapshot_CarriesInteractiveMarkers()
    {
        var task = await SeedTaskAsync(TaskStatus.Active, isInteractive: true);

        var snapshot = await _api.GetTaskSnapshotAsync(task.Id);

        snapshot.Should().NotBeNull();
        snapshot!.IsInteractive.Should().BeTrue();
        snapshot.InteractiveSessionStartedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TaskSnapshot_OrdinaryTaskIsNotInteractive()
    {
        var task = await SeedTaskAsync(TaskStatus.Active);

        var snapshot = await _api.GetTaskSnapshotAsync(task.Id);

        snapshot.Should().NotBeNull();
        snapshot!.IsInteractive.Should().BeFalse();
        snapshot.InteractiveSessionStartedAt.Should().BeNull();
    }
}
