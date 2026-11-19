namespace Orkka.Tests.Features.Trackers;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Trackers.Contracts;
using Orkka.Modules.Trackers.Domain;
using Orkka.Modules.Trackers.Infrastructure;
using Orkka.Modules.Trackers.PublicApi;

/// <summary>
/// The guided setup's two promises about a ticket source. The gate: before the developer presses
/// Start nothing a poll reports is imported; after it, the same report is. And the dry run: for the
/// same tickets it lists exactly the tasks, Stories and Story members the real poll creates — the
/// same keys, the same order, the same routing — and creates nothing.
/// </summary>
public sealed class TrackerSetupGateAndPreviewTests
{
    private static readonly Ulid OrgId = Ulid.NewUlid();
    private static readonly Ulid ProjectId = Ulid.NewUlid();
    private static readonly Ulid ApiRepo = Ulid.NewUlid();
    private static readonly Ulid WebRepo = Ulid.NewUlid();
    private const string UserId = "user_dana";
    private const string Instance = "runner-1";

    /// <summary>One TrackersApi over its own in-memory database, with every call it makes into
    /// the Tasks module recorded.</summary>
    private sealed class Harness : IDisposable
    {
        public TrackersDbContext Db { get; } = new(
            new DbContextOptionsBuilder<TrackersDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            currentUser: null);

        public Mock<ITasksApi> Tasks { get; } = new(MockBehavior.Loose);
        public Mock<IProjectsApi> Projects { get; } = new(MockBehavior.Loose);
        // Loose: the real poll asks it about the unrouted ticket and gets no answer (as with an
        // offline runner), so that ticket is created without a repository and asks.
        public Mock<IRepoRouter> Router { get; } = new(MockBehavior.Loose);
        public TrackersApi Api { get; }
        public ProjectTrackerConfig Config { get; }
        public HashSet<Ulid> Pending { get; } = [];
        public List<TrackerTaskSpec> CreatedTasks { get; } = [];
        public List<TrackerStorySpec> CreatedStories { get; } = [];

        public Harness(bool storyMode = true)
        {
            Api = new TrackersApi(Db, Tasks.Object, Projects.Object, Router.Object, NullLogger<TrackersApi>.Instance);
            var connection = new TrackerConnection
            {
                Id = Ulid.NewUlid(), OrgId = OrgId, UserId = UserId, ConnectionKey = "jira-acme",
                Kind = TrackerKinds.Jira, SiteUrl = "https://acme.atlassian.net", MachineName = "dana-laptop",
                DisplayName = "Dana", Healthy = true, LastSeenAt = DateTimeOffset.UtcNow,
            };
            Config = new ProjectTrackerConfig
            {
                Id = Ulid.NewUlid(), OrgId = OrgId, ProjectId = ProjectId, OwnerUserId = UserId,
                TrackerConnectionId = connection.Id, Scope = "SHOP", LeaseHolder = Instance,
                LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), StoryMode = storyMode ? 1 : 0,
                DefaultRepoId = null,
                Epics =
                [
                    new TrackerEpicMapping
                    {
                        Id = Ulid.NewUlid(), OrgId = OrgId, Kind = TrackerRuleKind.Label, EpicKey = "frontend",
                        RepoId = WebRepo, Position = 0,
                    },
                    new TrackerEpicMapping
                    {
                        Id = Ulid.NewUlid(), OrgId = OrgId, Kind = TrackerRuleKind.Label, EpicKey = "backend",
                        RepoId = ApiRepo, Position = 1,
                    },
                ],
            };
            Db.Connections.Add(connection);
            Db.ProjectConfigs.Add(Config);
            Db.SaveChanges();

            Projects.Setup(p => p.ListReposAsync(ProjectId, It.IsAny<CancellationToken>()))
                .ReturnsAsync([Repo(ApiRepo, "shop-api"), Repo(WebRepo, "shop-web")]);
            Projects.Setup(p => p.GetSetupPendingProjectIdsAsync(It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Ulid> ids, CancellationToken _) => ids.Where(Pending.Contains).ToHashSet());
            Tasks.Setup(t => t.CreateTrackerTaskAsync(It.IsAny<TrackerTaskSpec>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((TrackerTaskSpec spec, CancellationToken _) =>
                {
                    CreatedTasks.Add(spec);
                    return new ExternalTaskResult(Ulid.NewUlid(), CreatedTasks.Count, spec.Title, true);
                });
            Tasks.Setup(t => t.CreateTrackerStoryAsync(It.IsAny<TrackerStorySpec>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((TrackerStorySpec spec, CancellationToken _) =>
                {
                    CreatedStories.Add(spec);
                    return new TrackerStoryResult(Ulid.NewUlid(),
                        [.. spec.SubTasks.Select((s, i) => new StoryMemberRef(Ulid.NewUlid(), s.Key, s.RepoIds.FirstOrDefault(), i + 1, true))],
                        true);
                });
            Tasks.Setup(t => t.GetTrackerTaskStatesAsync(It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            Tasks.Setup(t => t.SubmitTaskAsync(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new BoardOpOutcome(true));
        }

        public Task<TrackerReportAck> ReportAsync(IEnumerable<TrackerTicket> tickets) => Api.ReportAsync(OrgId, UserId,
            new TrackerTicketReport(Config.Id.ToString(), Instance, true, null, DateTimeOffset.UtcNow, [.. tickets]));

        public void Dispose() => Db.Dispose();
    }

    private static RepoSummary Repo(Ulid id, string name) =>
        new(id, ProjectId, OrgId, name, $"git@github.com:acme/{name}.git", "main", "#fff", WorkspaceStatus.Ready);

    private static TrackerTicketBody Body(string title, string label = "backend", string type = "Story") => new(
        title, type, "To Do", "todo", "Medium", $"{title} — details.", null, null, [label], [], DateTimeOffset.UtcNow);

    private static TrackerTicket Ticket(string key, string label = "backend", bool mine = true, bool inIntake = true,
        string category = "todo", string? parent = null, params string[] children) => new(
        key, key, $"https://acme.atlassian.net/browse/{key}", null, $"h-{key}", Body($"Work {key}", label), [],
        Status: category switch { "done" => "Done", "inprogress" => "In Progress", _ => "To Do" },
        StatusCategory: category, AssignedToMe: mine, InIntake: inIntake,
        ParentKey: parent, ParentType: parent is null ? null : "Story",
        ChildKeys: children.Length == 0 ? null : children);

    /// <summary>A poll with everything intake has to decide: plain tasks routed by label, a Story
    /// whose sub-tasks are partly mine / someone else's / under way / done, a ticket of mine that is
    /// already in progress, one that is not mine, and a Story with no sub-task left to do.</summary>
    private static TrackerTicket[] MixedReport() =>
    [
        Ticket("SHOP-1", "backend"),
        Ticket("SHOP-2", "frontend"),
        Ticket("SHOP-3", "unrouted"),
        // A sub-task listed before its Story.
        Ticket("SHOP-12", "frontend", inIntake: false, parent: "SHOP-10"),
        Ticket("SHOP-10", "backend", children: ["SHOP-11", "SHOP-12", "SHOP-13", "SHOP-14", "SHOP-15"]),
        Ticket("SHOP-11", "backend", inIntake: false, parent: "SHOP-10"),
        Ticket("SHOP-13", "backend", mine: false, inIntake: false, parent: "SHOP-10"),
        Ticket("SHOP-14", "backend", inIntake: false, category: "inprogress", parent: "SHOP-10"),
        Ticket("SHOP-15", "backend", inIntake: false, category: "done", parent: "SHOP-10"),
        Ticket("SHOP-20", "backend", children: ["SHOP-21"]),
        Ticket("SHOP-21", "backend", inIntake: false, category: "done", parent: "SHOP-20"),
        Ticket("SHOP-30", "backend", inIntake: false, category: "inprogress"),
        Ticket("SHOP-40", "backend", mine: false, inIntake: false),
    ];

    // ── The Start gate ──────────────────────────────────────────────────────

    [Fact]
    public async Task BeforeStart_AReport_IsRejected_AndNothingIsImported()
    {
        using var h = new Harness();
        h.Pending.Add(ProjectId);

        var ack = await h.ReportAsync(MixedReport());

        ack.Accepted.Should().BeFalse();
        ack.Error.Should().Contain("press Start");
        h.CreatedTasks.Should().BeEmpty();
        h.CreatedStories.Should().BeEmpty();
        (await h.Db.TicketLinks.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await h.Db.ProjectConfigs.IgnoreQueryFilters().SingleAsync()).LastPolledAt.Should().BeNull(
            "a refused report is not a poll");
    }

    [Fact]
    public async Task AfterStart_TheSameReport_IsImported()
    {
        using var h = new Harness();
        h.Pending.Add(ProjectId);
        (await h.ReportAsync(MixedReport())).Accepted.Should().BeFalse();

        h.Pending.Clear(); // Start
        (await h.ReportAsync(MixedReport())).Accepted.Should().BeTrue();

        h.CreatedTasks.Should().NotBeEmpty();
        h.CreatedStories.Should().ContainSingle();
    }

    // ── Dry run parity ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThePreview_ListsExactlyWhatThePollCreates_AndCreatesNothing(bool storyMode)
    {
        var report = MixedReport();

        using var real = new Harness(storyMode);
        (await real.ReportAsync(report)).Accepted.Should().BeTrue();

        using var dry = new Harness(storyMode);
        dry.Pending.Add(ProjectId); // the preview is for a project not started yet
        var preview = await dry.Api.PreviewIntakeAsync(ProjectId, report);

        preview.Should().NotBeNull();
        // The same plain tasks, in the same order, on the same repositories.
        preview!.Tasks.Select(t => (t.Key, t.RepoId)).Should().Equal(
            real.CreatedTasks.Select(s => (s.ExternalKey, s.RepoId)));
        // The same Stories with the same members, in tracker order, on the same branch.
        preview.Stories.Select(s => (s.Story.Key, s.Branch)).Should().Equal(
            real.CreatedStories.Select(s => (s.StoryKey, s.BranchName)));
        preview.Stories.Select(s => s.Members.Select(m => (m.Key, m.RepoId)).ToArray()).Should().BeEquivalentTo(
            real.CreatedStories.Select(s => s.SubTasks.Select(m => (m.Key, (Ulid?)m.RepoIds.FirstOrDefault())).ToArray()),
            o => o.WithStrictOrdering());
        preview.NewTaskCount.Should().Be(real.CreatedTasks.Count + real.CreatedStories.Sum(s => s.SubTasks.Count));

        // Nothing created, linked, submitted or asked for the dry run.
        dry.Tasks.Verify(t => t.CreateTrackerTaskAsync(It.IsAny<TrackerTaskSpec>(), It.IsAny<CancellationToken>()), Times.Never);
        dry.Tasks.Verify(t => t.CreateTrackerStoryAsync(It.IsAny<TrackerStorySpec>(), It.IsAny<CancellationToken>()), Times.Never);
        dry.Tasks.Verify(t => t.SubmitTaskAsync(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        (await dry.Db.TicketLinks.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        var config = await dry.Db.ProjectConfigs.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        config.LastPolledAt.Should().BeNull();
    }

    [Fact]
    public async Task ThePreview_ExplainsWhatItLeavesOut_AndNeverAsksTheRepoRouter()
    {
        using var h = new Harness();
        var preview = (await h.Api.PreviewIntakeAsync(ProjectId, MixedReport()))!;

        preview.Stories.Should().ContainSingle().Which.Members.Select(m => m.Key).Should().Equal("SHOP-11", "SHOP-12");
        preview.Tasks.Select(t => t.Key).Should().Equal("SHOP-1", "SHOP-2", "SHOP-3", "SHOP-20");
        preview.Tasks.Single(t => t.Key == "SHOP-1").Should().Match<TrackerPreviewTicket>(t =>
            t.RepoName == "shop-api" && t.Route == "rule:Label" && t.Assignee == "Dana");
        preview.Tasks.Single(t => t.Key == "SHOP-2").RepoName.Should().Be("shop-web");
        var unrouted = preview.Tasks.Single(t => t.Key == "SHOP-3");
        unrouted.RepoId.Should().BeNull();
        unrouted.Route.Should().Be("router-at-import");
        preview.Skipped.Select(s => (s.Key, s.Reason)).Should().BeEquivalentTo(new[]
        {
            ("SHOP-13", TrackerPreviewSkipReasons.NotAssignedToMe),
            ("SHOP-14", TrackerPreviewSkipReasons.NotInIntake),
            ("SHOP-15", TrackerPreviewSkipReasons.NotInIntake),
            ("SHOP-21", TrackerPreviewSkipReasons.NotInIntake),
            ("SHOP-30", TrackerPreviewSkipReasons.NotInIntake),
            ("SHOP-40", TrackerPreviewSkipReasons.NotAssignedToMe),
        });
        h.Router.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ThePreview_CountsTicketsTheBoardAlreadyHas_AsTracked_NotNew()
    {
        using var h = new Harness();
        h.Db.TicketLinks.Add(new TicketLink
        {
            Id = Ulid.NewUlid(), OrgId = OrgId, ConfigId = h.Config.Id, ProjectId = ProjectId, TaskId = Ulid.NewUlid(),
            Kind = TrackerKinds.Jira, SiteUrl = "https://acme.atlassian.net", TicketKey = "SHOP-1", TicketId = "SHOP-1",
            TicketUrl = "https://acme.atlassian.net/browse/SHOP-1",
        });
        await h.Db.SaveChangesAsync();

        var preview = (await h.Api.PreviewIntakeAsync(ProjectId, MixedReport()))!;

        preview.AlreadyTracked.Select(t => t.Key).Should().Equal("SHOP-1");
        preview.Tasks.Select(t => t.Key).Should().NotContain("SHOP-1");
    }
}
