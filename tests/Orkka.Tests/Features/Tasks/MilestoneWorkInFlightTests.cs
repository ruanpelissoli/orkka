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
/// "Is a milestone still landing?" — what keeps an autopilot session open when the next
/// milestone's tasks are gated on it. Prod, 2026-09-17: a session started for milestone 4
/// drained itself in fifteen seconds while milestone 3's last two tasks were still merging.
/// </summary>
public sealed class MilestoneWorkInFlightTests : IDisposable
{
    private readonly TasksDbContext _db;
    private readonly TasksApi _api;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _projectId = Ulid.NewUlid();
    private int _number;

    public MilestoneWorkInFlightTests()
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

    [Fact]
    public async Task ARunningTask_UnderAMilestone_MeansItIsStillLanding()
    {
        var milestone = await SeedMilestoneAsync();
        await SeedTaskAsync(milestone.Id, TaskStatus.Active, Lane.Developing);

        (await _api.HasMilestoneWorkInFlightAsync(_projectId)).Should().BeTrue();
    }

    [Fact]
    public async Task ATaskParkedForItsRunner_StillCounts_ButOneParkedOnAPersonDoesNot()
    {
        var milestone = await SeedMilestoneAsync();
        var task = await SeedTaskAsync(milestone.Id, TaskStatus.WaitingForRunner, Lane.Developing);
        (await _api.HasMilestoneWorkInFlightAsync(_projectId)).Should().BeTrue();

        task.Status = TaskStatus.WaitingAnswer;
        await _db.SaveChangesAsync();
        (await _api.HasMilestoneWorkInFlightAsync(_projectId)).Should().BeFalse(
            "a question for a human does not resolve itself — holding on it holds until morning");
    }

    [Fact]
    public async Task AFinishedTask_WhosePrHasNotMerged_IsStillLanding()
    {
        var milestone = await SeedMilestoneAsync();
        var task = await SeedTaskAsync(milestone.Id, TaskStatus.Completed, Lane.Done, prUrl: "orkka-local://pull/20");
        (await _api.HasMilestoneWorkInFlightAsync(_projectId)).Should().BeTrue();

        task.IsMerged = true;
        await _db.SaveChangesAsync();
        (await _api.HasMilestoneWorkInFlightAsync(_projectId)).Should().BeFalse();
    }

    [Fact]
    public async Task AShipThatIsPending_IsStillLanding_AFailedOneIsNot()
    {
        var milestone = await SeedMilestoneAsync(MilestoneShipState.Pending);
        (await _api.HasMilestoneWorkInFlightAsync(_projectId)).Should().BeTrue();

        milestone.ShipState = MilestoneShipState.Failed;
        await _db.SaveChangesAsync();
        (await _api.HasMilestoneWorkInFlightAsync(_projectId)).Should().BeFalse(
            "a failed ship needs a human; the session drains and says so");
    }

    [Fact]
    public async Task BacklogWork_OutsideAnyMilestone_DoesNotCount()
    {
        await SeedTaskAsync(milestoneId: null, TaskStatus.Active, Lane.Developing);

        (await _api.HasMilestoneWorkInFlightAsync(_projectId)).Should().BeFalse();
    }

    private async Task<Milestone> SeedMilestoneAsync(MilestoneShipState shipState = MilestoneShipState.NotReady)
    {
        var milestone = new Milestone
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = _projectId,
            Title = "Milestone",
            Status = MilestoneStatus.Active,
            ShipState = shipState,
        };
        _db.Milestones.Add(milestone);
        await _db.SaveChangesAsync();
        return milestone;
    }

    private async Task<TaskEntity> SeedTaskAsync(Ulid? milestoneId, TaskStatus status, Lane lane, string? prUrl = null)
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = _projectId,
            MilestoneId = milestoneId,
            RepoId = Ulid.NewUlid(),
            Number = ++_number,
            Title = $"Task {_number}",
            Status = status,
            CurrentLane = lane,
            PrUrl = prUrl,
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        return task;
    }
}
