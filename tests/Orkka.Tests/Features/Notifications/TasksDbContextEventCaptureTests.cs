namespace Orkka.Tests.Features.Notifications;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// TasksDbContext publishes TaskStatusChanged/TaskMergeAttemptFailed for tracked saves that
/// move a task into an attention status or record a merge failure — the single interception
/// point behind in-app notifications.
/// </summary>
public sealed class TasksDbContextEventCaptureTests : IDisposable
{
    private sealed class RecordingEventBus : IEventBus
    {
        public List<IntegrationEvent> Published { get; } = [];

        public ValueTask PublishAsync(IntegrationEvent evt, CancellationToken ct = default)
        {
            Published.Add(evt);
            return ValueTask.CompletedTask;
        }
    }

    private readonly RecordingEventBus _bus = new();
    private readonly TasksDbContext _db;

    public TasksDbContextEventCaptureTests()
    {
        var options = new DbContextOptionsBuilder<TasksDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new TasksDbContext(options, currentUser: null, eventBus: _bus);
    }

    public void Dispose() => _db.Dispose();

    private async Task<TaskEntity> SeedTaskAsync(TaskStatus status = TaskStatus.Active)
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            Number = 12,
            Title = "Fix flaky test",
            CreatedByUserId = "user_creator",
            Status = status,
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        _bus.Published.Clear();
        return task;
    }

    [Fact]
    public async Task TransitionIntoAttentionStatus_PublishesTaskStatusChanged()
    {
        var task = await SeedTaskAsync();

        task.Status = TaskStatus.WaitingPlanApproval;
        await _db.SaveChangesAsync();

        var evt = _bus.Published.Should().ContainSingle()
            .Which.Should().BeOfType<TaskStatusChanged>().Subject;
        evt.TaskId.Should().Be(task.Id);
        evt.NewStatus.Should().Be("WaitingPlanApproval");
        evt.TaskNumber.Should().Be(12);
        evt.TaskTitle.Should().Be("Fix flaky test");
        evt.CreatedByUserId.Should().Be("user_creator");
        evt.ActorUserId.Should().BeNull();
    }

    [Fact]
    public async Task TransitionIntoWaitingUserVerification_PublishesTaskStatusChanged()
    {
        var task = await SeedTaskAsync();

        task.Status = TaskStatus.WaitingUserVerification;
        await _db.SaveChangesAsync();

        var evt = _bus.Published.Should().ContainSingle()
            .Which.Should().BeOfType<TaskStatusChanged>().Subject;
        evt.TaskId.Should().Be(task.Id);
        evt.NewStatus.Should().Be("WaitingUserVerification");
        evt.CreatedByUserId.Should().Be("user_creator");
    }

    [Fact]
    public async Task TransitionIntoNonAttentionStatus_PublishesNothing()
    {
        var task = await SeedTaskAsync(TaskStatus.InQueue);

        task.Status = TaskStatus.Active;
        await _db.SaveChangesAsync();

        _bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task UnrelatedFieldChange_PublishesNothing()
    {
        var task = await SeedTaskAsync();

        task.Title = "Renamed";
        await _db.SaveChangesAsync();

        _bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task AddedEntity_PublishesNothing()
    {
        // Only Modified entries publish — new tasks start in Draft and creating one is
        // not an attention transition even if seeded with an attention status.
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            Number = 1,
            Title = "New",
            Status = TaskStatus.Completed,
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        _bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task MergeFailureReasonSet_PublishesTaskMergeAttemptFailed()
    {
        var task = await SeedTaskAsync();

        task.MergeFailureReason = "Branch protection rejected the merge";
        await _db.SaveChangesAsync();

        var evt = _bus.Published.Should().ContainSingle()
            .Which.Should().BeOfType<TaskMergeAttemptFailed>().Subject;
        evt.Reason.Should().Be("Branch protection rejected the merge");
    }

    [Fact]
    public async Task MergeFailureReasonCleared_PublishesNothing()
    {
        var task = await SeedTaskAsync();
        task.MergeFailureReason = "conflict";
        await _db.SaveChangesAsync();
        _bus.Published.Clear();

        task.MergeFailureReason = null;
        await _db.SaveChangesAsync();

        _bus.Published.Should().BeEmpty();
    }
}
