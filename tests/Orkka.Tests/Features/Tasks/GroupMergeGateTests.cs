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
/// The all-or-nothing gate: a cross-repo group's pull requests land together or not at all.
///
/// It exists because merging the API half and leaving the UI half open ships exactly the
/// half-migrated production the group was created to avoid. What it cannot do is make the merges
/// atomic — two forges have no shared transaction, and a merged PR cannot be un-merged — so
/// PartiallyMerged is a state the model has to be able to represent honestly rather than an
/// error to be swallowed.
/// </summary>
public sealed class GroupMergeGateTests : IDisposable
{
    private readonly TasksDbContext _db;
    private readonly TasksApi _api;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _projectId = Ulid.NewUlid();
    private int _number;

    public GroupMergeGateTests()
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

    private async Task<TaskGroup> SeedGroupAsync(
        TaskGroupMergePolicy policy = TaskGroupMergePolicy.Gated)
    {
        var group = new TaskGroup
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = _projectId,
            Title = "Add avatar upload",
            MergePolicy = policy,
            Status = TaskGroupStatus.Active,
        };
        _db.TaskGroups.Add(group);
        await _db.SaveChangesAsync();
        return group;
    }

    private async Task<TaskEntity> SeedMemberAsync(
        Ulid groupId,
        TaskStatus status = TaskStatus.Completed,
        Lane lane = Lane.Done,
        bool isMerged = false,
        string? prUrl = "https://example.test/pr/1",
        DateTimeOffset? prClosedAt = null,
        string? mergeFailureReason = null,
        MergeFailureKind? mergeFailureKind = null)
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = _projectId,
            GroupId = groupId,
            RepoId = Ulid.NewUlid(),
            Number = ++_number,
            Title = $"Half {_number}",
            Status = status,
            CurrentLane = lane,
            IsMerged = isMerged,
            PrUrl = prUrl,
            PrClosedAt = prClosedAt,
            MergeFailureReason = mergeFailureReason,
            MergeFailureKind = mergeFailureKind,
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        return task;
    }

    private async Task AddEdgeAsync(TaskEntity dependent, TaskEntity blocker)
    {
        _db.TaskDependencies.Add(new TaskDependency
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            TaskId = dependent.Id,
            DependsOnTaskId = blocker.Id,
            Source = DependencySource.Manual,
        });
        await _db.SaveChangesAsync();
    }

    private async Task<TaskGroupMergeState> StateOfAsync(Ulid groupId) =>
        (await _api.GetGroupMergeStatesAsync([groupId])).Single();

    [Fact]
    public async Task Group_IsNotReady_WhileAnyHalfIsStillBeingWritten()
    {
        var group = await SeedGroupAsync();
        await SeedMemberAsync(group.Id);
        await SeedMemberAsync(group.Id, TaskStatus.Active, Lane.Developing, prUrl: null);

        var state = await StateOfAsync(group.Id);

        state.Ready.Should().BeFalse();
        // The reason is shown in the UI — "waiting" with no subject is not actionable.
        state.BlockedBy.Should().Contain("Developing");
    }

    [Fact]
    public async Task Group_IsReady_WhenEveryHalfIsDoneWithAnOpenPr()
    {
        var group = await SeedGroupAsync();
        await SeedMemberAsync(group.Id);
        await SeedMemberAsync(group.Id);

        var state = await StateOfAsync(group.Id);

        state.Ready.Should().BeTrue();
        state.BlockedBy.Should().BeNull();
    }

    [Fact]
    public async Task Group_IsReady_WhenOneHalfAlreadyMerged()
    {
        // Someone hit merge on the forge directly. The remaining half must still land.
        var group = await SeedGroupAsync();
        await SeedMemberAsync(group.Id, isMerged: true);
        await SeedMemberAsync(group.Id);

        (await StateOfAsync(group.Id)).Ready.Should().BeTrue();
    }

    [Fact]
    public async Task Group_IsNotReady_WhenAHalfsPrWasClosedWithoutMerging()
    {
        var group = await SeedGroupAsync();
        await SeedMemberAsync(group.Id);
        await SeedMemberAsync(group.Id, prClosedAt: DateTimeOffset.UtcNow);

        var state = await StateOfAsync(group.Id);

        // A closed PR never lands. Shipping the siblings anyway is the half-migrated production
        // the gate exists to prevent.
        state.Ready.Should().BeFalse();
        state.BlockedBy.Should().Contain("Cannot merge");
    }

    [Fact]
    public async Task Group_IsNotReady_WhenAHalfHasARecordedMergeFailure()
    {
        var group = await SeedGroupAsync();
        await SeedMemberAsync(group.Id);
        await SeedMemberAsync(group.Id, mergeFailureReason: "Required checks failed",
            mergeFailureKind: MergeFailureKind.ForgeRejected);

        (await StateOfAsync(group.Id)).Ready.Should().BeFalse();
    }

    /// <summary>Only a TERMINAL failure holds the group. The retry path — a conflict the
    /// resolver is about to fix, "runner not connected" at one attempt — is the sweep's to try
    /// again, and holding the group for it kept a member from a merge that would have succeeded
    /// on the next cycle (prod, 2026-09-16).</summary>
    [Fact]
    public async Task Group_StaysReady_WhenAHalfsFailureIsOnTheRetryPath()
    {
        var group = await SeedGroupAsync();
        await SeedMemberAsync(group.Id);
        await SeedMemberAsync(group.Id, mergeFailureReason: "Your runner is not connected.",
            mergeFailureKind: MergeFailureKind.EnvironmentBlocked);
        await SeedMemberAsync(group.Id, mergeFailureReason: "Merge conflicts — attempting automatic resolution");

        (await StateOfAsync(group.Id)).Ready.Should().BeTrue();
    }

    [Fact]
    public async Task MergeOrder_PutsTheProviderBeforeTheCaller()
    {
        var group = await SeedGroupAsync();
        var provider = await SeedMemberAsync(group.Id);
        var consumer = await SeedMemberAsync(group.Id);
        await AddEdgeAsync(consumer, provider);

        var state = await StateOfAsync(group.Id);

        state.MergeOrder.Should().Equal([provider.Id, consumer.Id]);
    }

    [Fact]
    public async Task MergeOrder_IsStable_WhenNothingOrdersTheMembers()
    {
        var group = await SeedGroupAsync();
        await SeedMemberAsync(group.Id);
        await SeedMemberAsync(group.Id);

        var first = await StateOfAsync(group.Id);
        var second = await StateOfAsync(group.Id);

        // An order that shuffles between cycles would make the merge sequence unreproducible.
        first.MergeOrder.Should().Equal(second.MergeOrder);
    }

    [Fact]
    public async Task RefreshStatus_MarksTheGroupMerged_OnceEveryHalfHas()
    {
        var group = await SeedGroupAsync();
        await SeedMemberAsync(group.Id, isMerged: true);
        await SeedMemberAsync(group.Id, isMerged: true);

        await _api.RefreshGroupMergeStatusAsync(group.Id);

        var reloaded = await _db.TaskGroups.AsNoTracking().FirstAsync(g => g.Id == group.Id);
        reloaded.Status.Should().Be(TaskGroupStatus.Merged);
        reloaded.SettledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RefreshStatus_MarksPartiallyMerged_WhenOneLandedAndAnotherCannot()
    {
        var group = await SeedGroupAsync();
        await SeedMemberAsync(group.Id, isMerged: true);
        await SeedMemberAsync(group.Id, mergeFailureReason: "Required checks failed",
            mergeFailureKind: MergeFailureKind.ForgeRejected);

        await _api.RefreshGroupMergeStatusAsync(group.Id);

        // The honest name for a genuinely reachable state. Calling it Merged would hide a broken
        // deploy; calling it Active would re-attempt a merge that already happened.
        (await _db.TaskGroups.AsNoTracking().FirstAsync(g => g.Id == group.Id)).Status
            .Should().Be(TaskGroupStatus.PartiallyMerged);
    }

    [Fact]
    public async Task RefreshStatus_HoldsAtAwaitingMerge_MidSequence()
    {
        var group = await SeedGroupAsync();
        await SeedMemberAsync(group.Id, isMerged: true);
        await SeedMemberAsync(group.Id);

        await _api.RefreshGroupMergeStatusAsync(group.Id);

        // One down, one to go and nothing wrong with it — not a partial failure.
        var reloaded = await _db.TaskGroups.AsNoTracking().FirstAsync(g => g.Id == group.Id);
        reloaded.Status.Should().Be(TaskGroupStatus.AwaitingMerge);
        reloaded.SettledAt.Should().BeNull();
    }

    [Fact]
    public async Task IndependentPolicy_IsReportedSoThePollerDoesNotGate()
    {
        var group = await SeedGroupAsync(TaskGroupMergePolicy.Independent);
        await SeedMemberAsync(group.Id);
        await SeedMemberAsync(group.Id, TaskStatus.Active, Lane.Developing, prUrl: null);

        var state = await StateOfAsync(group.Id);

        // Not ready, but the policy is what the poller keys on — under Independent it merges
        // each half as it is approved, exactly as an ungrouped task does.
        state.MergePolicy.Should().Be(TaskGroupMergePolicy.Independent);
        state.Ready.Should().BeFalse();
    }
}
