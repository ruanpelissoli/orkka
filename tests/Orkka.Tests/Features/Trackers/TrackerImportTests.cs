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
/// Importing a ticket by its key: the key (or link) is normalized for the source's tracker; the
/// request makes the source due and puts the ticket and its picked sub-tasks on the next poll;
/// the report imports it whatever its status or assignee — a task, or a Story with exactly the
/// picked members (the unticked ones never join and are never waited for). Imported links are
/// pinned: a reassignment does not retire them and a member is never released for not being
/// mine. A ticket already on the board is pinned and answered at once; one the tracker never
/// returns fails after a few polls; an epic is refused.
/// </summary>
public sealed class TrackerImportTests : IDisposable
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
    private readonly TrackersApi _api;
    private readonly ProjectTrackerConfig _config;
    private readonly Ulid _groupId = Ulid.NewUlid();
    private TrackerStorySpec? _storySpec;

    public TrackerImportTests()
    {
        _api = new TrackersApi(_db, _tasks.Object, _projects.Object, Mock.Of<IRepoRouter>(),
            NullLogger<TrackersApi>.Instance, Mock.Of<IStoryPullRequests>());
        var connection = new TrackerConnection
        {
            Id = Ulid.NewUlid(), OrgId = OrgId, UserId = UserId, ConnectionKey = "jira-acme",
            Kind = TrackerKinds.Jira, SiteUrl = "https://acme.atlassian.net", MachineName = "dana-laptop",
            Healthy = true, LastSeenAt = DateTimeOffset.UtcNow,
        };
        _config = new ProjectTrackerConfig
        {
            Id = Ulid.NewUlid(), OrgId = OrgId, ProjectId = ProjectId, OwnerUserId = UserId,
            TrackerConnectionId = connection.Id, Scope = "SHOP", LeaseHolder = Instance,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), LastPolledAt = DateTimeOffset.UtcNow,
        };
        _db.Connections.Add(connection);
        _db.ProjectConfigs.Add(_config);
        _db.SaveChanges();

        _projects.Setup(p => p.ListReposAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new RepoSummary(ShopRepo, ProjectId, OrgId, "shop", "git@github.com:acme/shop.git", "main", "#fff",
                WorkspaceStatus.Ready, ForgeKind: ForgeKind.GitHub)]);
        _tasks.Setup(t => t.CreateTrackerStoryAsync(It.IsAny<TrackerStorySpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrackerStorySpec spec, CancellationToken _) =>
            {
                _storySpec = spec;
                return new TrackerStoryResult(_groupId,
                    [.. spec.SubTasks.Select((s, i) => new StoryMemberRef(Ulid.NewUlid(), s.Key, s.RepoIds.FirstOrDefault(), i + 1, true))],
                    true);
            });
        _tasks.Setup(t => t.CreateTrackerTaskAsync(It.IsAny<TrackerTaskSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ExternalTaskResult(Ulid.NewUlid(), 1, "x", true));
        _tasks.Setup(t => t.SubmitTaskAsync(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardOpOutcome(true));
        _tasks.Setup(t => t.GetTrackerTaskStatesAsync(It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _tasks.Setup(t => t.GetTrackerGroupStatesAsync(It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [new TrackerGroupState(_groupId, TaskGroupKind.Story, "SHOP-10", "b", null, false, 0, [], [])]);
    }

    public void Dispose() => _db.Dispose();

    private static TrackerTicketBody Body(string title) => new(
        title, "Story", "In Progress", "inprogress", "Medium", $"{title} — details.", null, null, [], [], DateTimeOffset.UtcNow);

    // Assigned to a teammate and already in progress: intake would never take it.
    private static TrackerTicket Story(params string[] children) => new(
        "SHOP-10", "10010", "https://acme.atlassian.net/browse/SHOP-10", null, "hs", Body("Checkout"), [],
        Status: "In Progress", StatusCategory: "inprogress", AssignedToMe: false, InIntake: false, ChildKeys: children)
    {
        Level = TicketLevel.Story,
    };

    private static TrackerTicket Child(string key, bool assignedToMe = false, string category = "todo") => new(
        key, key, $"https://acme.atlassian.net/browse/{key}", null, $"h-{key}", Body($"Part {key}"), [],
        Status: category == "todo" ? "To Do" : "In Progress", StatusCategory: category, AssignedToMe: assignedToMe,
        InIntake: false, ParentKey: "SHOP-10", ParentType: "Story")
    {
        Level = TicketLevel.SubTask,
        ParentLevel = TicketLevel.Story,
    };

    private Task<TrackerReportAck> ReportAsync(params TrackerTicket[] tickets) => _api.ReportAsync(OrgId, UserId,
        new TrackerTicketReport(_config.Id.ToString(), Instance, true, null, DateTimeOffset.UtcNow, tickets));

    private Task<List<TicketLink>> LinksAsync() => _db.TicketLinks.IgnoreQueryFilters().OrderBy(l => l.TicketKey).ToListAsync();

    private async Task<TrackerImport> ImportRowAsync() => await _db.Imports.IgnoreQueryFilters().AsNoTracking().SingleAsync();

    // ── Keys ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(TrackerKinds.Jira, "shop-10", "SHOP-10")]
    [InlineData(TrackerKinds.Jira, "https://acme.atlassian.net/browse/SHOP-10", "SHOP-10")]
    [InlineData(TrackerKinds.Jira, "https://acme.atlassian.net/jira/software/projects/SHOP/issues/?selectedIssue=SHOP-10", "SHOP-10")]
    [InlineData(TrackerKinds.Jira, "10", null)]
    [InlineData(TrackerKinds.AzureDevOps, "4567", "4567")]
    [InlineData(TrackerKinds.AzureDevOps, "#4567", "4567")]
    [InlineData(TrackerKinds.AzureDevOps, "https://dev.azure.com/contoso/Shop/_workitems/edit/4567/", "4567")]
    [InlineData(TrackerKinds.AzureDevOps, "SHOP-1", null)]
    [InlineData(TrackerKinds.GitHub, "acme/shop#12", "acme/shop#12")]
    [InlineData(TrackerKinds.GitHub, "https://github.com/acme/shop/issues/12", "acme/shop#12")]
    [InlineData(TrackerKinds.GitHub, "#12", null)]
    public void AKeyOrLink_IsNormalizedForTheTracker(string kind, string input, string? expected)
        => TrackersApi.NormalizeKey(kind, input).Should().Be(expected);

    // ── Request ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ARequest_IsQueued_AndMakesTheSourceDue_AndTheNextPollReadsItsKeys()
    {
        var result = await _api.RequestImportAsync(ProjectId, UserId, "https://acme.atlassian.net/browse/shop-10", ["SHOP-11", "shop-12"]);

        result.StatusCode.Should().Be(202);
        result.Import!.State.Should().Be("pending");
        result.Import.TicketKey.Should().Be("SHOP-10");
        (await _db.ProjectConfigs.AsNoTracking().SingleAsync()).LastPolledAt.Should().BeNull("due now, not after the poll interval");
        (await _api.ExtraKnownKeysAsync(_config.Id, CancellationToken.None)).Should().Equal("SHOP-10", "SHOP-11", "SHOP-12");
    }

    [Fact]
    public async Task OnlyTheSourcesOwner_CanImport()
        => (await _api.RequestImportAsync(ProjectId, "user_someone", "SHOP-10", [])).StatusCode.Should().Be(403);

    // ── The poll ────────────────────────────────────────────────────────

    [Fact]
    public async Task AStoryIsImportedWithExactlyThePickedSubTasks_WhoeverHasThem_AndPinned()
    {
        await _api.RequestImportAsync(ProjectId, UserId, "SHOP-10", ["SHOP-11", "SHOP-13"]);

        await ReportAsync(Story("SHOP-11", "SHOP-12", "SHOP-13"), Child("SHOP-11"), Child("SHOP-13", assignedToMe: true));

        _storySpec!.SubTasks.Select(s => s.Key).Should().Equal("SHOP-11", "SHOP-13");
        var links = await LinksAsync();
        links.Select(l => (l.TicketKey, l.Role)).Should().Equal(
            ("SHOP-10", TicketRole.Story), ("SHOP-11", TicketRole.SubTask), ("SHOP-13", TicketRole.SubTask));
        links.Should().OnlyContain(l => l.Pinned);
        links[0].ExcludedChildKeysJson.Should().Be("""["SHOP-12"]""");
        _tasks.Verify(t => t.SetStoryPendingChildrenAsync(_groupId, 0, It.IsAny<CancellationToken>()), Times.Once,
            "the unticked sub-task is never waited for");
        var import = await ImportRowAsync();
        import.CompletedAt.Should().NotBeNull();
        import.TaskId.Should().Be(links[0].TaskId);
        import.Error.Should().BeNull();

        // The unticked sub-task is never read again, nor joins later — even once it is mine.
        (await _api.ExtraKnownKeysAsync(_config.Id, CancellationToken.None)).Should().BeEmpty();
        await ReportAsync(Story("SHOP-11", "SHOP-12", "SHOP-13") with { Body = null }, Child("SHOP-12", assignedToMe: true));
        _tasks.Verify(t => t.AppendStoryMemberAsync(It.IsAny<Ulid>(), It.IsAny<StorySubTaskSpec>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task APinnedMember_IsNeverReleasedForNotBeingMine()
    {
        await _api.RequestImportAsync(ProjectId, UserId, "SHOP-10", ["SHOP-11"]);
        await ReportAsync(Story("SHOP-11"), Child("SHOP-11"));

        await ReportAsync(Story("SHOP-11") with { Body = null }, Child("SHOP-11", category: "inprogress") with { Body = null });

        _tasks.Verify(t => t.ReleaseStoryMemberAsync(It.IsAny<Ulid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        (await LinksAsync()).Should().OnlyContain(l => l.TrackingEndedAt == null);
    }

    [Fact]
    public async Task ATicketWithNoSubTasksPicked_IsATask_AndAReassignmentDoesNotRetireIt()
    {
        await _api.RequestImportAsync(ProjectId, UserId, "SHOP-10", []);
        await ReportAsync(Story("SHOP-11"));

        var link = (await LinksAsync()).Single();
        link.Role.Should().Be(TicketRole.Standalone);
        link.Pinned.Should().BeTrue();
        _tasks.Verify(t => t.CreateTrackerStoryAsync(It.IsAny<TrackerStorySpec>(), It.IsAny<CancellationToken>()), Times.Never);

        // Assigned to someone else all along — and reassigned again: it stays.
        await ReportAsync(Story("SHOP-11") with { Body = null });
        _tasks.Verify(t => t.RetireTrackerTaskAsync(It.IsAny<Ulid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        (await LinksAsync()).Single().TrackingEndedAt.Should().BeNull();
    }

    [Fact]
    public async Task ATicketAlreadyOnTheBoard_IsPinned_AndAnsweredAtOnce()
    {
        await _api.RequestImportAsync(ProjectId, UserId, "SHOP-10", []);
        await ReportAsync(Story());
        var taskId = (await LinksAsync()).Single().TaskId;
        (await _db.TicketLinks.SingleAsync()).Pinned = false;
        await _db.SaveChangesAsync();

        var again = await _api.RequestImportAsync(ProjectId, UserId, "SHOP-10", []);

        again.StatusCode.Should().Be(200);
        again.Import!.State.Should().Be("done");
        again.Import.TaskId.Should().Be(taskId);
        (await _db.TicketLinks.AsNoTracking().SingleAsync()).Pinned.Should().BeTrue();
    }

    [Fact]
    public async Task ATicketTheTrackerNeverReturns_FailsAfterAFewPolls()
    {
        await _api.RequestImportAsync(ProjectId, UserId, "SHOP-99", []);

        for (var i = 0; i < TrackersApi.MaxImportAttempts; i++)
            await ReportAsync();

        var import = await ImportRowAsync();
        import.CompletedAt.Should().NotBeNull();
        import.Error.Should().Contain("SHOP-99 was not found");
        (await _api.GetImportAsync(ProjectId, import.Id))!.State.Should().Be("failed");
    }

    [Fact]
    public async Task AnEpic_IsRefused()
    {
        await _api.RequestImportAsync(ProjectId, UserId, "SHOP-10", []);

        await ReportAsync(Story() with { Level = TicketLevel.Above });

        (await ImportRowAsync()).Error.Should().Contain("above a story");
        (await LinksAsync()).Should().BeEmpty();
    }
}
