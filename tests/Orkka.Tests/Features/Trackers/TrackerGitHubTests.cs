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
using Orkka.Runners.Server.Hosting;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// GitHub Projects on the host: issues routed to the repository they live in, sub-issues as a
/// Story (only mine and not started), status defaults from a project's options, the status move
/// carrying its project and field, a runner too old for GitHub never handed a GitHub source, and
/// the pull request's "Closes owner/repo#n".
/// </summary>
public sealed class TrackerGitHubTests : IDisposable
{
    private static readonly Ulid OrgId = Ulid.NewUlid();
    private static readonly Ulid ProjectId = Ulid.NewUlid();
    private static readonly Ulid ShopRepo = Ulid.NewUlid();
    private static readonly Ulid ApiRepo = Ulid.NewUlid();
    private const string UserId = "user_dana";
    private const string Instance = "runner-1";
    private const string ProjectNode = "PVT_kwDOshop";

    private readonly TrackersDbContext _db = new(
        new DbContextOptionsBuilder<TrackersDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
        currentUser: null);

    private readonly Mock<ITasksApi> _tasks = new(MockBehavior.Loose);
    private readonly Mock<IProjectsApi> _projects = new(MockBehavior.Loose);
    private readonly Mock<IRepoRouter> _router = new(MockBehavior.Strict);
    private readonly TrackersApi _api;
    private readonly TrackerConnection _connection;
    private readonly ProjectTrackerConfig _config;
    private readonly List<TrackerTaskSpec> _created = [];
    private TrackerStorySpec? _storySpec;

    public TrackerGitHubTests()
    {
        _api = new TrackersApi(_db, _tasks.Object, _projects.Object, _router.Object,
            NullLogger<TrackersApi>.Instance, Mock.Of<IStoryPullRequests>());
        _connection = new TrackerConnection
        {
            Id = Ulid.NewUlid(), OrgId = OrgId, UserId = UserId, ConnectionKey = "github",
            Kind = TrackerKinds.GitHub, AuthMode = TrackerAuthModes.GhCli, SiteUrl = "https://github.com",
            MachineName = "dana-laptop", Healthy = true, LastSeenAt = DateTimeOffset.UtcNow,
        };
        _config = new ProjectTrackerConfig
        {
            Id = Ulid.NewUlid(), OrgId = OrgId, ProjectId = ProjectId, OwnerUserId = UserId,
            TrackerConnectionId = _connection.Id, Scope = "acme", BoardId = ProjectNode, BoardName = "Shop roadmap",
            StatusField = "Stage", RepoFilterJson = """["acme/shop","acme/api"]""", IntakeStatusesJson = """["Todo"]""",
            LeaseHolder = Instance, LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            StatusMapJson = """{"developing":"In Progress","inReview":"In Review","merged":"Done"}""",
        };
        _db.Connections.Add(_connection);
        _db.ProjectConfigs.Add(_config);
        _db.SaveChanges();

        _projects.Setup(p => p.ListReposAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new RepoSummary(ShopRepo, ProjectId, OrgId, "shop", "git@github-work:acme/shop.git", "main", "#fff",
                    WorkspaceStatus.Ready, ForgeKind: ForgeKind.GitHub),
                new RepoSummary(ApiRepo, ProjectId, OrgId, "api", "https://github.com/acme/api.git", "main", "#fff",
                    WorkspaceStatus.Ready, ForgeKind: ForgeKind.GitHub)]);
        _tasks.Setup(t => t.CreateTrackerTaskAsync(It.IsAny<TrackerTaskSpec>(), It.IsAny<CancellationToken>()))
            .Callback((TrackerTaskSpec s, CancellationToken _) => _created.Add(s))
            .ReturnsAsync(() => new ExternalTaskResult(Ulid.NewUlid(), 7, "x", true));
        _tasks.Setup(t => t.CreateTrackerStoryAsync(It.IsAny<TrackerStorySpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrackerStorySpec spec, CancellationToken _) =>
            {
                _storySpec = spec;
                return new TrackerStoryResult(Ulid.NewUlid(),
                    [.. spec.SubTasks.Select((s, i) => new StoryMemberRef(Ulid.NewUlid(), s.Key, s.RepoIds.FirstOrDefault(), i + 1, true))],
                    true);
            });
        _tasks.Setup(t => t.GetTrackerTaskStatesAsync(It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _tasks.Setup(t => t.GetTrackerGroupStatesAsync(It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _tasks.Setup(t => t.SubmitTaskAsync(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardOpOutcome(true));
    }

    public void Dispose() => _db.Dispose();

    private static TrackerTicket Issue(string key, string? status = "Todo", string category = "todo", bool mine = true,
        bool intake = false, string[]? children = null, string? parent = null)
    {
        var repo = key[..key.IndexOf('#')];
        return new TrackerTicket(key, $"I_{key}", $"https://github.com/{repo}/issues/{key[(key.IndexOf('#') + 1)..]}", null, $"h-{key}",
            new TrackerTicketBody($"Issue {key}", null, status, category, null, "Markdown body", null, null, [], [], DateTimeOffset.UtcNow,
                Repository: $"https://github.com/{repo}"),
            [], Status: status, StatusCategory: category, AssignedToMe: mine, InIntake: intake,
            ParentKey: parent, ParentType: parent is null ? null : "Issue", ChildKeys: children);
    }

    private Task<TrackerReportAck> ReportAsync(params TrackerTicket[] tickets) => _api.ReportAsync(OrgId, UserId,
        new TrackerTicketReport(_config.Id.ToString(), Instance, true, null, DateTimeOffset.UtcNow, tickets));

    [Fact]
    public async Task AnIssue_IsWorkedInTheRepositoryItLivesIn_EvenThroughAnSshAlias()
    {
        (await ReportAsync(Issue("acme/api#5", intake: true), Issue("acme/shop#6", intake: true))).Accepted.Should().BeTrue();

        _created.Select(s => (s.ExternalKey, s.RepoId)).Should().BeEquivalentTo(new[] { ("acme/api#5", (Ulid?)ApiRepo), ("acme/shop#6", (Ulid?)ShopRepo) });
        _created.Select(s => s.ExternalRef).Should().BeEquivalentTo("github:github.com:acme/api#5", "github:github.com:acme/shop#6");
        _created.Select(s => s.Title).Should().Contain("[acme/api#5] Issue acme/api#5");
        _router.VerifyNoOtherCalls();
        var links = await _db.TicketLinks.IgnoreQueryFilters().ToListAsync();
        links.Should().OnlyContain(l => l.Kind == TrackerKinds.GitHub && l.TicketId.StartsWith("I_"));
    }

    [Theory]
    [InlineData("https://github.com/acme/shop", true)]
    [InlineData("https://github.com/ACME/Shop", true)]
    [InlineData("https://github.com/acme/web", false)]
    [InlineData("https://acme.ghe.com/acme/shop", true)]
    [InlineData(null, false)]
    public void RepositoryMatching_IsByOwnerAndName_OnTheSameHostOrAnAlias(string? issueRepo, bool matchesShop)
    {
        RepoSummary Repo(Ulid id, string url) => new(id, ProjectId, OrgId, "r", url, "main", "#fff", WorkspaceStatus.Ready);
        var repos = new[] { Repo(ShopRepo, "git@github-work:acme/shop.git"), Repo(ApiRepo, "https://github.com/acme/api") };

        TrackersApi.MatchRepository(repos, issueRepo).Should().Be(matchesShop ? ShopRepo : null);
    }

    [Fact]
    public void RepositoryMatching_TwoCandidates_IsNoAnswer()
    {
        RepoSummary Repo(Ulid id, string url) => new(id, ProjectId, OrgId, "r", url, "main", "#fff", WorkspaceStatus.Ready);
        TrackersApi.MatchRepository([Repo(ShopRepo, "https://github.com/acme/shop"), Repo(ApiRepo, "git@github.com:acme/shop.git")],
            "https://github.com/acme/shop").Should().BeNull();
    }

    [Fact]
    public async Task AnIssueWithSubIssues_IsAStory_OfMyNotStartedSubIssues_InGitHubsOrder()
    {
        (await ReportAsync(
            Issue("acme/shop#20", intake: true, children: ["acme/shop#22", "acme/api#5", "acme/shop#21", "acme/shop#23"]),
            Issue("acme/shop#21", status: null, parent: "acme/shop#20"),
            Issue("acme/shop#22", status: "Todo", parent: "acme/shop#20"),
            Issue("acme/api#5", mine: false, parent: "acme/shop#20"),
            Issue("acme/shop#23", status: "In Progress", category: "inprogress", parent: "acme/shop#20"))).Accepted.Should().BeTrue();

        _storySpec.Should().NotBeNull();
        _storySpec!.StoryKey.Should().Be("acme/shop#20");
        _storySpec.SubTasks.Select(s => s.Key).Should().Equal(["acme/shop#22", "acme/shop#21"],
            "a teammate's sub-issue and one already in progress are never members");
        _storySpec.SubTasks.Should().OnlyContain(s => s.RepoIds.SequenceEqual(new[] { ShopRepo }));
        _created.Should().BeEmpty();
    }

    [Fact]
    public async Task AnIssueWhoseSubIssuesAreNotMine_IsAPlainTask()
    {
        (await ReportAsync(
            Issue("acme/shop#30", intake: true, children: ["acme/shop#31"]),
            Issue("acme/shop#31", mine: false, parent: "acme/shop#30"))).Accepted.Should().BeTrue();

        _storySpec.Should().BeNull();
        _created.Select(s => s.ExternalKey).Should().Equal("acme/shop#30");
    }

    [Fact]
    public async Task AClosedIssue_BeforeWorkStarted_IsRetired_AndATrackedOneThatMovedKeepsSyncing()
    {
        await ReportAsync(Issue("acme/shop#6", intake: true), Issue("acme/shop#7", intake: true));
        _tasks.Setup(t => t.RetireTrackerTaskAsync(It.IsAny<Ulid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TrackerTaskRetirement.Archived);

        await ReportAsync(
            Issue("acme/shop#6", status: "In Progress", category: "inprogress"),
            Issue("acme/shop#7", status: "Todo", category: "done"));

        var links = await _db.TicketLinks.IgnoreQueryFilters().OrderBy(l => l.TicketKey).ToListAsync();
        links[0].RemoteStatus.Should().Be("In Progress");
        links[0].TrackingEndedAt.Should().BeNull("moving on the board is not leaving it");
        links[1].TrackingEndedAt.Should().NotBeNull();
        _tasks.Verify(t => t.RetireTrackerTaskAsync(It.IsAny<Ulid>(), It.Is<string>(m => m.Contains("in GitHub")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AStatusMove_CarriesTheProjectAndTheStatusField()
    {
        _db.TicketLinks.Add(new TicketLink
        {
            Id = Ulid.NewUlid(), OrgId = OrgId, ConfigId = _config.Id, ProjectId = ProjectId, TaskId = Ulid.NewUlid(),
            Kind = TrackerKinds.GitHub, SiteUrl = "https://github.com", TicketKey = "acme/shop#6", TicketId = "I_kwDOissue6",
            TicketUrl = "https://github.com/acme/shop/issues/6", RepoId = ShopRepo, LastSeenAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
        var taskId = (await _db.TicketLinks.SingleAsync()).TaskId;
        _tasks.Setup(t => t.GetTrackerTaskStatesAsync(It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TrackerTaskState(taskId, true, null, false, false, ShopRepo, Developing: true)]);

        var (transitions, _) = await _api.PlanWriteBackAsync(OrgId, UserId, [_connection], CancellationToken.None);

        var move = transitions.Should().ContainSingle().Subject;
        move.Should().BeEquivalentTo(new
        {
            ConnectionId = "github", TicketKey = "acme/shop#6", TicketId = "I_kwDOissue6",
            TargetStatus = "In Progress", Phase = "Developing", BoardId = ProjectNode, StatusField = "Stage",
        });
    }

    [Fact]
    public void Defaults_FollowTheProjectTemplatesNames()
    {
        var d = TrackerStatusDefaults.Compute(TrackerKinds.GitHub,
        [
            new("Backlog", "todo"), new("Todo", "todo"), new("Ready", "todo"), new("In Progress", "inprogress"),
            new("In Review", "inprogress"), new("Done", "done"),
        ]);

        d.IntakeStatuses.Should().Equal("Todo", "Ready");
        d.StatusMap.Should().Be(new TrackerStatusMap(Developing: "In Progress", InReview: "In Review", Merged: "Done"));
    }

    [Fact]
    public void Defaults_WithUnusualNames_PickUpTheToDoCategory_AndMapNothingElse()
    {
        var d = TrackerStatusDefaults.Compute(TrackerKinds.GitHub, [new("New", "todo"), new("Building", "inprogress")]);

        d.IntakeStatuses.Should().Equal("New");
        d.StatusMap.Should().Be(new TrackerStatusMap());
    }

    [Fact]
    public void TheTrackerIsCalledGitHub()
    {
        TrackersApi.TrackerName(TrackerKinds.GitHub).Should().Be("GitHub");
        TrackersApi.ExternalRef(_connection, "acme/shop#12").Should().Be("github:github.com:acme/shop#12");
    }

    [Fact]
    public void RunnerRelay_RefusesGitHubWork_ToARunnerWithoutTheCapability()
    {
        string[] boardsOnly = [RunnerCapabilities.TrackerConfig, RunnerCapabilities.TrackerBoards];
        string[] current = [.. boardsOnly, RunnerCapabilities.TrackerGitHub];

        RunnerRelayEndpoints.TrackerQueryRefusal(new TrackerQueryRequest("github", TrackerQueryKinds.Fields, "acme", BoardId: ProjectNode), boardsOnly)
            .Should().Be(RunnerRelayEndpoints.OldRunnerGitHub);
        RunnerRelayEndpoints.TrackerQueryRefusal(new TrackerQueryRequest("github", TrackerQueryKinds.Repos, "acme", BoardId: ProjectNode), boardsOnly)
            .Should().Be(RunnerRelayEndpoints.OldRunnerGitHub);
        RunnerRelayEndpoints.TrackerQueryRefusal(new TrackerQueryRequest("github", TrackerQueryKinds.Preview, "acme", BoardId: ProjectNode,
            StatusField: "Stage"), boardsOnly).Should().Be(RunnerRelayEndpoints.OldRunnerGitHub);
        RunnerRelayEndpoints.TrackerQueryRefusal(new TrackerQueryRequest("github", TrackerQueryKinds.Preview, "acme", BoardId: ProjectNode,
            RepoFilter: ["acme/shop"]), boardsOnly).Should().Be(RunnerRelayEndpoints.OldRunnerGitHub);
        RunnerRelayEndpoints.TrackerQueryRefusal(new TrackerQueryRequest("github", TrackerQueryKinds.Boards, "acme"), [RunnerCapabilities.TrackerConfig])
            .Should().Be(RunnerRelayEndpoints.OldRunnerBoards);
        RunnerRelayEndpoints.TrackerQueryRefusal(new TrackerQueryRequest("github", TrackerQueryKinds.Fields, "acme", BoardId: ProjectNode), current)
            .Should().BeNull();
        RunnerRelayEndpoints.TrackerQueryRefusal(new TrackerQueryRequest("jira-acme", TrackerQueryKinds.Statuses, "SHOP"), [RunnerCapabilities.TrackerConfig])
            .Should().BeNull("Jira and Azure DevOps questions are unchanged");

        RunnerRelayEndpoints.TrackerConnectionRefusal(new AddTrackerConnectionRequest("github", "github.com", null, "", "gh"), boardsOnly)
            .Should().Be(RunnerRelayEndpoints.OldRunnerGitHub);
        RunnerRelayEndpoints.TrackerConnectionRefusal(new AddTrackerConnectionRequest("github", "github.com", null, "", "gh"), current)
            .Should().BeNull();
        RunnerRelayEndpoints.TrackerConnectionRefusal(new AddTrackerConnectionRequest("jira", "acme", "d@acme.com", "t"), boardsOnly)
            .Should().BeNull();
    }
}

/// <summary>"Closes owner/repo#n" in a published pull request, for issues in its own repository.</summary>
public sealed class GitHubIssueLinksTests
{
    [Theory]
    [InlineData("https://github.com/acme/shop/issues/12", "https://github.com/acme/shop.git", "Closes acme/shop#12")]
    [InlineData("https://github.com/acme/shop/issues/12", "git@github.com:acme/shop.git", "Closes acme/shop#12")]
    [InlineData("https://github.com/acme/shop/issues/12", "git@github-work:Acme/Shop.git", "Closes acme/shop#12")]
    [InlineData("https://acme.ghe.com/acme/shop/issues/3", "https://acme.ghe.com/acme/shop", "Closes acme/shop#3")]
    [InlineData("https://github.com/acme/shop/issues/12", "https://github.com/acme/api.git", null)]
    [InlineData("https://github.com/acme/shop/issues/12", "https://gitlab.com/acme/shop.git", null)]
    [InlineData("https://acme.atlassian.net/browse/SHOP-12", "https://github.com/acme/shop.git", null)]
    [InlineData("https://github.com/acme/shop/pull/12", "https://github.com/acme/shop.git", null)]
    [InlineData(null, "https://github.com/acme/shop.git", null)]
    public void ClosingReference(string? issueUrl, string repo, string? expected)
        => GitHubIssueLinks.ClosingReference(issueUrl, repo).Should().Be(expected);

    [Fact]
    public void AppendClosing_AddsEachLineOnce()
    {
        var body = GitHubIssueLinks.AppendClosing("## Task\nDo it\n", ["Closes acme/shop#1", "Closes acme/shop#2"]);
        body.Should().Be("## Task\nDo it\n\nCloses acme/shop#1\nCloses acme/shop#2");
        GitHubIssueLinks.AppendClosing(body, ["Closes acme/shop#1"]).Should().Be(body);
    }

    [Fact]
    public void AStoryPullRequest_ClosesTheStoryAndItsSubIssues_InItsOwnRepository()
    {
        StoryMemberContext Member(int order, string key, bool archived = false) => new(
            Ulid.NewUlid(), order, $"[{key}] Part {order}", order, null, Orkka.Modules.Tasks.Contracts.TaskStatus.Completed,
            IsMerged: false, IsArchived: archived, ReviewBaseSha: null, PlanSummary: null, Files: [],
            ExternalKey: key, ExternalUrl: $"https://github.com/{key.Replace("#", "/issues/")}");
        var story = new StoryContext(Ulid.NewUlid(), Ulid.NewUlid(), Ulid.NewUlid(), "[acme/shop#20] Checkout", null,
            "acme/shop#20", "https://github.com/acme/shop/issues/20", "feature/acme/shop-20-checkout", null, false, 0,
            [Member(1, "acme/shop#22"), Member(2, "acme/api#5"), Member(3, "acme/shop#21"), Member(4, "acme/shop#24", archived: true)], []);

        var body = Orkka.Modules.SourceControl.Infrastructure.Services.StoryLandingService.Body(
            story, Ulid.NewUlid(), null, "git@github.com:acme/shop.git");

        body.Should().EndWith("\n\nCloses acme/shop#20\nCloses acme/shop#22\nCloses acme/shop#21");
        Orkka.Modules.SourceControl.Infrastructure.Services.StoryLandingService.Body(story, Ulid.NewUlid(), null)
            .Should().NotContain("Closes", "a folder landing has no forge to link");
    }

    [Fact]
    public void GheDotCom_IsGitHub()
        => RepoUrl.DetectForge("git@acme.ghe.com:acme/shop.git").Should().Be(ForgeKind.GitHub);
}
