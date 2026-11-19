namespace Orkka.Tests.Services;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.LocalRunner.Contracts;
using Orkka.LocalRunner.Trackers;

/// <summary>
/// The setup page's dry run on the runner: the real poller reads the same tickets the real poll
/// would and normalizes them the same way, but downloads no attachment, fetches no comment and
/// remembers nothing — and the tracker query hands the tickets back instead of reporting them.
/// </summary>
public sealed class TrackerDryRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkka-dryrun-" + Guid.NewGuid().ToString("N"));
    private readonly TrackerStore _store;
    private readonly FileSecretStore _secrets;
    private readonly Mock<ITrackerTicketSource> _source = new(MockBehavior.Strict);
    private readonly CountingHandler _downloads = new();
    private readonly TrackerTicketPoller _poller;

    public TrackerDryRunTests()
    {
        _store = new TrackerStore(_root);
        _store.Upsert(new TrackerConnectionEntry
        {
            Id = "jira-acme", Kind = TrackerKinds.Jira, SiteUrl = "https://acme.atlassian.net", Email = "dana@acme.com",
        });
        _secrets = new FileSecretStore(TrackerPaths.SecretsDir(_root));
        _secrets.Set("jira-acme", "jira-token");
        _poller = new TrackerTicketPoller(_store, _secrets, _ => _source.Object,
            new TrackerAttachmentCache(TrackerPaths.TicketsDir(_root), new HttpClient(_downloads),
                NullLogger<TrackerAttachmentCache>.Instance),
            NullLogger<TrackerTicketPoller>.Instance);

        _source.Setup(s => s.FetchTicketsAsync(It.IsAny<TrackerCredential>(), It.IsAny<TrackerPollAssignment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new RawTicket("SHOP-1", "10001", "https://acme.atlassian.net/browse/SHOP-1", null, "Export invoices", "Story",
                    "To Do", "todo", "High", "<p>As CSV.</p>", null, null, ["backend"],
                    [new RawAttachment("a1", "mock.png", 1200, "image/png", "https://acme.atlassian.net/rest/api/3/attachment/content/a1")],
                    DateTimeOffset.UtcNow)
                {
                    AssignedToMe = true, InIntake = true,
                },
            ]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static TrackerPollAssignment Assignment() =>
        new("cfg-1", "jira-acme", TrackerKinds.Jira, "SHOP", [], null, false, [], IntakeStatuses: ["To Do"]);

    [Fact]
    public async Task ADryRun_ReadsAndNormalizesLikeThePoll_ButDownloadsAndFetchesNothing_AndRemembersNothing()
    {
        var real = await _poller.PollAsync(Assignment(), "i", CancellationToken.None, dryRun: true);

        real.Report.Ok.Should().BeTrue();
        var ticket = real.Report.Tickets.Should().ContainSingle().Subject;
        ticket.InIntake.Should().BeTrue();
        ticket.Body!.Title.Should().Be("Export invoices");
        ticket.Body.Attachments.Should().ContainSingle().Which.Should().Be(new TrackerAttachmentMeta("mock.png", 1200, "image/png", Cached: false));
        ticket.NewComments.Should().BeEmpty();
        _downloads.Calls.Should().Be(0, "a dry run never downloads an attachment");
        _source.Verify(s => s.FetchCommentsAsync(It.IsAny<TrackerCredential>(), It.IsAny<RawTicket>(),
            It.IsAny<DateTimeOffset?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        Directory.Exists(TrackerPaths.TicketsDir(_root)).Should().BeFalse("nothing is cached on disk");
    }

    [Fact]
    public async Task TheDryRunQuery_ReturnsTheTickets_ThroughTheRealPoller()
    {
        var service = new TrackerService(_store, _secrets, new JiraCloudClient(new HttpClient(_downloads)),
            new AzureDevOpsClient(new HttpClient(_downloads)), NullLogger<TrackerService>.Instance) { Poller = _poller };

        var result = await service.QueryAsync(
            new TrackerQueryRequest("jira-acme", TrackerQueryKinds.DryRun, Assignment: Assignment()), CancellationToken.None);

        result.Ok.Should().BeTrue(result.Error);
        result.Tickets.Should().ContainSingle().Which.Key.Should().Be("SHOP-1");
        result.MatchCount.Should().Be(1);
    }

    [Fact]
    public async Task TheDryRunQuery_RefusesAnAssignmentForAnotherConnection()
    {
        var service = new TrackerService(_store, _secrets, new JiraCloudClient(new HttpClient(_downloads)),
            new AzureDevOpsClient(new HttpClient(_downloads)), NullLogger<TrackerService>.Instance) { Poller = _poller };

        var result = await service.QueryAsync(
            new TrackerQueryRequest("jira-acme", TrackerQueryKinds.DryRun, Assignment: Assignment() with { ConnectionId = "jira-other" }),
            CancellationToken.None);

        result.Ok.Should().BeFalse();
        _source.VerifyNoOtherCalls();
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
