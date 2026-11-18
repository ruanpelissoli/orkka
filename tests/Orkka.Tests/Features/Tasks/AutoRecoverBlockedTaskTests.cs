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
/// The data half of the provider-outage recovery sweep: which Blocked tasks it may touch, and
/// what spending a round does. The interesting behavior is the guards — every one of them is a
/// task that must NOT be silently re-run (a live incident, an exhausted budget, a human's
/// pending reset), because an unwanted re-run bills the org for a repeat failure.
/// </summary>
public sealed class AutoRecoverBlockedTaskTests : IDisposable
{
    private const int MaxRounds = 3;
    private static readonly DateTimeOffset Cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);

    private readonly TasksDbContext _db;
    private readonly TasksApi _api;

    public AutoRecoverBlockedTaskTests()
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

    private async Task<TaskEntity> SeedBlockedTaskAsync(
        int autoRecoveryCount = 0,
        string? error = "API Error: Connection lost mid-response",
        DateTimeOffset? failedAt = null,
        Action<TaskEntity>? mutate = null)
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            Number = 1,
            Title = "Task under test",
            CurrentLane = Lane.Developing,
            Status = TaskStatus.Blocked,
            AutoRecoveryCount = autoRecoveryCount,
        };
        mutate?.Invoke(task);
        _db.Tasks.Add(task);

        if (error is not null)
        {
            _db.TaskExecutions.Add(new TaskExecution
            {
                Id = Ulid.NewUlid(),
                TaskId = task.Id,
                OrgId = task.OrgId,
                Lane = task.CurrentLane,
                Status = ExecutionStatus.Failed,
                ErrorMessage = error,
                CompletedAt = failedAt ?? Cutoff.AddMinutes(-5),
            });
        }

        await _db.SaveChangesAsync();
        return task;
    }

    [Fact]
    public async Task List_ReturnsBlockedTaskWithItsLastError()
    {
        var task = await SeedBlockedTaskAsync();

        var candidates = await _api.ListAutoRecoverableBlockedTasksAsync(Cutoff, MaxRounds);

        candidates.Should().ContainSingle();
        candidates[0].Id.Should().Be(task.Id);
        candidates[0].LastError.Should().Be("API Error: Connection lost mid-response");
        candidates[0].AutoRecoveryCount.Should().Be(0);
    }

    [Fact]
    public async Task List_FreshFailure_WaitsOutTheBackoff()
    {
        // Failed a moment ago: the incident may still be live, and re-queueing now would
        // just burn one of the task's bounded rounds against the same outage.
        await SeedBlockedTaskAsync(failedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var candidates = await _api.ListAutoRecoverableBlockedTasksAsync(Cutoff, MaxRounds);

        candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task List_AtTheRoundCap_LeavesTheTaskForAHuman()
    {
        await SeedBlockedTaskAsync(autoRecoveryCount: MaxRounds);

        var candidates = await _api.ListAutoRecoverableBlockedTasksAsync(Cutoff, MaxRounds);

        candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task List_SkipsArchivedResetPendingAndInteractiveTasks()
    {
        await SeedBlockedTaskAsync(mutate: t => t.IsArchived = true);
        await SeedBlockedTaskAsync(mutate: t => t.ResetRequestedAt = DateTimeOffset.UtcNow);
        await SeedBlockedTaskAsync(mutate: t => t.IsInteractive = true);

        var candidates = await _api.ListAutoRecoverableBlockedTasksAsync(Cutoff, MaxRounds);

        candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task List_SkipsTasksWithALiveLease()
    {
        // A live lease means some replica's run is still winding down — the state it leaves
        // behind is the one to classify, not this mid-flight snapshot.
        await SeedBlockedTaskAsync(mutate: t =>
        {
            t.LeaseOwner = "worker-1";
            t.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(4);
        });

        var candidates = await _api.ListAutoRecoverableBlockedTasksAsync(Cutoff, MaxRounds);

        candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task List_ExpiredLease_DoesNotBlockRecovery()
    {
        await SeedBlockedTaskAsync(mutate: t =>
        {
            t.LeaseOwner = "worker-1";
            t.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        });

        var candidates = await _api.ListAutoRecoverableBlockedTasksAsync(Cutoff, MaxRounds);

        candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task List_WithNoFailedExecution_HasNothingToClassify()
    {
        await SeedBlockedTaskAsync(error: null);

        var candidates = await _api.ListAutoRecoverableBlockedTasksAsync(Cutoff, MaxRounds);

        candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task List_CarriesTheNewestFailure_NotAnOlderOne()
    {
        // The task is blocked ON its newest failure. An older error may be a real defect the
        // human already retried past — classifying that one could re-run a task whose current
        // failure is not transient at all.
        var task = await SeedBlockedTaskAsync(
            error: "assertion failed: expected 200", failedAt: Cutoff.AddHours(-2));
        _db.TaskExecutions.Add(new TaskExecution
        {
            Id = Ulid.NewUlid(),
            TaskId = task.Id,
            OrgId = task.OrgId,
            Lane = Lane.Developing,
            Status = ExecutionStatus.Failed,
            ErrorMessage = "Overloaded",
            CompletedAt = Cutoff.AddMinutes(-5),
        });
        await _db.SaveChangesAsync();

        var candidates = await _api.ListAutoRecoverableBlockedTasksAsync(Cutoff, MaxRounds);

        candidates.Should().ContainSingle();
        candidates[0].LastError.Should().Be("Overloaded");
    }

    [Fact]
    public async Task Recover_SpendsARoundAndRequeuesInPlace()
    {
        var task = await SeedBlockedTaskAsync(autoRecoveryCount: 1);

        var recovered = await _api.TryAutoRecoverBlockedTaskAsync(task.Id, MaxRounds);

        recovered.Should().BeTrue();
        var reloaded = await _db.Tasks.SingleAsync(t => t.Id == task.Id);
        reloaded.Status.Should().Be(TaskStatus.InQueue);
        reloaded.AutoRecoveryCount.Should().Be(2);
        // Same semantics as the Retry endpoint: the lane does not move, so the task re-enters
        // the phase it was in when the provider dropped it.
        reloaded.CurrentLane.Should().Be(Lane.Developing);
    }

    [Fact]
    public async Task Recover_TaskNoLongerBlocked_IsARaceTheSweepLoses()
    {
        // A human pressed Retry between the listing and this call. Their action already
        // re-queued the task — spending an automatic round on top would double-charge it.
        var task = await SeedBlockedTaskAsync();
        var entity = await _db.Tasks.SingleAsync(t => t.Id == task.Id);
        entity.Status = TaskStatus.InQueue;
        await _db.SaveChangesAsync();

        var recovered = await _api.TryAutoRecoverBlockedTaskAsync(task.Id, MaxRounds);

        recovered.Should().BeFalse();
        (await _db.Tasks.SingleAsync(t => t.Id == task.Id)).AutoRecoveryCount.Should().Be(0);
    }

    [Fact]
    public async Task Recover_AtTheCap_Refuses()
    {
        var task = await SeedBlockedTaskAsync(autoRecoveryCount: MaxRounds);

        var recovered = await _api.TryAutoRecoverBlockedTaskAsync(task.Id, MaxRounds);

        recovered.Should().BeFalse();
        (await _db.Tasks.SingleAsync(t => t.Id == task.Id)).Status.Should().Be(TaskStatus.Blocked);
    }

    [Fact]
    public async Task Recover_MissingTask_ReturnsFalse()
    {
        var recovered = await _api.TryAutoRecoverBlockedTaskAsync(Ulid.NewUlid(), MaxRounds);

        recovered.Should().BeFalse();
    }
}
