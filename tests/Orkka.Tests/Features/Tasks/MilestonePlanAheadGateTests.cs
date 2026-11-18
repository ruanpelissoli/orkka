namespace Orkka.Tests.Features.Tasks;

using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Milestones;
using Orkka.Modules.Tasks.Infrastructure;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Planning a milestone's tasks while the milestone it waits on finishes its last builds —
/// without ever BUILDING ahead of the gate. Prod, 2026-09-17: ten drafted tasks of the next
/// milestone sat untouched while one task of the previous milestone finished, and autopilot
/// stopped itself twice in the meantime.
///
/// Three questions, three expressions: may autopilot submit it (planning ahead is allowed once
/// every blocker is past planning), may the queue pick it up (only for its prep runs), and is
/// the ordinary gate — Shipped — still what releases the build.
/// </summary>
public sealed class MilestonePlanAheadGateTests : IDisposable
{
    private readonly TasksDbContext _db;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _projectId = Ulid.NewUlid();
    private int _position;

    public MilestonePlanAheadGateTests()
    {
        var options = new DbContextOptionsBuilder<TasksDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new TasksDbContext(options, currentUser: null, eventBus: null);
    }

    public void Dispose() => _db.Dispose();

    // ── Submission: planning ahead of the gate ────────────────────────────────

    [Fact]
    public async Task OnceEveryBlockerTaskIsBuilding_TheNextMilestonesTasksMayBeSubmittedToPlan()
    {
        var (current, next) = await PairAsync();
        await TaskInAsync(current, Lane.Developing);
        await TaskInAsync(current, Lane.CodeReview);
        await TaskInAsync(current, Lane.Done);
        var waiting = await TaskInAsync(next, Lane.Drafting);

        (await HoldsAsync(MilestoneDependencyQueries.MilestoneUnblocked, waiting)).Should().BeFalse(
            "the build gate is unchanged: nothing has shipped");
        (await HoldsAsync(MilestoneDependencyQueries.PlanAheadAcrossGate, waiting)).Should().BeTrue();
        (await HoldsAsync(MilestoneDependencyQueries.AutopilotMaySubmit, waiting)).Should().BeTrue();
    }

    [Theory]
    [InlineData(Lane.Drafting)]
    [InlineData(Lane.Ready)]
    [InlineData(Lane.Planning)]
    [InlineData(Lane.PlanReady)]
    public async Task WhileABlockerTaskIsStillBeingPlanned_NothingPlansAhead(Lane blockerLane)
    {
        var (current, next) = await PairAsync();
        await TaskInAsync(current, Lane.Developing);
        await TaskInAsync(current, blockerLane);
        var waiting = await TaskInAsync(next, Lane.Drafting);

        (await HoldsAsync(MilestoneDependencyQueries.AutopilotMaySubmit, waiting)).Should().BeFalse(
            "a plan for the next milestone written before this one is designed plans against code nobody has decided on");
    }

    [Fact]
    public async Task AnArchivedDraft_InTheBlocker_DoesNotHoldPlanningBack()
    {
        var (current, next) = await PairAsync();
        await TaskInAsync(current, Lane.Developing);
        await TaskInAsync(current, Lane.Drafting, archived: true);
        var waiting = await TaskInAsync(next, Lane.Drafting);

        (await HoldsAsync(MilestoneDependencyQueries.PlanAheadAcrossGate, waiting)).Should().BeTrue();
    }

    [Fact]
    public async Task ABlockerWhoseShipFailed_HoldsPlanningToo()
    {
        var (current, next) = await PairAsync(MilestoneStatus.Completed, MilestoneShipState.Failed);
        await TaskInAsync(current, Lane.Done);
        var waiting = await TaskInAsync(next, Lane.Drafting);

        (await HoldsAsync(MilestoneDependencyQueries.AutopilotMaySubmit, waiting)).Should().BeFalse(
            "a failed ship needs a human; the base it was supposed to give is not coming on its own");
    }

    [Fact]
    public async Task ABlockerWithNoTasksYet_HoldsPlanning()
    {
        var (_, next) = await PairAsync(MilestoneStatus.Planned);
        var waiting = await TaskInAsync(next, Lane.Drafting);

        (await HoldsAsync(MilestoneDependencyQueries.AutopilotMaySubmit, waiting)).Should().BeFalse();
    }

    [Fact]
    public async Task ASkipPlanningTask_NeverGoesAheadOfTheGate()
    {
        var (current, next) = await PairAsync();
        await TaskInAsync(current, Lane.Developing);
        var waiting = await TaskInAsync(next, Lane.Drafting, skipPlanning: true);

        (await HoldsAsync(MilestoneDependencyQueries.AutopilotMaySubmit, waiting)).Should().BeFalse(
            "its first run is the build");
    }

    [Fact]
    public async Task EveryBlocker_MustBePastPlanning()
    {
        var (first, next) = await PairAsync();
        await TaskInAsync(first, Lane.Developing);
        var second = await MilestoneAsync(MilestoneStatus.Active, MilestoneShipState.NotReady);
        await TaskInAsync(second, Lane.Planning);
        await EdgeAsync(next, second);
        var waiting = await TaskInAsync(next, Lane.Drafting);

        (await HoldsAsync(MilestoneDependencyQueries.AutopilotMaySubmit, waiting)).Should().BeFalse();
    }

    // ── Pickup: prep runs go ahead, the build waits for the ship ──────────────

    [Fact]
    public async Task AnAutopilotPlanningRun_IsPickedUp_ButTheSameTasksBuildIsHeld()
    {
        var (current, next) = await PairAsync();
        await TaskInAsync(current, Lane.Developing);
        var task = await TaskInAsync(next, Lane.Planning, autopilot: true);

        (await HoldsAsync(MilestoneDependencyQueries.MayBePickedUp, task)).Should().BeTrue();

        task.CurrentLane = Lane.Ready;
        await _db.SaveChangesAsync();
        (await HoldsAsync(MilestoneDependencyQueries.MayBePickedUp, task)).Should().BeTrue("PM review is prep too");

        task.CurrentLane = Lane.Developing;
        await _db.SaveChangesAsync();
        (await HoldsAsync(MilestoneDependencyQueries.MayBePickedUp, task)).Should().BeFalse(
            "the approved plan is banked; the build starts from a base that has the previous milestone");
    }

    [Fact]
    public async Task AHandSubmittedPlanningRun_StaysBehindTheGate()
    {
        var (current, next) = await PairAsync();
        await TaskInAsync(current, Lane.Developing);
        var task = await TaskInAsync(next, Lane.Planning, autopilot: false);

        (await HoldsAsync(MilestoneDependencyQueries.MayBePickedUp, task)).Should().BeFalse(
            "a hand-submitted plan parks for a human — planning it early buys nothing");
    }

    [Fact]
    public async Task OnceTheBlockerShips_TheBuildIsReleased()
    {
        var (current, next) = await PairAsync(MilestoneStatus.Completed, MilestoneShipState.Shipped);
        await TaskInAsync(current, Lane.Done);
        var task = await TaskInAsync(next, Lane.Developing, autopilot: true);

        (await HoldsAsync(MilestoneDependencyQueries.MayBePickedUp, task)).Should().BeTrue();
    }

    [Fact]
    public async Task BacklogWork_IsNeverGated_EitherWay()
    {
        var task = await TaskInAsync(milestone: null, Lane.Developing);

        (await HoldsAsync(MilestoneDependencyQueries.MayBePickedUp, task)).Should().BeTrue();
        (await HoldsAsync(MilestoneDependencyQueries.AutopilotMaySubmit, task)).Should().BeTrue();
    }

    // ── harness ───────────────────────────────────────────────────────────────

    private async Task<bool> HoldsAsync(Expression<Func<TaskEntity, bool>> gate, TaskEntity task)
        => await _db.Tasks.AsNoTracking().Where(t => t.Id == task.Id).Where(gate).AnyAsync();

    /// <summary>"next" waits on "current".</summary>
    private async Task<(Milestone Current, Milestone Next)> PairAsync(
        MilestoneStatus currentStatus = MilestoneStatus.Active,
        MilestoneShipState currentShip = MilestoneShipState.NotReady)
    {
        var current = await MilestoneAsync(currentStatus, currentShip);
        var next = await MilestoneAsync(MilestoneStatus.Planned, MilestoneShipState.NotReady);
        await EdgeAsync(next, current);
        return (current, next);
    }

    private async Task<Milestone> MilestoneAsync(MilestoneStatus status, MilestoneShipState ship)
    {
        var milestone = new Milestone
        {
            Id = Ulid.NewUlid(), OrgId = _orgId, ProjectId = _projectId,
            Title = $"Milestone {++_position}", Position = _position,
            Status = status, ShipState = ship,
        };
        _db.Milestones.Add(milestone);
        await _db.SaveChangesAsync();
        return milestone;
    }

    private async Task EdgeAsync(Milestone dependent, Milestone blocker)
    {
        _db.MilestoneDependencies.Add(new MilestoneDependency
        {
            Id = Ulid.NewUlid(), OrgId = _orgId,
            MilestoneId = dependent.Id, DependsOnMilestoneId = blocker.Id,
            Source = DependencySource.Manual,
        });
        await _db.SaveChangesAsync();
    }

    private async Task<TaskEntity> TaskInAsync(
        Milestone? milestone, Lane lane, bool skipPlanning = false, bool autopilot = false, bool archived = false)
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(), OrgId = _orgId, ProjectId = _projectId,
            Number = ++_position, Title = "Task",
            MilestoneId = milestone?.Id,
            CurrentLane = lane,
            Status = lane == Lane.Drafting ? TaskStatus.Draft : TaskStatus.InQueue,
            SkipPlanning = skipPlanning,
            AutopilotSessionId = autopilot ? Ulid.NewUlid() : null,
            IsArchived = archived,
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        return task;
    }
}
