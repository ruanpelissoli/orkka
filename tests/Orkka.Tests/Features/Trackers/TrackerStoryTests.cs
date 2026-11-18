namespace Orkka.Tests.Features.Trackers;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Trackers.Domain;
using Orkka.Modules.Trackers.Infrastructure;
using Orkka.Modules.Trackers.PublicApi;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// Stories at intake and in write-back: a Story assigned to me with open sub-tasks becomes a
/// story group (members in tracker order on the branch rendered for the Story's key); a Story
/// with nothing open is a task of its own; a sub-task whose Story is someone else's carries the
/// Story as parent context; sub-tasks added later join the end (a ready story turns back into a
/// draft), or are worked on their own once the story's pull request is done. In write-back the
/// Story and its sub-tasks follow the story: the furthest step their members reached, "PR
/// opened" only when the story is ready, "Merged" when every story pull request merged.
///
/// Only sub-tasks assigned to me and not under way are members: the rest are never imported,
/// never waited for, and a member that stops being mine leaves the story while nothing started.
/// </summary>
public sealed class TrackerStoryTests : IDisposable
{
    private static readonly Ulid OrgId = Ulid.NewUlid();
    private static readonly Ulid ProjectId = Ulid.NewUlid();
    private static readonly Ulid ShopRepo = Ulid.NewUlid();
    private const string UserId = "user_dana";
    private const string Instance = "runner-1";

    private readonly TrackersDbContext _db = new(
        new DbContextOptionsBuilder<TrackersDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
        currentUser: null);

    private readonly Mock<ITasksApi> _tasks = new(MockBehavior.Loose);
    private readonly Mock<IProjectsApi> _projects = new(MockBehavior.Loose);
    private readonly Mock<IStoryPullRequests> _storyPrs = new(MockBehavior.Loose);
    private readonly TrackersApi _api;
    private readonly TrackerConnection _connection;
    private readonly ProjectTrackerConfig _config;
    private readonly Ulid _groupId = Ulid.NewUlid();
    private TrackerStorySpec? _storySpec;
    private TrackerGroupState _group;

    public TrackerStoryTests()
    {
        _api = new TrackersApi(_db, _tasks.Object, _projects.Object, Mock.Of<IRepoRouter>(),
            NullLogger<TrackersApi>.Instance, _storyPrs.Object);
        _connection = new TrackerConnection
        {
            Id = Ulid.NewUlid(), OrgId = OrgId, UserId = UserId, ConnectionKey = "jira-acme",
            Kind = TrackerKinds.Jira, SiteUrl = "https://acme.atlassian.net", MachineName = "dana-laptop",
            Healthy = true, LastSeenAt = DateTimeOffset.UtcNow,
        };
        _config = new ProjectTrackerConfig
        {
            Id = Ulid.NewUlid(), OrgId = OrgId, ProjectId = ProjectId, OwnerUserId = UserId,
            TrackerConnectionId = _connection.Id, Scope = "SHOP", LeaseHolder = Instance,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            StatusMapJson = """{"planning":"In Progress","prOpened":"Code Review","merged":"Done"}""",
        };
        _db.Connections.Add(_connection);
        _db.ProjectConfigs.Add(_config);
        _db.SaveChanges();

        _projects.Setup(p => p.ListReposAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new RepoSummary(ShopRepo, ProjectId, OrgId, "shop", "git@github.com:acme/shop.git", "main", "#fff",
                WorkspaceStatus.Ready, ForgeKind: ForgeKind.GitHub)]);
        _projects.Setup(p => p.GetRepoAsync(ShopRepo, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepoSummary(ShopRepo, ProjectId, OrgId, "shop", "git@github.com:acme/shop.git", "main", "#fff",
                WorkspaceStatus.Ready, ForgeKind: ForgeKind.GitHub));
        _tasks.Setup(t => t.CreateTrackerStoryAsync(It.IsAny<TrackerStorySpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrackerStorySpec spec, CancellationToken _) =>
            {
                _storySpec = spec;
                return new TrackerStoryResult(_groupId,
                    [.. spec.SubTasks.Select((s, i) => new StoryMemberRef(Ulid.NewUlid(), s.Key, s.RepoIds.FirstOrDefault(), i + 1, true))],
                    true);
            });
        _tasks.Setup(t => t.GetTrackerTaskStatesAsync(It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _tasks.Setup(t => t.SubmitTaskAsync(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardOpOutcome(true));
        _group = new TrackerGroupState(_groupId, TaskGroupKind.Story, "SHOP-10", "feature/SHOP-10-checkout", null, false, 0, [], []);
        _tasks.Setup(t => t.GetTrackerGroupStatesAsync(It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [_group]);
    }

    public void Dispose() => _db.Dispose();

    private static TrackerTicketBody Body(string title) => new(
        title, "Story", "To Do", "todo", "Medium", $"{title} — details.", null, null, [], [], DateTimeOffset.UtcNow);

    private static TrackerTicket Story(params string[] children) => new(
        "SHOP-10", "10010", "https://acme.atlassian.net/browse/SHOP-10", null, "hs", Body("Checkout"), [],
        Status: "To Do", StatusCategory: "todo", AssignedToMe: true, InIntake: true, ChildKeys: children);

    // A sub-task is mine and not started unless a test says otherwise.
    private static TrackerTicket Child(string key, string category = "todo", bool assignedToMe = true, bool inIntake = false,
        string? status = null) => new(
        key, key, $"https://acme.atlassian.net/browse/{key}", null, $"h-{key}", Body($"Part {key}"), [],
        Status: status ?? category switch { "done" => "Done", "inprogress" => "In Progress", _ => "To Do" },
        StatusCategory: category, AssignedToMe: assignedToMe, InIntake: inIntake,
        ParentKey: "SHOP-10", ParentType: "Story");

    private Task<TrackerReportAck> ReportAsync(params TrackerTicket[] tickets) => _api.ReportAsync(OrgId, UserId,
        new TrackerTicketReport(_config.Id.ToString(), Instance, true, null, DateTimeOffset.UtcNow, tickets));

    private Task<List<TicketLink>> LinksAsync() => _db.TicketLinks.IgnoreQueryFilters().OrderBy(l => l.TicketKey).ToListAsync();

    // ── Intake ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AStoryAssignedToMe_WithOpenSubTasks_BecomesAStory_InTrackerOrder_OnTheStoryBranch()
    {
        // Children arrive before their parent and one is already done.
        (await ReportAsync(Child("SHOP-12"), Child("SHOP-13", "done"), Story("SHOP-11", "SHOP-12", "SHOP-13"), Child("SHOP-11")))
            .Accepted.Should().BeTrue();

        _storySpec.Should().NotBeNull();
        _storySpec!.StoryKey.Should().Be("SHOP-10");
        _storySpec.BranchName.Should().Be("feature/SHOP-10-checkout");
        _storySpec.SubTasks.Select(s => s.Key).Should().Equal("SHOP-11", "SHOP-12");
        _storySpec.SubTasks.Should().OnlyContain(s => s.RepoIds.SequenceEqual(new[] { ShopRepo }));
        _tasks.Verify(t => t.CreateTrackerTaskAsync(It.IsAny<TrackerTaskSpec>(), It.IsAny<CancellationToken>()), Times.Never);

        var links = await LinksAsync();
        links.Select(l => (l.TicketKey, l.Role)).Should().Equal(
            ("SHOP-10", TicketRole.Story), ("SHOP-11", TicketRole.SubTask), ("SHOP-12", TicketRole.SubTask));
        links.Should().OnlyContain(l => l.GroupId == _groupId);
        links[0].ChildKeysJson.Should().Contain("SHOP-13");
        _tasks.Verify(t => t.SubmitTaskAsync(OrgId, It.IsAny<Ulid>(), null, It.IsAny<CancellationToken>()), Times.Exactly(2));
        _tasks.Verify(t => t.SetStoryPendingChildrenAsync(_groupId, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StoryModeOff_TheStoryIsATaskOfItsOwn()
    {
        _config.StoryMode = 0;
        await _db.SaveChangesAsync();
        _tasks.Setup(t => t.CreateTrackerTaskAsync(It.IsAny<TrackerTaskSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalTaskResult(Ulid.NewUlid(), 1, "x", true));

        await ReportAsync(Story("SHOP-11"), Child("SHOP-11"));

        _tasks.Verify(t => t.CreateTrackerStoryAsync(It.IsAny<TrackerStorySpec>(), It.IsAny<CancellationToken>()), Times.Never);
        (await LinksAsync()).Should().ContainSingle().Which.Role.Should().Be(TicketRole.Standalone);
    }

    [Fact]
    public async Task AStoryWithNoOpenSubTasks_IsATaskOfItsOwn()
    {
        _tasks.Setup(t => t.CreateTrackerTaskAsync(It.IsAny<TrackerTaskSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalTaskResult(Ulid.NewUlid(), 1, "x", true));

        await ReportAsync(Story("SHOP-11"), Child("SHOP-11", "done"));

        _tasks.Verify(t => t.CreateTrackerStoryAsync(It.IsAny<TrackerStorySpec>(), It.IsAny<CancellationToken>()), Times.Never);
        (await LinksAsync()).Should().ContainSingle(l => l.TicketKey == "SHOP-10").Which.Role.Should().Be(TicketRole.Standalone);
    }

    [Fact]
    public async Task ASubTaskOfSomeoneElsesStory_IsStandalone_AndCarriesTheStoryAsParentContext()
    {
        var taskId = Ulid.NewUlid();
        _tasks.Setup(t => t.CreateTrackerTaskAsync(It.IsAny<TrackerTaskSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalTaskResult(taskId, 1, "x", true));

        await ReportAsync(Child("SHOP-11", assignedToMe: true, inIntake: true));
        var link = (await LinksAsync()).Single();
        link.Role.Should().Be(TicketRole.Standalone);
        link.ParentTitle.Should().Be("", "the Story is wanted for context and read on the next poll");

        (await _api.ExtraKnownKeysAsync(_config.Id, CancellationToken.None)).Should().Equal("SHOP-10");

        // The next poll reads the (not mine, not in intake) Story by key.
        await ReportAsync(Child("SHOP-11", assignedToMe: true) with { Body = null },
            Story() with { AssignedToMe = false, InIntake = false, ChildKeys = ["SHOP-11"] });

        link = (await LinksAsync()).Single(l => l.TicketKey == "SHOP-11");
        link.ParentTitle.Should().Be("Checkout");
        link.ParentDescription.Should().Contain("Checkout — details.");
        (await _api.ExtraKnownKeysAsync(_config.Id, CancellationToken.None)).Should().BeEmpty();
        _tasks.Verify(t => t.CreateTrackerStoryAsync(It.IsAny<TrackerStorySpec>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Sub-tasks added later ───────────────────────────────────────────────

    private async Task<TicketLink> ImportStoryAsync()
    {
        await ReportAsync(Story("SHOP-11"), Child("SHOP-11"));
        return (await LinksAsync()).Single(l => l.Role == TicketRole.Story);
    }

    [Fact]
    public async Task ANewSubTask_IsAskedForByKey_ThenJoinsTheEnd()
    {
        var storyLink = await ImportStoryAsync();
        var newMember = Ulid.NewUlid();
        _tasks.Setup(t => t.AppendStoryMemberAsync(_groupId, It.IsAny<StorySubTaskSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoryAppendResult([new StoryMemberRef(newMember, "SHOP-14", ShopRepo, 2, true)], false, false));

        // The Story moved on and grew a sub-task the report does not carry yet.
        var tracked = Story("SHOP-11", "SHOP-14") with { Body = null, InIntake = false, Status = "In Progress", StatusCategory = "inprogress" };
        await ReportAsync(tracked, Child("SHOP-11") with { Body = null });

        _tasks.Verify(t => t.SetStoryPendingChildrenAsync(_groupId, 1, It.IsAny<CancellationToken>()), Times.Once,
            "the new sub-task is pending until it is read");
        (await _api.ExtraKnownKeysAsync(_config.Id, CancellationToken.None)).Should().Contain("SHOP-14");

        await ReportAsync(tracked, Child("SHOP-11") with { Body = null }, Child("SHOP-14"));

        _tasks.Verify(t => t.AppendStoryMemberAsync(_groupId,
            It.Is<StorySubTaskSpec>(s => s.Key == "SHOP-14"), It.IsAny<CancellationToken>()), Times.Once);
        _storyPrs.Verify(p => p.ReturnToDraftAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>()), Times.Never);
        (await LinksAsync()).Should().Contain(l => l.TicketKey == "SHOP-14" && l.Role == TicketRole.SubTask && l.TaskId == newMember);
        _ = storyLink;
    }

    [Fact]
    public async Task ANewSubTask_AfterTheStoryWasReady_TurnsItsPullRequestBackIntoADraft()
    {
        await ImportStoryAsync();
        _tasks.Setup(t => t.AppendStoryMemberAsync(_groupId, It.IsAny<StorySubTaskSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoryAppendResult([new StoryMemberRef(Ulid.NewUlid(), "SHOP-14", ShopRepo, 2, true)], true, false));
        _storyPrs.Setup(p => p.ReturnToDraftAsync(_groupId, It.IsAny<CancellationToken>())).ReturnsAsync(Result.Success());

        await ReportAsync(Story("SHOP-11", "SHOP-14") with { Body = null, InIntake = false }, Child("SHOP-14"));

        _storyPrs.Verify(p => p.ReturnToDraftAsync(_groupId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ANewSubTask_AfterTheStoryPullRequestMerged_IsWorkedOnItsOwn_WithTheStoryContext()
    {
        await ImportStoryAsync();
        _tasks.Setup(t => t.AppendStoryMemberAsync(_groupId, It.IsAny<StorySubTaskSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoryAppendResult([], false, Closed: true));
        _tasks.Setup(t => t.GetStoryAsync(_groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoryContext(_groupId, OrgId, ProjectId, "[SHOP-10] Checkout", "The whole story.", "SHOP-10", null,
                "feature/SHOP-10-checkout", DateTimeOffset.UtcNow, true, 0, [], []));
        _tasks.Setup(t => t.CreateTrackerTaskAsync(It.IsAny<TrackerTaskSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalTaskResult(Ulid.NewUlid(), 9, "x", true));

        await ReportAsync(Story("SHOP-11", "SHOP-14") with { Body = null, InIntake = false }, Child("SHOP-14"));

        var link = (await LinksAsync()).Single(l => l.TicketKey == "SHOP-14");
        link.Role.Should().Be(TicketRole.Standalone);
        link.ParentKey.Should().Be("SHOP-10");
        link.ParentTitle.Should().Be("[SHOP-10] Checkout");
        link.ParentDescription.Should().Be("The whole story.");
    }

    // ── Only my sub-tasks, and only ones not started ────────────────────────

    [Fact]
    public async Task OnlySubTasksAssignedToMe_ThatAreNotUnderWay_BecomeMembers()
    {
        _config.IntakeStatusesJson = """["To Do","Ready for Dev"]""";
        await _db.SaveChangesAsync();

        await ReportAsync(
            Story("SHOP-11", "SHOP-12", "SHOP-13", "SHOP-14", "SHOP-15", "SHOP-16"),
            Child("SHOP-11"),                                                   // mine, To Do
            Child("SHOP-12", assignedToMe: false),                              // a teammate's
            Child("SHOP-13", assignedToMe: false, status: "Backlog"),           // unassigned
            Child("SHOP-14", "inprogress"),                                     // mine, already in progress
            Child("SHOP-15", "inprogress", status: "In Test"),                  // mine long ago, now with QA
            Child("SHOP-16", "inprogress", status: "Ready for Dev"));           // an intake status

        _storySpec!.SubTasks.Select(s => s.Key).Should().Equal("SHOP-11", "SHOP-16");
        (await LinksAsync()).Select(l => l.TicketKey).Should().Equal("SHOP-10", "SHOP-11", "SHOP-16");
        _tasks.Verify(t => t.CreateTrackerTaskAsync(It.IsAny<TrackerTaskSpec>(), It.IsAny<CancellationToken>()), Times.Never,
            "nobody else's sub-task becomes a task of its own either");
        _tasks.Verify(t => t.SetStoryPendingChildrenAsync(_groupId, 0, It.IsAny<CancellationToken>()), Times.Once,
            "sub-tasks read and not mine are never waited for");
    }

    [Fact]
    public void MemberCandidates_FollowTheNormalizedStatusCategory_OnBothTrackers()
    {
        string[] intake = ["New"];
        // Jira "new" and ADO "Proposed" are normalized to "todo"; "indeterminate" / InProgress / Resolved to "inprogress".
        TrackersApi.IsMemberCandidate(Child("1", "todo", status: "New"), intake).Should().BeTrue();
        TrackersApi.IsMemberCandidate(Child("2", "inprogress", status: "Active"), intake).Should().BeFalse();
        TrackersApi.IsMemberCandidate(Child("3", "inprogress", status: "Resolved"), intake).Should().BeFalse();
        TrackersApi.IsMemberCandidate(Child("4", "done", status: "Closed"), intake).Should().BeFalse();
        TrackersApi.IsMemberCandidate(Child("5", "todo", assignedToMe: false), intake).Should().BeFalse();
        TrackersApi.IsMemberCandidate(Child("6", null!, status: "Removed"), intake).Should().BeFalse("ADO Removed has no category");
        TrackersApi.IsMemberCandidate(Child("7", "inprogress", status: "new"), intake).Should().BeTrue("an intake status, any case");
    }

    [Fact]
    public async Task AStoryWithNoSubTaskOfMineToDo_IsATaskOfItsOwn()
    {
        _tasks.Setup(t => t.CreateTrackerTaskAsync(It.IsAny<TrackerTaskSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalTaskResult(Ulid.NewUlid(), 1, "x", true));

        await ReportAsync(Story("SHOP-11", "SHOP-12"), Child("SHOP-11", assignedToMe: false), Child("SHOP-12", "inprogress"));

        _tasks.Verify(t => t.CreateTrackerStoryAsync(It.IsAny<TrackerStorySpec>(), It.IsAny<CancellationToken>()), Times.Never);
        _tasks.Verify(t => t.CreateTrackerTaskAsync(It.Is<TrackerTaskSpec>(s => s.ExternalKey == "SHOP-10"), It.IsAny<CancellationToken>()),
            Times.Once);
        (await LinksAsync()).Should().ContainSingle().Which.Should().Match<TicketLink>(l =>
            l.TicketKey == "SHOP-10" && l.Role == TicketRole.Standalone && l.GroupId == null);
    }

    [Fact]
    public async Task ASubTaskAssignedToMeLater_IsWatched_ThenJoinsTheEnd()
    {
        await ReportAsync(Story("SHOP-11", "SHOP-12"), Child("SHOP-11"), Child("SHOP-12", assignedToMe: false));
        _storySpec!.SubTasks.Select(s => s.Key).Should().Equal("SHOP-11");
        _tasks.Verify(t => t.SetStoryPendingChildrenAsync(_groupId, 0, It.IsAny<CancellationToken>()), Times.Once);
        (await _api.ExtraKnownKeysAsync(_config.Id, CancellationToken.None)).Should().Equal("SHOP-12");

        var tracked = Story("SHOP-11", "SHOP-12") with { Body = null, InIntake = false, Status = "In Progress", StatusCategory = "inprogress" };
        await ReportAsync(tracked, Child("SHOP-11") with { Body = null }, Child("SHOP-12", assignedToMe: false));
        _tasks.Verify(t => t.AppendStoryMemberAsync(It.IsAny<Ulid>(), It.IsAny<StorySubTaskSpec>(), It.IsAny<CancellationToken>()), Times.Never);

        var member = Ulid.NewUlid();
        _tasks.Setup(t => t.AppendStoryMemberAsync(_groupId, It.IsAny<StorySubTaskSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoryAppendResult([new StoryMemberRef(member, "SHOP-12", ShopRepo, 2, true)], false, false));

        await ReportAsync(tracked, Child("SHOP-11") with { Body = null }, Child("SHOP-12"));

        _tasks.Verify(t => t.AppendStoryMemberAsync(_groupId, It.Is<StorySubTaskSpec>(s => s.Key == "SHOP-12"),
            It.IsAny<CancellationToken>()), Times.Once);
        (await LinksAsync()).Should().Contain(l => l.TicketKey == "SHOP-12" && l.Role == TicketRole.SubTask && l.TaskId == member);
    }

    // ── Members that are not mine (cleanup) ─────────────────────────────────

    /// <summary>Imports SHOP-10 with the given sub-tasks (all mine, not started) and points the
    /// group state at their member tasks. Releasing a member takes it out of the group.</summary>
    private async Task<Dictionary<string, TicketLink>> ImportStoryWithAsync(params string[] keys)
    {
        await ReportAsync([Story(keys), .. keys.Select(k => Child(k))]);
        var links = (await LinksAsync()).ToDictionary(l => l.TicketKey);
        _group = _group with
        {
            Members = [.. keys.Select((k, i) => new StoryMemberState(links[k].TaskId, k, i + 1, ShopRepo, false, false, false, false, false))],
        };
        _tasks.Setup(t => t.ReleaseStoryMemberAsync(It.IsAny<Ulid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Ulid taskId, string _, CancellationToken _) =>
            {
                _group = _group with { Members = [.. _group.Members.Where(m => m.TaskId != taskId)] };
                return TrackerTaskRetirement.Archived;
            });
        return links;
    }

    private static TrackerTicket Tracked(params string[] keys)
        => Story(keys) with { Body = null, InIntake = false, Status = "In Progress", StatusCategory = "inprogress" };

    [Fact]
    public async Task AMemberNotAssignedToMe_NotStarted_LeavesTheStory_AndRejoinsWhenItIsMineAgain()
    {
        var links = await ImportStoryWithAsync("SHOP-11", "SHOP-12", "SHOP-13");
        var released = links["SHOP-12"].TaskId;

        await ReportAsync(Tracked("SHOP-11", "SHOP-12", "SHOP-13"),
            Child("SHOP-11") with { Body = null }, Child("SHOP-12", assignedToMe: false) with { Body = null },
            Child("SHOP-13") with { Body = null });

        _tasks.Verify(t => t.ReleaseStoryMemberAsync(released,
            It.Is<string>(r => r.Contains("SHOP-12 is not assigned to you in Jira")), It.IsAny<CancellationToken>()), Times.Once);
        _tasks.Verify(t => t.ReleaseStoryMemberAsync(It.Is<Ulid>(id => id != released), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        var link = (await LinksAsync()).Single(l => l.TicketKey == "SHOP-12");
        link.TrackingEndedAt.Should().NotBeNull();
        link.AssignedToOwner.Should().BeFalse();
        link.Role.Should().Be(TicketRole.Standalone);
        link.GroupId.Should().BeNull();
        (await LinksAsync()).Single(l => l.TicketKey == "SHOP-10").Role.Should().Be(TicketRole.Story, "two members remain");
        _tasks.Verify(t => t.CancelStoryAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>()), Times.Never);
        _tasks.Verify(t => t.SetStoryPendingChildrenAsync(_groupId, 0, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _tasks.Verify(t => t.SetStoryPendingChildrenAsync(_groupId, It.Is<int>(n => n > 0), It.IsAny<CancellationToken>()), Times.Never);
        (await _api.ExtraKnownKeysAsync(_config.Id, CancellationToken.None)).Should().Equal(["SHOP-12"], "it is watched in case it becomes mine");

        // Given back to me, not started: revived and appended at the end of the story.
        _tasks.Setup(t => t.AppendStoryMemberAsync(_groupId, It.IsAny<StorySubTaskSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoryAppendResult([new StoryMemberRef(released, "SHOP-12", ShopRepo, 4, false)], false, false));
        await ReportAsync(Tracked("SHOP-11", "SHOP-12", "SHOP-13"),
            Child("SHOP-11") with { Body = null }, Child("SHOP-12"), Child("SHOP-13") with { Body = null });

        _tasks.Verify(t => t.ReviveTrackerTaskAsync(released, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _tasks.Verify(t => t.AppendStoryMemberAsync(_groupId, It.Is<StorySubTaskSpec>(s => s.Key == "SHOP-12"), It.IsAny<CancellationToken>()),
            Times.Once);
        _tasks.Verify(t => t.SubmitTaskAsync(OrgId, released, null, It.IsAny<CancellationToken>()), Times.Exactly(2),
            "submitted at import, and again once revived");
        link = (await LinksAsync()).Single(l => l.TicketKey == "SHOP-12");
        link.Should().Match<TicketLink>(l => l.TrackingEndedAt == null && l.AssignedToOwner
            && l.Role == TicketRole.SubTask && l.GroupId == _groupId && l.TaskId == released);
    }

    [Fact]
    public async Task AMemberUnderWayInTheTracker_NotStarted_LeavesTheStory_ButOneOrkkaMovedItselfStays()
    {
        var links = await ImportStoryWithAsync("SHOP-11", "SHOP-12");
        var moved = await _db.TicketLinks.SingleAsync(l => l.TicketKey == "SHOP-11");
        moved.SyncedPhase = TrackerPhase.PickedUp; // Orkka moved it to "In Progress" itself
        await _db.SaveChangesAsync();

        await ReportAsync(Tracked("SHOP-11", "SHOP-12"),
            Child("SHOP-11", "inprogress") with { Body = null }, Child("SHOP-12", "inprogress", status: "Resolved") with { Body = null });

        _tasks.Verify(t => t.ReleaseStoryMemberAsync(links["SHOP-12"].TaskId,
            It.Is<string>(r => r.Contains("already under way") && r.Contains("Resolved")), It.IsAny<CancellationToken>()), Times.Once);
        _tasks.Verify(t => t.ReleaseStoryMemberAsync(links["SHOP-11"].TaskId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        (await LinksAsync()).Single(l => l.TicketKey == "SHOP-11").TrackingEndedAt.Should().BeNull();
    }

    [Fact]
    public async Task WhenNoMemberIsLeft_TheStoryIsCancelled_AndTheStoryTicketIsATaskOfItsOwn()
    {
        var links = await ImportStoryWithAsync("SHOP-11", "SHOP-12");
        var storyTask = Ulid.NewUlid();
        _tasks.Setup(t => t.CreateTrackerTaskAsync(It.IsAny<TrackerTaskSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalTaskResult(storyTask, 7, "x", true));

        // Still in my To Do; neither sub-task is mine (the damage the cleanup repairs).
        await ReportAsync(Story("SHOP-11", "SHOP-12"),
            Child("SHOP-11", assignedToMe: false) with { Body = null }, Child("SHOP-12", "inprogress", assignedToMe: false) with { Body = null });

        _tasks.Verify(t => t.ReleaseStoryMemberAsync(It.IsAny<Ulid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _tasks.Verify(t => t.CancelStoryAsync(_groupId, It.IsAny<CancellationToken>()), Times.Once);
        _tasks.Verify(t => t.CreateTrackerTaskAsync(It.Is<TrackerTaskSpec>(s => s.ExternalKey == "SHOP-10"), It.IsAny<CancellationToken>()),
            Times.Once);
        var all = await LinksAsync();
        all.Single(l => l.TicketKey == "SHOP-10").Should().Match<TicketLink>(l =>
            l.Role == TicketRole.Standalone && l.TaskId == storyTask && l.GroupId == null && l.TrackingEndedAt == null);
        all.Where(l => l.TicketKey != "SHOP-10").Should().OnlyContain(l => l.TrackingEndedAt != null && l.Role == TicketRole.Standalone);
        _ = links;
    }

    [Fact]
    public async Task AStartedMemberNotAssignedToMe_KeepsGoing_WithOneNote()
    {
        var links = await ImportStoryWithAsync("SHOP-11", "SHOP-12");
        _group = _group with
        {
            Members = [.. _group.Members.Select(m => m.SubTaskKey == "SHOP-12" ? m with { Planning = true } : m)],
        };

        for (var poll = 0; poll < 2; poll++)
            await ReportAsync(Tracked("SHOP-11", "SHOP-12"),
                Child("SHOP-11") with { Body = null }, Child("SHOP-12", assignedToMe: false) with { Body = null });

        _tasks.Verify(t => t.ReleaseStoryMemberAsync(It.IsAny<Ulid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _tasks.Verify(t => t.AddTaskMessageAsync(links["SHOP-12"].TaskId, "system",
            It.Is<string>(m => m.Contains("SHOP-12 is not assigned to you in Jira; Orkka will not pick up more of it")),
            MessageType.SystemEvent, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        var link = (await LinksAsync()).Single(l => l.TicketKey == "SHOP-12");
        link.TrackingEndedAt.Should().BeNull("started work is never touched");
        link.Role.Should().Be(TicketRole.SubTask);
    }

    // ── Write-back ──────────────────────────────────────────────────────────

    private static StoryMemberState Member(string key, bool planning = false, bool developing = false, bool review = false,
        bool landed = false) => new(Ulid.NewUlid(), key, 1, ShopRepo, planning, developing, review, landed, false);

    private static TicketLink Link(TicketRole role, string key) => new()
    {
        Kind = "jira", SiteUrl = "s", TicketKey = key, TicketId = key, TicketUrl = "u", Role = role,
    };

    [Fact]
    public void Desired_ForTheStoryAndItsSubTasks_FollowsTheStory()
    {
        var story = Link(TicketRole.Story, "SHOP-10");
        var first = Link(TicketRole.SubTask, "SHOP-11");
        var second = Link(TicketRole.SubTask, "SHOP-12");

        var group = _group with { Members = [Member("SHOP-11"), Member("SHOP-12")] };
        TrackersApi.Desired(group, story).Should().Be(TrackerPhase.PickedUp);

        group = group with { Members = [Member("SHOP-11", planning: true, developing: true, review: true, landed: true), Member("SHOP-12", planning: true)] };
        TrackersApi.Desired(group, story).Should().Be(TrackerPhase.InReview, "the Story follows its furthest member");
        TrackersApi.Desired(group, first).Should().Be(TrackerPhase.InReview);
        TrackersApi.Desired(group, second).Should().Be(TrackerPhase.Planning, "a sub-task follows its own member");

        group = group with { ReadyAt = DateTimeOffset.UtcNow };
        TrackersApi.Desired(group, story).Should().Be(TrackerPhase.PrOpened);
        TrackersApi.Desired(group, second).Should().Be(TrackerPhase.PrOpened, "every sub-task reaches Code Review together");

        group = group with { Branches = [new StoryBranchState(ShopRepo, "b", "https://github.com/acme/shop/pull/7", false, DateTimeOffset.UtcNow, "merged")] };
        TrackersApi.Desired(group, first).Should().Be(TrackerPhase.Merged);
    }

    [Fact]
    public void NextMove_SkipsUnmappedSteps_AndNeverGoesBackwards()
    {
        var map = new TrackerStatusMap(Planning: "In Progress", PrOpened: "Code Review");

        TrackersApi.NextMove(TrackerPhase.None, TrackerPhase.PickedUp, map).Should().BeNull("Picked up is not mapped");
        TrackersApi.NextMove(TrackerPhase.None, TrackerPhase.InReview, map).Should().Be((TrackerPhase.Planning, "In Progress"));
        TrackersApi.NextMove(TrackerPhase.Planning, TrackerPhase.Merged, map).Should().Be((TrackerPhase.PrOpened, "Code Review"));
        TrackersApi.NextMove(TrackerPhase.PrOpened, TrackerPhase.Developing, map).Should().BeNull();

        var full = new TrackerStatusMap("Selected", "Planning", "In Progress", "In Review", "Code Review", "Done");
        foreach (var phase in Enum.GetValues<TrackerPhase>().Where(p => p != TrackerPhase.None))
            TrackersApi.NextMove(phase - 1, phase, full)!.Value.Phase.Should().Be(phase);
    }

    [Fact]
    public async Task WriteBack_MovesTheStoryAndEverySubTaskToCodeReview_OnlyWhenTheStoryIsReady()
    {
        await ImportStoryAsync();
        _group = _group with { Members = [Member("SHOP-11", planning: true, developing: true, review: true, landed: true)] };

        var (transitions, _) = await _api.PlanWriteBackAsync(OrgId, UserId, [_connection], CancellationToken.None);
        transitions.Select(t => (t.TicketKey, t.TargetStatus)).Should().BeEquivalentTo(
            [("SHOP-10", "In Progress"), ("SHOP-11", "In Progress")], "In review is not mapped; Code Review waits for ready");

        foreach (var link in await LinksAsync())
        {
            link.SyncedPhase = TrackerPhase.Planning;
            link.PendingPhase = null;
        }
        await _db.SaveChangesAsync();
        _group = _group with
        {
            ReadyAt = DateTimeOffset.UtcNow,
            Branches = [new StoryBranchState(ShopRepo, "feature/SHOP-10-checkout", "https://github.com/acme/shop/pull/7", false, DateTimeOffset.UtcNow, "open")],
        };

        var (ready, checks) = await _api.PlanWriteBackAsync(OrgId, UserId, [_connection], CancellationToken.None);
        ready.Select(t => (t.TicketKey, t.TargetStatus)).Should().BeEquivalentTo(
            [("SHOP-10", "Code Review"), ("SHOP-11", "Code Review")]);
        var check = checks.Should().ContainSingle("the story's pull request is asked about once, from the Story's link").Subject;
        check.CheckId.Should().Be(TrackersApi.GroupCheckId(_groupId, ShopRepo));
        check.PullRequestUrl.Should().Be("https://github.com/acme/shop/pull/7");

        await _api.AckTransitionsAsync(OrgId, UserId,
            new TrackerTransitionReport(Instance, [], [new TrackerPrState(check.CheckId, "merged")]));
        _tasks.Verify(t => t.SetGroupBranchForgeStateAsync(_groupId, ShopRepo, "merged", It.IsAny<CancellationToken>()), Times.Once);
    }
}
