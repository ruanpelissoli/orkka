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
/// The bound on base-branch rework. Sending a collision back to a dev agent is the right first
/// move — it is the only party with context for both sides — but each lap is a full agent run,
/// so the interesting behavior is where the laps stop.
/// </summary>
public sealed class BaseSyncRequeueTests : IDisposable
{
    private const int MaxAttempts = 3;

    private readonly TasksDbContext _db;
    private readonly TasksApi _api;

    public BaseSyncRequeueTests()
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

    private async Task<TaskEntity> SeedTaskAsync(Lane lane, int baseSyncCycleCount = 0)
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            Number = 1,
            Title = "Task under test",
            CurrentLane = lane,
            Status = TaskStatus.Completed,
            HasAgentWorking = true,
            BaseSyncCycleCount = baseSyncCycleCount,
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        return task;
    }

    [Fact]
    public async Task Requeue_FromDone_ReopensDevelopingAndFreesTheAgentSlot()
    {
        var task = await SeedTaskAsync(Lane.Done);

        var result = await _api.RequeueForBaseSyncAsync(task.Id, MaxAttempts, "main moved");

        result!.EscalatedToHuman.Should().BeFalse();
        result.FromLane.Should().Be(Lane.Done);
        result.NewLane.Should().Be(Lane.Developing);
        result.NewStatus.Should().Be(TaskStatus.InQueue);
        result.BaseSyncCycleCount.Should().Be(1);

        var reloaded = await _db.Tasks.SingleAsync(t => t.Id == task.Id);
        reloaded.HasAgentWorking.Should().BeFalse();
        // Not stamped on the way back out — the merge page shows a reason only once the task
        // has actually stopped being worked on.
        reloaded.MergeFailureReason.Should().BeNull();
    }

    [Fact]
    public async Task Snapshot_CarriesBaseSyncCycleCount()
    {
        // The end-of-Developing sync reads the count off the snapshot to fail OPEN at the
        // cap (a finished run completes; merge-time resolution is the backstop). A snapshot
        // that silently dropped the field would make the cap check read 0 forever and the
        // finish-line sync would requeue past the bound.
        var task = await SeedTaskAsync(Lane.Developing, baseSyncCycleCount: 2);

        var snapshot = await _api.GetTaskSnapshotAsync(task.Id);

        snapshot!.BaseSyncCycleCount.Should().Be(2);
    }

    [Fact]
    public async Task Requeue_CountsUpAcrossPasses()
    {
        var task = await SeedTaskAsync(Lane.CodeReview, baseSyncCycleCount: 1);

        var result = await _api.RequeueForBaseSyncAsync(task.Id, MaxAttempts, "main moved");

        result!.BaseSyncCycleCount.Should().Be(2);
        result.EscalatedToHuman.Should().BeFalse();
        result.NewLane.Should().Be(Lane.Developing);
    }

    [Fact]
    public async Task Requeue_OnTheLastAttempt_ParksForAHumanInsteadOfLooping()
    {
        var task = await SeedTaskAsync(Lane.CodeReview, baseSyncCycleCount: MaxAttempts - 1);

        var result = await _api.RequeueForBaseSyncAsync(
            task.Id, MaxAttempts, "could not merge main");

        result!.EscalatedToHuman.Should().BeTrue();
        result.BaseSyncCycleCount.Should().Be(MaxAttempts);
        result.NewStatus.Should().Be(TaskStatus.WaitingCodeReview);

        // The lane must NOT move: bouncing it to Developing and then parking it there would
        // hide a task needing attention behind a lane that means "an agent is on it".
        result.NewLane.Should().Be(Lane.CodeReview);

        var reloaded = await _db.Tasks.SingleAsync(t => t.Id == task.Id);
        reloaded.CurrentLane.Should().Be(Lane.CodeReview);
        reloaded.HasAgentWorking.Should().BeFalse();
        reloaded.MergeFailureReason.Should().Be("could not merge main");
    }

    [Fact]
    public async Task Requeue_PastTheCap_StaysParked()
    {
        var task = await SeedTaskAsync(Lane.CodeReview, baseSyncCycleCount: MaxAttempts + 5);

        var result = await _api.RequeueForBaseSyncAsync(task.Id, MaxAttempts, "still stuck");

        result!.EscalatedToHuman.Should().BeTrue();
        result.NewLane.Should().Be(Lane.CodeReview);
    }

    [Fact]
    public async Task Requeue_MissingTask_ReturnsNull()
    {
        var result = await _api.RequeueForBaseSyncAsync(Ulid.NewUlid(), MaxAttempts, "gone");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Requeue_Escalation_MarksTheFailureAsResolutionExhausted()
    {
        var task = await SeedTaskAsync(Lane.CodeReview, baseSyncCycleCount: MaxAttempts - 1);

        await _api.RequeueForBaseSyncAsync(task.Id, MaxAttempts, "could not merge main");

        var reloaded = await _db.Tasks.SingleAsync(t => t.Id == task.Id);
        reloaded.MergeFailureKind.Should().Be(MergeFailureKind.ResolutionExhausted);
    }

    [Fact]
    public async Task Requeue_BelowTheCap_LeavesTheFailureKindUnset()
    {
        var task = await SeedTaskAsync(Lane.CodeReview);

        await _api.RequeueForBaseSyncAsync(task.Id, MaxAttempts, "main moved");

        // Still on the retry path — labelling it terminal here would tell the user to intervene
        // in something the platform is actively fixing.
        var reloaded = await _db.Tasks.SingleAsync(t => t.Id == task.Id);
        reloaded.MergeFailureKind.Should().BeNull();
    }

    [Fact]
    public async Task SetMergeFailure_KindEscalates_CountsAsAChangeWithoutASecondMessage()
    {
        var task = await SeedTaskAsync(Lane.CodeReview);

        var first = await _api.SetTaskMergeFailureAsync(task.Id, "same reason");
        first!.Changed.Should().BeTrue();

        // Same text, but automation has now given up. The transition has to reach the UI…
        var escalated = await _api.SetTaskMergeFailureAsync(
            task.Id, "same reason", MergeFailureKind.ResolutionExhausted);
        escalated!.Changed.Should().BeTrue();

        // …without repeating a thread message whose text the human already read.
        escalated.Message.Should().BeNull();
        (await _db.TaskMessages.CountAsync(m => m.TaskId == task.Id)).Should().Be(1);

        var reloaded = await _db.Tasks.SingleAsync(t => t.Id == task.Id);
        reloaded.MergeFailureKind.Should().Be(MergeFailureKind.ResolutionExhausted);
    }

    [Fact]
    public async Task SetMergeFailure_IdenticalReasonAndKind_IsNotAChange()
    {
        var task = await SeedTaskAsync(Lane.CodeReview);

        await _api.SetTaskMergeFailureAsync(task.Id, "branch protection", MergeFailureKind.ForgeRejected);
        var repeat = await _api.SetTaskMergeFailureAsync(
            task.Id, "branch protection", MergeFailureKind.ForgeRejected);

        // The poller re-reports this every cycle; it must stay silent after the first time.
        repeat!.Changed.Should().BeFalse();
        (await _db.TaskMessages.CountAsync(m => m.TaskId == task.Id)).Should().Be(1);
    }
}
