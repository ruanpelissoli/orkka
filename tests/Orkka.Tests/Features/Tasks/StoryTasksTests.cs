namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Milestones;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Tasks.Infrastructure.Services;
using Orkka.Modules.Tasks.PublicApi;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// A tracker Story in the Tasks module: the group and its members in tracker order on one shared
/// branch, each depending on the one before; a sub-task in two repositories keeps strict order;
/// late sub-tasks join the end (un-readying a ready story, refused once its pull request is
/// done); an archived member hands its place in the chain to its predecessor; an archived
/// blocker counts as met; members never plan ahead of a milestone gate.
/// </summary>
public sealed class StoryTasksTests : IDisposable
{
    private static readonly Ulid OrgId = Ulid.NewUlid();
    private static readonly Ulid ProjectId = Ulid.NewUlid();
    private static readonly Ulid Api = Ulid.NewUlid();
    private static readonly Ulid Web = Ulid.NewUlid();
    private const string Branch = "feature/SHOP-10-checkout-v2";

    private readonly TasksDbContext _db;
    private readonly TasksApi _api;
    private int _nextNumber = 1;
    private readonly Mock<ITaskNumberService> _numbers = new();

    public StoryTasksTests()
    {
        var options = new DbContextOptionsBuilder<TasksDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new TasksDbContext(options, currentUser: null, eventBus: null);
        var numbers = _numbers;
        numbers.Setup(n => n.ReserveRangeAsync(ProjectId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            // The real contract (ProjectsApi.ReserveTaskNumbersAsync): the LAST number of the range.
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

    private static StorySubTaskSpec Sub(string key, params Ulid[] repos)
        => new(key, $"https://acme.atlassian.net/browse/{key}", $"jira:acme.atlassian.net:{key}",
            $"[{key}] Do {key}", $"Body of {key}", TaskType.Feature, Priority.Medium, [], repos);

    private Task<TrackerStoryResult> CreateStoryAsync(params StorySubTaskSpec[] subs)
        => _api.CreateTrackerStoryAsync(new TrackerStorySpec(
            OrgId, ProjectId, "user_dana", "SHOP-10", "https://acme.atlassian.net/browse/SHOP-10",
            "[SHOP-10] Checkout v2", "The whole story.", Branch, subs));

    private async Task<List<TaskEntity>> MembersAsync()
        => await _db.Tasks.Include(t => t.Dependencies).OrderBy(t => t.StoryOrder).ToListAsync();

    [Fact]
    public async Task Intake_CreatesTheStory_MembersInOrder_OnTheSharedBranch_EachDependingOnThePrevious()
    {
        var result = await CreateStoryAsync(Sub("SHOP-11", Api), Sub("SHOP-12", Api), Sub("SHOP-13", Api));

        result.Created.Should().BeTrue();
        var group = await _db.TaskGroups.Include(g => g.Branches).SingleAsync();
        group.Kind.Should().Be(TaskGroupKind.Story);
        group.BranchName.Should().Be(Branch);
        group.ExternalKey.Should().Be("SHOP-10");
        group.MergePolicy.Should().Be(TaskGroupMergePolicy.Independent, "members land one by one on the shared branch");
        group.Branches.Should().ContainSingle().Which.RepoId.Should().Be(Api);

        var members = await MembersAsync();
        members.Select(m => m.ExternalKey).Should().Equal("SHOP-11", "SHOP-12", "SHOP-13");
        members.Select(m => m.StoryOrder).Should().Equal(1, 2, 3);
        members.Should().OnlyContain(m => m.BranchName == Branch && m.GroupId == group.Id && m.RunnerUserId == "user_dana");
        members[0].Dependencies.Should().BeEmpty();
        members[1].Dependencies.Should().ContainSingle().Which.DependsOnTaskId.Should().Be(members[0].Id);
        members[2].Dependencies.Should().ContainSingle().Which.DependsOnTaskId.Should().Be(members[1].Id);

        var again = await CreateStoryAsync(Sub("SHOP-11", Api), Sub("SHOP-12", Api), Sub("SHOP-13", Api));
        again.Created.Should().BeFalse("the Story's key is its idempotency key");
        again.GroupId.Should().Be(group.Id);
        (await _db.Tasks.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task ASubTaskInTwoRepositories_IsOneMemberPerRepository_InStrictGlobalOrder_WithARowPerRepository()
    {
        await CreateStoryAsync(Sub("SHOP-11", Api, Web), Sub("SHOP-12", Web));

        var members = await MembersAsync();
        members.Select(m => (m.ExternalKey, m.RepoId, m.StoryOrder)).Should().Equal(
            ("SHOP-11", (Ulid?)Api, (int?)1), ("SHOP-11", Web, 2), ("SHOP-12", Web, 3));
        members[1].ExternalRef.Should().Be($"jira:acme.atlassian.net:SHOP-11@{Web}");
        members[2].Dependencies.Single().DependsOnTaskId.Should().Be(members[1].Id);
        (await _db.GroupBranches.Select(b => b.RepoId).ToListAsync()).Should().BeEquivalentTo([Api, Web]);
        (await _db.GroupBranches.ToListAsync()).Should().OnlyContain(b => b.BranchName == Branch);
    }

    [Fact]
    public async Task ALateSubTask_BeforeReady_JoinsTheEnd()
    {
        var story = await CreateStoryAsync(Sub("SHOP-11", Api));

        var appended = await _api.AppendStoryMemberAsync(story.GroupId, Sub("SHOP-14", Api));

        appended.Closed.Should().BeFalse();
        appended.WasReady.Should().BeFalse();
        var members = await MembersAsync();
        members.Select(m => m.ExternalKey).Should().Equal("SHOP-11", "SHOP-14");
        members[1].Dependencies.Single().DependsOnTaskId.Should().Be(members[0].Id);
        members[1].BranchName.Should().Be(Branch);
    }

    [Fact]
    public async Task ALateSubTask_AfterReady_ClearsReady_AndAsksForTheDraftBack()
    {
        var story = await CreateStoryAsync(Sub("SHOP-11", Api));
        await _api.SetGroupBranchPullRequestAsync(story.GroupId, Api, Branch, "https://github.com/acme/api/pull/7", draft: true);
        await _api.SetStoryReadyAsync(story.GroupId, DateTimeOffset.UtcNow);
        (await _db.GroupBranches.SingleAsync()).ForgePrDraft.Should().BeFalse();

        var appended = await _api.AppendStoryMemberAsync(story.GroupId, Sub("SHOP-14", Api));

        appended.WasReady.Should().BeTrue();
        var group = await _db.TaskGroups.Include(g => g.Branches).SingleAsync();
        group.ReadyAt.Should().BeNull();
        group.Branches.Single().ForgePrDraft.Should().BeTrue();
        group.Branches.Single().ReadyAt.Should().BeNull();
    }

    /// <summary>A mixed story (a linked folder plus a forge repository) readied once: the folder
    /// got the shared branch squash-merged, the forge pull request is ready. A late sub-task in
    /// the folder joins — the forge pull request goes back to draft and the folder is open again
    /// for the final squash, which a late member in the forge repository leaves alone.</summary>
    [Fact]
    public async Task ALateSubTask_InAMixedFolderAndForgeStory_AfterReady_ReopensOnlyItsOwnSquashedFolder()
    {
        var folder = Ulid.NewUlid();
        var story = await CreateStoryAsync(Sub("SHOP-11", folder), Sub("SHOP-12", Api));
        await _api.SetGroupBranchPullRequestAsync(story.GroupId, Api, Branch, "https://github.com/acme/api/pull/7", draft: true);
        await _api.SetGroupBranchForgeStateAsync(story.GroupId, folder, "merged");
        await _api.SetStoryReadyAsync(story.GroupId, DateTimeOffset.UtcNow);

        var intoForge = await _api.AppendStoryMemberAsync(story.GroupId, Sub("SHOP-13", Api));
        intoForge.Closed.Should().BeFalse("the forge pull request is still open");
        intoForge.WasReady.Should().BeTrue();
        (await _db.GroupBranches.SingleAsync(b => b.RepoId == folder)).ForgeState.Should().Be("merged",
            "a late member elsewhere brings the folder nothing new");

        var intoFolder = await _api.AppendStoryMemberAsync(story.GroupId, Sub("SHOP-14", folder));
        intoFolder.Closed.Should().BeFalse();
        var rows = await _db.GroupBranches.ToListAsync();
        rows.Single(b => b.RepoId == folder).ForgeState.Should().BeNull("the end of the story squashes the folder again");
        rows.Single(b => b.RepoId == Api).ForgePrDraft.Should().BeTrue();
        (await MembersAsync()).Select(m => m.ExternalKey).Should().Equal("SHOP-11", "SHOP-12", "SHOP-13", "SHOP-14");
        (await _api.GetTrackerGroupStatesAsync([story.GroupId])).Single().PullRequestFinished.Should().BeFalse();
    }

    /// <summary>The routing answer for a Story member naming two repositories: one member per
    /// repository, in story order, exactly like intake (gap: the second answer was dropped).</summary>
    [Fact]
    public async Task ARepoAnswerNamingTwoRepositories_ForAStoryMember_IsOneMemberPerRepository_InOrder()
    {
        var story = await CreateStoryAsync(Sub("SHOP-11"), Sub("SHOP-12", Api));
        var asking = (await MembersAsync())[0];
        asking.RepoId.Should().BeNull();

        var added = await StoryChain.SpanReposAsync(_db, _numbers.Object, asking, [Web, Api], default);
        await _db.SaveChangesAsync();

        added.Should().ContainSingle();
        var members = await MembersAsync();
        members.Select(m => (m.ExternalKey, m.RepoId, m.StoryOrder)).Should().Equal(
            ("SHOP-11", (Ulid?)Web, (int?)1), ("SHOP-11", Api, 2), ("SHOP-12", Api, 3));
        members[1].ExternalRef.Should().Be($"jira:acme.atlassian.net:SHOP-11@{Api}");
        members[1].BranchName.Should().Be(Branch);
        members[1].Dependencies.Single().DependsOnTaskId.Should().Be(members[0].Id);
        members[2].Dependencies.Single().DependsOnTaskId.Should().Be(members[1].Id, "the next sub-task now waits on the last new member");
        members.Select(m => m.Number).Should().OnlyHaveUniqueItems();
        (await _db.GroupBranches.Select(b => b.RepoId).ToListAsync()).Should().BeEquivalentTo([Api, Web]);
    }

    [Fact]
    public async Task ALateSubTask_AfterTheStoryPullRequestMerged_IsRefused()
    {
        var story = await CreateStoryAsync(Sub("SHOP-11", Api));
        await _api.SetGroupBranchPullRequestAsync(story.GroupId, Api, Branch, "https://github.com/acme/api/pull/7", draft: false);
        await _api.SetGroupBranchForgeStateAsync(story.GroupId, Api, "merged");

        var appended = await _api.AppendStoryMemberAsync(story.GroupId, Sub("SHOP-14", Api));

        appended.Closed.Should().BeTrue();
        (await _db.Tasks.CountAsync()).Should().Be(1);
        (await _api.GetTrackerGroupStatesAsync([story.GroupId])).Single().Merged.Should().BeTrue();
    }

    [Fact]
    public async Task ARetiredMember_HandsItsPlaceToItsPredecessor()
    {
        await CreateStoryAsync(Sub("SHOP-11", Api), Sub("SHOP-12", Api), Sub("SHOP-13", Api));
        var members = await MembersAsync();

        var outcome = await _api.RetireTrackerTaskAsync(members[1].Id, "SHOP-12 was closed.");

        outcome.Should().Be(TrackerTaskRetirement.Archived);
        var edges = await _db.TaskDependencies.ToListAsync();
        edges.Should().ContainSingle(e => e.TaskId == members[2].Id)
            .Which.DependsOnTaskId.Should().Be(members[0].Id, "the third sub-task now waits on the first");
        edges.Should().NotContain(e => e.DependsOnTaskId == members[1].Id);
    }

    [Fact]
    public void AnArchivedBlocker_CountsAsMet()
    {
        TaskDependencyQueries.IsUnmetItem(Item(nameof(TaskStatus.Archived), merged: false, sameGroup: false)).Should().BeFalse();
        TaskDependencyQueries.IsUnmetItem(Item(nameof(TaskStatus.Completed), merged: false, sameGroup: true)).Should().BeFalse(
            "a same-group (story) edge is met at Completed");
        TaskDependencyQueries.IsUnmetItem(Item(nameof(TaskStatus.Active), merged: false, sameGroup: true)).Should().BeTrue();
        TaskDependencyQueries.IsUnmetItem(Item(nameof(TaskStatus.Completed), merged: false, sameGroup: false)).Should().BeTrue();

        var unmet = TaskDependencyQueries.IsUnmet.Compile();
        var archived = new TaskEntity { Title = "a", Status = TaskStatus.Archived, IsArchived = true };
        unmet(new TaskDependency { Task = new TaskEntity { Title = "b" }, DependsOnTask = archived }).Should().BeFalse();

        static TaskDependencyItem Item(string status, bool merged, bool sameGroup)
            => new(Ulid.NewUlid(), 1, "SHOP-1", "t", status, merged, sameGroup, null, "Manual");
    }

    [Fact]
    public void AStoryMember_NeverPlansAheadOfAMilestoneGate()
    {
        var blocker = new Milestone { Title = "m1", Status = MilestoneStatus.Active };
        blocker.Tasks.Add(new TaskEntity { Title = "x", CurrentLane = Lane.Developing });
        var milestone = new Milestone { Title = "m2" };
        milestone.Dependencies.Add(new MilestoneDependency { DependsOnMilestone = blocker });
        var planAhead = MilestoneDependencyQueries.PlanAheadAcrossGate.Compile();

        planAhead(new TaskEntity { Title = "plain", Milestone = milestone }).Should().BeTrue();
        planAhead(new TaskEntity
        {
            Title = "member", Milestone = milestone, StoryOrder = 2,
            Group = new TaskGroup { Title = "story", Kind = TaskGroupKind.Story },
        }).Should().BeFalse();
        planAhead(new TaskEntity
        {
            Title = "half", Milestone = milestone,
            Group = new TaskGroup { Title = "cross", Kind = TaskGroupKind.CrossRepo },
        }).Should().BeTrue();
    }

    [Fact]
    public async Task ReviewBaseSha_IsWrittenOnce()
    {
        await CreateStoryAsync(Sub("SHOP-11", Api));
        var member = (await MembersAsync()).Single();

        await _api.SetTaskReviewBaseShaAsync(member.Id, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        await _api.SetTaskReviewBaseShaAsync(member.Id, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        (await _db.Tasks.AsNoTracking().SingleAsync()).ReviewBaseSha.Should().Be("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "a re-entered phase must not move the member's starting point onto its own commits");
    }

    [Fact]
    public async Task GetStory_CarriesOrderPlansAndChangedFiles()
    {
        var story = await CreateStoryAsync(Sub("SHOP-11", Api), Sub("SHOP-12", Api));
        var members = await MembersAsync();
        members[0].IsMerged = true;
        members[0].Status = TaskStatus.Completed;
        _db.TaskPlans.Add(new TaskPlan
        {
            OrgId = OrgId, TaskId = members[0].Id, Status = PlanStatus.Approved,
            Content = new PlanContent { Summary = "Add the cart endpoint", Approach = "x" },
        });
        _db.CodeChangeSnapshots.Add(new TaskCodeChangeSnapshot
        {
            OrgId = OrgId, TaskId = members[0].Id, RepoId = Api, Lane = Lane.CodeReview,
            FilesJson = """[{"path":"src/Cart.cs","status":"added","additions":40,"deletions":0}]""",
        });
        await _db.SaveChangesAsync();

        var context = (await _api.GetStoryForTaskAsync(members[1].Id))!;

        context.GroupId.Should().Be(story.GroupId);
        context.BranchName.Should().Be(Branch);
        context.Members.Select(m => m.StoryOrder).Should().Equal(1, 2);
        context.Members[0].PlanSummary.Should().Be("Add the cart endpoint");
        context.Members[0].Files.Should().ContainSingle().Which.Should().Be(new StoryChangedFile("src/Cart.cs", 40, 0));
        context.Members[0].RunnerUserId.Should().Be("user_dana");
    }
}
