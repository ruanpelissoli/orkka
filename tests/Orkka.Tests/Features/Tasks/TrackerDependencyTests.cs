namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Milestones;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Tasks.Infrastructure.Services;
using Orkka.Modules.Tasks.PublicApi;

/// <summary>
/// Tracker dependency links as task dependencies (<see cref="DependencySource.Tracker"/>): a
/// blocked Story waits from its first member, a Story blocker counts by its last; links inside
/// one story and links that would close a cycle are not edges; a link that went away takes its
/// edge with it while AI and manual edges stay; a blocker Orkka does not work is a note only.
/// </summary>
public sealed class TrackerDependencyTests : IDisposable
{
    private static readonly Ulid OrgId = Ulid.NewUlid();
    private static readonly Ulid ProjectId = Ulid.NewUlid();

    private readonly TasksDbContext _db;
    private readonly TasksApi _api;
    private int _nextNumber = 1;

    public TrackerDependencyTests()
    {
        var options = new DbContextOptionsBuilder<TasksDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new TasksDbContext(options, currentUser: null, eventBus: null);
        var numbers = new Mock<ITaskNumberService>();
        numbers.Setup(n => n.ReserveRangeAsync(ProjectId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Ulid _, int count, CancellationToken _) =>
            {
                _nextNumber += count;
                return _nextNumber - 1;
            });
        _api = new TasksApi(
            _db,
            numbers.Object,
            Mock.Of<IProjectsApi>(),
            Mock.Of<ITaskNotifier>(),
            Mock.Of<Orkka.SharedKernel.Events.IEventBus>(),
            new TaskSubmissionService(
                _db,
                Mock.Of<ITaskNotifier>(),
                new MilestoneProgressUpdater(
                    _db, Mock.Of<ITaskNotifier>(), Mock.Of<Orkka.SharedKernel.Events.IEventBus>(),
                    NullLogger<MilestoneProgressUpdater>.Instance),
                Mock.Of<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                NullLogger<TaskSubmissionService>.Instance),
            NullLogger<TasksApi>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private async Task<Ulid> TaskAsync(string key)
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = OrgId,
            ProjectId = ProjectId,
            Number = _nextNumber++,
            Title = $"[{key}] {key}",
            ExternalKey = key,
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        return task.Id;
    }

    private async Task<TrackerStoryResult> StoryAsync(string key, params string[] subTasks)
        => await _api.CreateTrackerStoryAsync(new TrackerStorySpec(
            OrgId, ProjectId, "user_dana", key, $"https://acme.atlassian.net/browse/{key}",
            $"[{key}] Story {key}", "The story.", $"feature/{key}",
            [.. subTasks.Select(s => new StorySubTaskSpec(s, $"https://acme.atlassian.net/browse/{s}",
                $"jira:acme.atlassian.net:{s}", $"[{s}] Do {s}", "Body", TaskType.Feature, Priority.Medium, [], []))]));

    private static TrackerBlockEnd Plain(Ulid task) => new(task, null);
    private static TrackerBlockEnd Story(TrackerStoryResult story) => new(null, story.GroupId);

    private Task<List<TaskDependency>> TrackerEdgesAsync()
        => _db.TaskDependencies.AsNoTracking().Where(d => d.Source == DependencySource.Tracker).ToListAsync();

    private static Dictionary<Ulid, IReadOnlyList<string>> NoNotes() => [];

    [Fact]
    public async Task APlainTaskBlockedByAnother_WaitsForIt()
    {
        var blocked = await TaskAsync("SHOP-2");
        var blocker = await TaskAsync("SHOP-1");

        await _api.ReconcileTrackerDependenciesAsync(ProjectId, [new(Plain(blocked), Plain(blocker), "SHOP-1")], NoNotes());

        var edge = (await TrackerEdgesAsync()).Single();
        edge.TaskId.Should().Be(blocked);
        edge.DependsOnTaskId.Should().Be(blocker);
        edge.Reason.Should().Contain("SHOP-1");
    }

    [Fact]
    public async Task AStoryBlockedByAStory_StartsAfterTheBlockersLastMember()
    {
        var first = await StoryAsync("SHOP-10", "SHOP-11", "SHOP-12");
        var second = await StoryAsync("SHOP-20", "SHOP-21", "SHOP-22");

        await _api.ReconcileTrackerDependenciesAsync(ProjectId, [new(Story(second), Story(first), "SHOP-10")], NoNotes());

        var edge = (await TrackerEdgesAsync()).Single();
        edge.TaskId.Should().Be(second.Members.Single(m => m.SubTaskKey == "SHOP-21").TaskId, "the blocked story starts with its first member");
        edge.DependsOnTaskId.Should().Be(first.Members.Single(m => m.SubTaskKey == "SHOP-12").TaskId,
            "the blocker is done when its last member is — it merges with the story's pull request");
    }

    [Fact]
    public async Task ALinkInsideOneStory_IsLeftToTheStoryOrder()
    {
        var story = await StoryAsync("SHOP-10", "SHOP-11", "SHOP-12");
        var first = story.Members[0].TaskId;
        var second = story.Members[1].TaskId;

        await _api.ReconcileTrackerDependenciesAsync(ProjectId,
            [new(new TrackerBlockEnd(first, story.GroupId), new TrackerBlockEnd(second, story.GroupId), "SHOP-12")], NoNotes());

        (await TrackerEdgesAsync()).Should().BeEmpty("the members already chain in tracker order");
    }

    [Fact]
    public async Task ALinkThatWouldCloseACycle_BecomesANote()
    {
        var a = await TaskAsync("SHOP-1");
        var b = await TaskAsync("SHOP-2");
        _db.TaskDependencies.Add(new TaskDependency
        {
            Id = Ulid.NewUlid(), OrgId = OrgId, TaskId = b, DependsOnTaskId = a, Source = DependencySource.Manual,
        });
        await _db.SaveChangesAsync();

        await _api.ReconcileTrackerDependenciesAsync(ProjectId, [new(Plain(a), Plain(b), "SHOP-2")], NoNotes());

        (await TrackerEdgesAsync()).Should().BeEmpty("waiting would deadlock both");
        (await _db.Tasks.AsNoTracking().SingleAsync(t => t.Id == a)).ExternalBlockedBy.Should().Be("SHOP-2 (circular)");
    }

    [Fact]
    public async Task ALinkThatWentAway_TakesItsEdge_ButNeverAnAiOrManualOne()
    {
        var blocked = await TaskAsync("SHOP-2");
        var blocker = await TaskAsync("SHOP-1");
        var other = await TaskAsync("SHOP-3");
        _db.TaskDependencies.Add(new TaskDependency
        {
            Id = Ulid.NewUlid(), OrgId = OrgId, TaskId = blocked, DependsOnTaskId = other, Source = DependencySource.Ai,
        });
        await _db.SaveChangesAsync();
        await _api.ReconcileTrackerDependenciesAsync(ProjectId, [new(Plain(blocked), Plain(blocker), "SHOP-1")], NoNotes());

        await _api.ReconcileTrackerDependenciesAsync(ProjectId, [], NoNotes());

        (await TrackerEdgesAsync()).Should().BeEmpty();
        (await _db.TaskDependencies.AsNoTracking().SingleAsync()).Source.Should().Be(DependencySource.Ai);
    }

    [Fact]
    public async Task ABlockerOrkkaDoesNotWork_IsANoteThatClearsWhenTheLinkGoes()
    {
        var task = await TaskAsync("SHOP-2");

        await _api.ReconcileTrackerDependenciesAsync(ProjectId, [],
            new Dictionary<Ulid, IReadOnlyList<string>> { [task] = ["OPS-9", "OPS-12"] });

        (await _db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task)).ExternalBlockedBy.Should().Be("OPS-9, OPS-12");
        (await _db.TaskDependencies.AsNoTracking().AnyAsync()).Should().BeFalse("nothing waits for a ticket Orkka does not work");

        await _api.ReconcileTrackerDependenciesAsync(ProjectId, [],
            new Dictionary<Ulid, IReadOnlyList<string>> { [task] = [] });

        (await _db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task)).ExternalBlockedBy.Should().BeNull();
    }
}
