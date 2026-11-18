namespace Orkka.Tests.Features.Trackers;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Trackers.Domain;
using Orkka.Modules.Trackers.Infrastructure;
using Orkka.Modules.Trackers.PublicApi;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// Status write-back: which status move a ticket is due (only forward, only to mapped statuses),
/// that a move handed out is not handed out twice, how acknowledgements land, and when the
/// developer's CLI is asked whether a published pull request merged.
/// </summary>
public sealed class TrackerWriteBackTests : IDisposable
{
    private static readonly Ulid OrgId = Ulid.NewUlid();
    private static readonly Ulid RepoId = Ulid.NewUlid();
    private const string UserId = "user_dana";

    private readonly TrackersDbContext _db = new(
        new DbContextOptionsBuilder<TrackersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options,
        currentUser: null);

    private readonly Mock<ITasksApi> _tasks = new(MockBehavior.Loose);
    private readonly Mock<IProjectsApi> _projects = new(MockBehavior.Loose);
    private readonly TrackersApi _api;
    private readonly TrackerConnection _connection;
    private readonly ProjectTrackerConfig _config;
    private readonly TicketLink _link;
    private TrackerTaskState _state;

    public TrackerWriteBackTests()
    {
        _api = new TrackersApi(_db, _tasks.Object, _projects.Object, Mock.Of<IRepoRouter>(), NullLogger<TrackersApi>.Instance);
        _connection = new TrackerConnection
        {
            Id = Ulid.NewUlid(), OrgId = OrgId, UserId = UserId, ConnectionKey = "jira-acme",
            Kind = TrackerKinds.Jira, SiteUrl = "https://acme.atlassian.net", MachineName = "dana-laptop",
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _config = new ProjectTrackerConfig
        {
            Id = Ulid.NewUlid(), OrgId = OrgId, ProjectId = Ulid.NewUlid(), OwnerUserId = UserId,
            TrackerConnectionId = _connection.Id, Scope = "SHOP",
            StatusMapJson = """{"started":"In Progress","prOpened":"In Review","merged":"Done"}""",
        };
        _link = new TicketLink
        {
            Id = Ulid.NewUlid(), OrgId = OrgId, ConfigId = _config.Id, ProjectId = _config.ProjectId,
            TaskId = Ulid.NewUlid(), Kind = TrackerKinds.Jira, SiteUrl = _connection.SiteUrl,
            TicketKey = "SHOP-12", TicketId = "10012", TicketUrl = "https://acme.atlassian.net/browse/SHOP-12",
            RepoId = RepoId, LastSeenAt = DateTimeOffset.UtcNow,
        };
        _db.Connections.Add(_connection);
        _db.ProjectConfigs.Add(_config);
        _db.TicketLinks.Add(_link);
        _db.SaveChanges();

        _state = new TrackerTaskState(_link.TaskId, Started: false, PublishedPrUrl: null, LandedInFolder: false, IsArchived: false);
        _tasks.Setup(t => t.GetTrackerTaskStatesAsync(It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [_state]);
        _projects.Setup(p => p.GetRepoAsync(RepoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepoSummary(RepoId, _config.ProjectId, OrgId, "shop", "git@github.com:acme/shop.git",
                "main", "#fff", WorkspaceStatus.Ready, ForgeKind: ForgeKind.GitHub, CliAccount: "dana-acme"));
    }

    public void Dispose() => _db.Dispose();

    private Task<(TrackerTransition[] Transitions, TrackerPrCheck[] PrChecks)> PlanAsync()
        => _api.PlanWriteBackAsync(OrgId, UserId, [_connection], CancellationToken.None);

    private Task AckAsync(TrackerTransition move, bool ok, string? error = null)
        => _api.AckTransitionsAsync(OrgId, UserId,
            new TrackerTransitionReport("runner-1", [new TrackerTransitionAck(move.TransitionId, ok, error)], []));

    // ── Which move ──────────────────────────────────────────────────────────

    [Fact]
    public async Task NothingStarted_NoMove()
        => (await PlanAsync()).Transitions.Should().BeEmpty();

    [Fact]
    public async Task Started_MovesToTheStartedStatus_OnTheRunnersConnection()
    {
        _state = _state with { Started = true };

        var move = (await PlanAsync()).Transitions.Should().ContainSingle().Subject;

        move.TargetStatus.Should().Be("In Progress");
        move.ConnectionId.Should().Be("jira-acme");
        move.TicketKey.Should().Be("SHOP-12");
        move.Phase.Should().Be("Planning");
    }

    [Fact]
    public async Task APublishedPullRequest_JumpsStraightToInReview()
    {
        _state = _state with { Started = true, PublishedPrUrl = "https://github.com/acme/shop/pull/9" };

        (await PlanAsync()).Transitions.Should().ContainSingle().Which.TargetStatus.Should().Be("In Review");
    }

    [Fact]
    public async Task AnUnmappedPhase_IsSkipped_NotABlocker()
    {
        _config.StatusMapJson = """{"prOpened":"In Review"}""";
        await _db.SaveChangesAsync();
        _state = _state with { Started = true };

        (await PlanAsync()).Transitions.Should().BeEmpty("Started is not mapped");

        _state = _state with { PublishedPrUrl = "https://github.com/acme/shop/pull/9" };
        (await PlanAsync()).Transitions.Should().ContainSingle().Which.TargetStatus.Should().Be("In Review");
    }

    [Fact]
    public async Task ALocalFolderMerge_IsMerged()
    {
        _state = _state with { Started = true, LandedInFolder = true };

        (await PlanAsync()).Transitions.Should().ContainSingle().Which.TargetStatus.Should().Be("Done");
    }

    [Fact]
    public async Task MovesNeverGoBackwards()
    {
        _link.SyncedPhase = TrackerPhase.PrOpened;
        await _db.SaveChangesAsync();
        _state = _state with { Started = true };

        (await PlanAsync()).Transitions.Should().BeEmpty();
    }

    [Fact]
    public async Task ATicketNoLongerTracked_IsNotMoved()
    {
        _link.TrackingEndedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        _state = _state with { Started = true };

        (await PlanAsync()).Transitions.Should().BeEmpty();
    }

    [Fact]
    public async Task AReassignedTicketWhoseWorkCarriesOn_StillMoves()
    {
        _link.AssignedToOwner = false;
        await _db.SaveChangesAsync();
        _state = _state with { Started = true };

        (await PlanAsync()).Transitions.Should().ContainSingle().Which.TargetStatus.Should().Be("In Progress");
    }

    [Fact]
    public async Task ALocalFolderLanding_WithMergedUnmapped_MovesTheTicketToPrOpened()
    {
        // Folder repos have no forge PR: landing is how they reach Code Review.
        _config.StatusMapJson = """{"started":"In Progress","prOpened":"Code Review"}""";
        await _db.SaveChangesAsync();
        _state = _state with { Started = true, LandedInFolder = true };

        var move = (await PlanAsync()).Transitions.Should().ContainSingle().Which;
        move.TargetStatus.Should().Be("Code Review");
        move.Phase.Should().Be("PrOpened");
    }

    [Fact]
    public async Task ThePrCheck_UsesTheTasksRepository_WhenRoutingSetItAfterImport()
    {
        _link.RepoId = null;
        await _db.SaveChangesAsync();
        _state = _state with { Started = true, PublishedPrUrl = "https://github.com/acme/shop/pull/9", RepoId = RepoId };

        (await PlanAsync()).PrChecks.Should().ContainSingle().Which.RepoUrl.Should().Be("git@github.com:acme/shop.git");
    }

    // ── Hand-out and acknowledgement ────────────────────────────────────────

    [Fact]
    public async Task AMoveInFlight_IsNotHandedOutTwice()
    {
        _state = _state with { Started = true };

        (await PlanAsync()).Transitions.Should().ContainSingle();
        (await PlanAsync()).Transitions.Should().BeEmpty("the first runner has not acknowledged yet");
    }

    [Fact]
    public async Task AnAcknowledgedMove_IsRecorded_AndTheNextOneFollows()
    {
        _state = _state with { Started = true };
        var started = (await PlanAsync()).Transitions.Single();

        await AckAsync(started, ok: true);

        var link = await _db.TicketLinks.SingleAsync();
        link.SyncedPhase.Should().Be(TrackerPhase.Planning);
        link.PendingPhase.Should().BeNull();
        _tasks.Verify(t => t.AddTaskMessageAsync(_link.TaskId, "system",
            It.Is<string>(m => m.Contains("Moved SHOP-12 to “In Progress” in Jira")),
            MessageType.SystemEvent, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        _state = _state with { PublishedPrUrl = "https://github.com/acme/shop/pull/9" };
        (await PlanAsync()).Transitions.Should().ContainSingle().Which.TargetStatus.Should().Be("In Review");
    }

    [Fact]
    public async Task AMoveThatKeepsFailing_IsGivenUp_AndTheTaskSaysWhy()
    {
        _state = _state with { Started = true };

        for (var attempt = 1; attempt <= TrackersApi.MaxTransitionAttempts; attempt++)
        {
            var move = (await PlanAsync()).Transitions.Should().ContainSingle().Subject;
            await AckAsync(move, ok: false, "No transition to 'In Progress' from 'Blocked'");
            // Let the retry window pass.
            _link.PendingSince = DateTimeOffset.UtcNow - TrackersApi.PendingWindow - TimeSpan.FromSeconds(1);
            await _db.SaveChangesAsync();
        }

        (await PlanAsync()).Transitions.Should().BeEmpty("given up after the cap");
        _tasks.Verify(t => t.AddTaskMessageAsync(_link.TaskId, "system",
            It.Is<string>(m => m.Contains("Couldn't move SHOP-12") && m.Contains("No transition")),
            MessageType.SystemEvent, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        (await _db.TicketLinks.SingleAsync()).LastTransitionError.Should().Contain("No transition");
        _tasks.Verify(t => t.SetTrackerSyncErrorAsync(_link.TaskId,
            "Couldn't move SHOP-12 to “In Progress” in Jira", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SomeoneElsesAck_ChangesNothing()
    {
        _state = _state with { Started = true };
        var move = (await PlanAsync()).Transitions.Single();

        await _api.AckTransitionsAsync(OrgId, "user_mallory",
            new TrackerTransitionReport("runner-x", [new TrackerTransitionAck(move.TransitionId, true)], []));

        (await _db.TicketLinks.SingleAsync()).SyncedPhase.Should().Be(TrackerPhase.None);
    }

    // ── Merged on the forge ─────────────────────────────────────────────────

    [Fact]
    public async Task APublishedPullRequest_IsCheckedWithTheDevelopersCli_ThenItsMergeMovesTheTicket()
    {
        _link.SyncedPhase = TrackerPhase.PrOpened;
        await _db.SaveChangesAsync();
        _state = _state with { Started = true, PublishedPrUrl = "https://github.com/acme/shop/pull/9" };

        var check = (await PlanAsync()).PrChecks.Should().ContainSingle().Subject;
        check.RepoUrl.Should().Be("git@github.com:acme/shop.git");
        check.ForgeKind.Should().Be("github");
        check.CliAccount.Should().Be("dana-acme");
        check.PullRequestUrl.Should().Be("https://github.com/acme/shop/pull/9");
        (await PlanAsync()).PrChecks.Should().BeEmpty("asked again only after the interval");

        await _api.AckTransitionsAsync(OrgId, UserId,
            new TrackerTransitionReport("runner-1", [], [new TrackerPrState(check.CheckId, "merged")]));

        (await PlanAsync()).Transitions.Should().ContainSingle().Which.TargetStatus.Should().Be("Done");
    }

    [Fact]
    public async Task NoMergedStatusMapped_NoPullRequestChecks()
    {
        _config.StatusMapJson = """{"started":"In Progress","prOpened":"In Review"}""";
        await _db.SaveChangesAsync();
        _state = _state with { Started = true, PublishedPrUrl = "https://github.com/acme/shop/pull/9" };

        (await PlanAsync()).PrChecks.Should().BeEmpty();
    }
}
