namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Milestones;
using Orkka.Modules.Tasks.Infrastructure;

/// <summary>
/// The milestone-level gate: "do not start this milestone until that one is on the default
/// branch."
///
/// A milestone branch is cut FROM the default branch, and the default branch only moves when a
/// milestone SHIPS. So the bar has to be Shipped, not Completed — and the gap between those two
/// is real, not theoretical: MilestoneProgressUpdater sets Completed and arms ShipState.Pending
/// in the same save, minutes before the merge lands. A gate that released at Completed would let
/// the dependent cut its branch from a tree that still lacks everything it was told to build on,
/// which is the exact failure these edges exist to prevent.
///
/// The other half is the states that will never reach Shipped. A milestone with no branch, or
/// one whose repos are all local-path, is returned to NotReady by MarkMilestoneNothingToShipAsync
/// and stays there forever. Requiring Shipped would wedge the whole roadmap behind it.
/// </summary>
public sealed class MilestoneDependencyGateTests : IDisposable
{
    private readonly TasksDbContext _db;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _projectId = Ulid.NewUlid();
    private int _position;

    public MilestoneDependencyGateTests()
    {
        var options = new DbContextOptionsBuilder<TasksDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new TasksDbContext(options, currentUser: null, eventBus: null);
    }

    public void Dispose() => _db.Dispose();

    private Milestone Milestone(MilestoneStatus status, MilestoneShipState shipState)
    {
        var milestone = new Milestone
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = _projectId,
            Title = $"Milestone {++_position}",
            Position = _position,
            Status = status,
            ShipState = shipState,
        };
        _db.Milestones.Add(milestone);
        return milestone;
    }

    private TaskEntity TaskIn(Milestone? milestone)
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = _projectId,
            Number = ++_position,
            Title = "Task",
            MilestoneId = milestone?.Id,
            Milestone = milestone,
        };
        _db.Tasks.Add(task);
        return task;
    }

    /// <summary>Adds "dependent waits on blocker" and reports whether the gate lets it through.</summary>
    private async Task<bool> IsUnblockedAsync(Milestone dependent, Milestone blocker)
    {
        _db.MilestoneDependencies.Add(new MilestoneDependency
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            MilestoneId = dependent.Id,
            DependsOnMilestoneId = blocker.Id,
            Source = DependencySource.Ai,
        });
        await _db.SaveChangesAsync();

        return await _db.Milestones
            .Where(m => m.Id == dependent.Id)
            .Where(MilestoneDependencyQueries.IsUnblocked)
            .AnyAsync();
    }

    [Fact]
    public async Task Blocks_WhileTheBlockerIsCompletedButNotYetMerged()
    {
        // The state MilestoneProgressUpdater leaves behind the instant the last task is Done.
        var blocker = Milestone(MilestoneStatus.Completed, MilestoneShipState.Pending);
        var dependent = Milestone(MilestoneStatus.Planned, MilestoneShipState.NotReady);

        (await IsUnblockedAsync(dependent, blocker)).Should().BeFalse(
            "Completed means the tasks are done, not that the code is on the default branch");
    }

    [Fact]
    public async Task Releases_OnceTheBlockerHasShipped()
    {
        var blocker = Milestone(MilestoneStatus.Completed, MilestoneShipState.Shipped);
        var dependent = Milestone(MilestoneStatus.Planned, MilestoneShipState.NotReady);

        (await IsUnblockedAsync(dependent, blocker)).Should().BeTrue();
    }

    [Fact]
    public async Task Releases_TheMomentATrunkBlockerCompletes()
    {
        // A Trunk milestone's tasks already landed on the default branch one by one, so the
        // progress updater writes Shipped in the same update that writes Completed. The gate
        // only reads Status + ShipState, so it releases with no rule of its own — which is the
        // point: milestone 2 of a new managed project must be plannable the moment milestone 1's
        // last task merges, not after a ship sweep that never runs for it.
        var blocker = Milestone(MilestoneStatus.Completed, MilestoneShipState.Shipped);
        blocker.IntegrationMode = MilestoneIntegrationMode.Trunk;
        var dependent = Milestone(MilestoneStatus.Planned, MilestoneShipState.NotReady);

        (await IsUnblockedAsync(dependent, blocker)).Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_WhileTheShipIsStillInFlight()
    {
        var blocker = Milestone(MilestoneStatus.Completed, MilestoneShipState.Shipping);
        var dependent = Milestone(MilestoneStatus.Planned, MilestoneShipState.NotReady);

        (await IsUnblockedAsync(dependent, blocker)).Should().BeFalse();
    }

    [Fact]
    public async Task Blocks_WhenTheShipFailed()
    {
        var blocker = Milestone(MilestoneStatus.Completed, MilestoneShipState.Failed);
        var dependent = Milestone(MilestoneStatus.Planned, MilestoneShipState.NotReady);

        (await IsUnblockedAsync(dependent, blocker)).Should().BeFalse(
            "a failed ship needs a human, and building on a base that was supposed to have it "
            + "is worse than waiting");
    }

    [Fact]
    public async Task Releases_WhenTheBlockerHasNothingToShip()
    {
        // No branch, or every repo local-path: MarkMilestoneNothingToShipAsync puts it back to
        // NotReady and nothing will ever move it to Shipped.
        var blocker = Milestone(MilestoneStatus.Completed, MilestoneShipState.NotReady);
        var dependent = Milestone(MilestoneStatus.Planned, MilestoneShipState.NotReady);

        (await IsUnblockedAsync(dependent, blocker)).Should().BeTrue(
            "requiring Shipped from a milestone that can never ship would deadlock the roadmap");
    }

    [Fact]
    public async Task Releases_WhenTheBlockerWasCancelled()
    {
        var blocker = Milestone(MilestoneStatus.Cancelled, MilestoneShipState.NotReady);
        var dependent = Milestone(MilestoneStatus.Planned, MilestoneShipState.NotReady);

        (await IsUnblockedAsync(dependent, blocker)).Should().BeTrue(
            "the work is not coming, and blocking its dependents forever is not a plan");
    }

    [Fact]
    public async Task Blocks_UntilEveryBlockerIsSatisfied()
    {
        var shipped = Milestone(MilestoneStatus.Completed, MilestoneShipState.Shipped);
        var pending = Milestone(MilestoneStatus.Completed, MilestoneShipState.Pending);
        var dependent = Milestone(MilestoneStatus.Planned, MilestoneShipState.NotReady);

        (await IsUnblockedAsync(dependent, shipped)).Should().BeTrue();
        (await IsUnblockedAsync(dependent, pending)).Should().BeFalse(
            "one unsatisfied edge is enough to hold the milestone");
    }

    [Fact]
    public async Task MilestoneWithNoEdges_IsAlwaysReady()
    {
        // Position 2 sits after position 1 in the roadmap and is still free to run: order is a
        // preference, the edges are the constraint.
        _ = Milestone(MilestoneStatus.Planned, MilestoneShipState.NotReady);
        var later = Milestone(MilestoneStatus.Planned, MilestoneShipState.NotReady);
        await _db.SaveChangesAsync();

        (await _db.Milestones.Where(m => m.Id == later.Id)
            .Where(MilestoneDependencyQueries.IsUnblocked).AnyAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task BacklogTask_WithNoMilestone_IsNeverGated()
    {
        var task = TaskIn(null);
        await _db.SaveChangesAsync();

        (await _db.Tasks.Where(t => t.Id == task.Id)
            .Where(MilestoneDependencyQueries.MilestoneUnblocked).AnyAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task TaskGate_FollowsItsMilestone()
    {
        var blocker = Milestone(MilestoneStatus.Completed, MilestoneShipState.Pending);
        var dependent = Milestone(MilestoneStatus.Planned, MilestoneShipState.NotReady);
        var task = TaskIn(dependent);
        await IsUnblockedAsync(dependent, blocker);

        (await _db.Tasks.Where(t => t.Id == task.Id)
            .Where(MilestoneDependencyQueries.MilestoneUnblocked).AnyAsync())
            .Should().BeFalse("the task inherits the gate on the milestone it belongs to");
    }

    /// <summary>
    /// The gate exists three times — once as <c>Satisfied</c> over a milestone, and twice
    /// inlined (into <c>IsUnblocked</c> and <c>MilestoneUnblocked</c>) because EF cannot
    /// translate an invoked expression inside <c>Any</c>. Copies drift; this walks the whole
    /// state matrix so they cannot.
    /// </summary>
    [Fact]
    public async Task AllThreeFormsOfTheGate_AgreeOnEveryState()
    {
        foreach (var status in Enum.GetValues<MilestoneStatus>())
        {
            foreach (var shipState in Enum.GetValues<MilestoneShipState>())
            {
                using var db = new TasksDbContext(
                    new DbContextOptionsBuilder<TasksDbContext>()
                        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
                    currentUser: null, eventBus: null);

                var blocker = new Milestone
                {
                    Id = Ulid.NewUlid(),
                    OrgId = _orgId,
                    ProjectId = _projectId,
                    Title = "Blocker",
                    Position = 1,
                    Status = status,
                    ShipState = shipState,
                };
                var dependent = new Milestone
                {
                    Id = Ulid.NewUlid(),
                    OrgId = _orgId,
                    ProjectId = _projectId,
                    Title = "Dependent",
                    Position = 2,
                };
                var task = new TaskEntity
                {
                    Id = Ulid.NewUlid(),
                    OrgId = _orgId,
                    ProjectId = _projectId,
                    Number = 1,
                    Title = "Task",
                    MilestoneId = dependent.Id,
                    Milestone = dependent,
                };

                db.Milestones.AddRange(blocker, dependent);
                db.Tasks.Add(task);
                db.MilestoneDependencies.Add(new MilestoneDependency
                {
                    Id = Ulid.NewUlid(),
                    OrgId = _orgId,
                    MilestoneId = dependent.Id,
                    DependsOnMilestoneId = blocker.Id,
                    Source = DependencySource.Ai,
                });
                await db.SaveChangesAsync();

                var satisfied = MilestoneDependencyQueries.SatisfiedFn(blocker);

                var milestoneForm = await db.Milestones.Where(m => m.Id == dependent.Id)
                    .Where(MilestoneDependencyQueries.IsUnblocked).AnyAsync();

                var taskForm = await db.Tasks.Where(t => t.Id == task.Id)
                    .Where(MilestoneDependencyQueries.MilestoneUnblocked).AnyAsync();

                milestoneForm.Should().Be(satisfied,
                    $"IsUnblocked must match Satisfied for {status}/{shipState}");
                taskForm.Should().Be(satisfied,
                    $"MilestoneUnblocked must match Satisfied for {status}/{shipState}");
            }
        }
    }
}
