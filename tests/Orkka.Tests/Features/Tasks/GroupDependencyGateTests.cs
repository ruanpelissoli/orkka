namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The deadlock guard for cross-repo groups.
///
/// An all-or-nothing merge gate and an edge that only clears on merge cannot both hold:
/// the consumer waits for the provider to merge, the provider waits for the gate, and the
/// gate waits for the consumer to finish. Nothing in the group ever runs.
///
/// The escape is that siblings do not code against each other's merged source — they code
/// against the group's contract, written before either of them starts — so a sibling edge
/// clears at Completed. These tests pin that this relaxation applies to sibling edges and
/// to nothing else; a regression here silently reintroduces the deadlock, or silently lets
/// unrelated tasks build against code that was never merged.
/// </summary>
public sealed class GroupDependencyGateTests : IDisposable
{
    private readonly TasksDbContext _db;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _projectId = Ulid.NewUlid();
    private int _number;

    public GroupDependencyGateTests()
    {
        var options = new DbContextOptionsBuilder<TasksDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new TasksDbContext(options, currentUser: null, eventBus: null);
    }

    public void Dispose() => _db.Dispose();

    private TaskEntity Task(TaskStatus status, bool isMerged, Ulid? groupId = null)
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = _projectId,
            Number = ++_number,
            Title = $"Task {_number}",
            Status = status,
            IsMerged = isMerged,
            GroupId = groupId,
        };
        _db.Tasks.Add(task);
        return task;
    }

    /// <summary>Adds "task depends on blocker" and returns whether the edge reads as unmet.</summary>
    private async Task<bool> IsUnmetAsync(TaskEntity task, TaskEntity blocker)
    {
        _db.TaskDependencies.Add(new TaskDependency
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            TaskId = task.Id,
            DependsOnTaskId = blocker.Id,
            Source = DependencySource.Ai,
        });
        await _db.SaveChangesAsync();

        return await _db.TaskDependencies
            .Where(d => d.TaskId == task.Id)
            .Where(TaskDependencyQueries.IsUnmet)
            .AnyAsync();
    }

    [Fact]
    public async Task SiblingEdge_ClearsAtCompleted_WithoutWaitingForTheMerge()
    {
        var groupId = Ulid.NewUlid();
        var provider = Task(TaskStatus.Completed, isMerged: false, groupId);
        var consumer = Task(TaskStatus.Active, isMerged: false, groupId);

        // The whole point: without this the gate deadlocks.
        (await IsUnmetAsync(consumer, provider)).Should().BeFalse();
    }

    [Fact]
    public async Task SiblingEdge_StillBlocks_WhileTheProviderIsUnfinished()
    {
        var groupId = Ulid.NewUlid();
        var provider = Task(TaskStatus.Active, isMerged: false, groupId);
        var consumer = Task(TaskStatus.Active, isMerged: false, groupId);

        // Ordering inside a group is relaxed, not removed — the provider's PR must exist first.
        (await IsUnmetAsync(consumer, provider)).Should().BeTrue();
    }

    [Fact]
    public async Task UngroupedEdge_StillRequiresTheMerge()
    {
        var provider = Task(TaskStatus.Completed, isMerged: false);
        var consumer = Task(TaskStatus.Active, isMerged: false);

        (await IsUnmetAsync(consumer, provider)).Should().BeTrue();
    }

    [Fact]
    public async Task EdgeBetweenDifferentGroups_StillRequiresTheMerge()
    {
        var provider = Task(TaskStatus.Completed, isMerged: false, Ulid.NewUlid());
        var consumer = Task(TaskStatus.Active, isMerged: false, Ulid.NewUlid());

        (await IsUnmetAsync(consumer, provider)).Should().BeTrue();
    }

    [Fact]
    public async Task GroupedConsumer_DependingOnAnUngroupedTask_StillRequiresTheMerge()
    {
        // Only the shared contract makes starting early safe, and a task outside the group
        // never wrote one. Half-membership must not leak the relaxation.
        var provider = Task(TaskStatus.Completed, isMerged: false);
        var consumer = Task(TaskStatus.Active, isMerged: false, Ulid.NewUlid());

        (await IsUnmetAsync(consumer, provider)).Should().BeTrue();
    }

    [Fact]
    public async Task MergedBlocker_ClearsTheEdge_GroupedOrNot()
    {
        var provider = Task(TaskStatus.Completed, isMerged: true);
        var consumer = Task(TaskStatus.Active, isMerged: false);

        (await IsUnmetAsync(consumer, provider)).Should().BeFalse();
    }

    [Fact]
    public void IsUnmetItem_MatchesTheQuery_ForEachCase()
    {
        // The DTO twin drives the "blocked by" badge. Drift between the two shows a satisfied
        // edge as still blocking, which is how a group looks stuck when it is running fine.
        static TaskDependencyItem Item(TaskStatus status, bool isMerged, bool sameGroup) =>
            new(Ulid.NewUlid(), 1, "P-1", "t", status.ToString(), isMerged, sameGroup, null, "Ai");

        TaskDependencyQueries.IsUnmetItem(Item(TaskStatus.Completed, false, sameGroup: true)).Should().BeFalse();
        TaskDependencyQueries.IsUnmetItem(Item(TaskStatus.Active, false, sameGroup: true)).Should().BeTrue();
        TaskDependencyQueries.IsUnmetItem(Item(TaskStatus.Completed, false, sameGroup: false)).Should().BeTrue();
        TaskDependencyQueries.IsUnmetItem(Item(TaskStatus.Completed, true, sameGroup: false)).Should().BeFalse();
    }
}
