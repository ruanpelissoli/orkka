namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Trackers.Contracts;
using Orkka.Modules.Trackers.Domain;
using Orkka.Modules.Trackers.Features.Connections;
using Orkka.Modules.Trackers.Features.ProjectConfig;
using Orkka.Modules.Trackers.Infrastructure;

/// <summary>
/// A GitHub Projects ticket source: one project (required), its status field and repository
/// filter saved and read back, epics and non-label rules refused — and never handed to a runner
/// that predates GitHub, which would ignore the field and the filter.
/// </summary>
[Collection("Integration")]
public sealed class TrackerGitHubEndpointTests(OrkkaApiFactory factory)
{
    private const string ProjectNode = "PVT_kwDOAbCdEf";
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(Ulid ProjectId, TrackerConnection Connection)> SeedAsync()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("GitHub Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        await _client.StartSetupAsync(project!.Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackersDbContext>();
        var connection = new TrackerConnection
        {
            Id = Ulid.NewUlid(), OrgId = TestCurrentUser.TestOrgId, UserId = TestCurrentUser.TestUserId,
            ConnectionKey = $"github-{Ulid.NewUlid()}", Kind = TrackerKinds.GitHub, AuthMode = TrackerAuthModes.GhCli,
            SiteUrl = "https://github.com", MachineName = "dana-laptop", Healthy = true, LastSeenAt = DateTimeOffset.UtcNow,
        };
        db.Connections.Add(connection);
        await db.SaveChangesAsync();
        return (project!.Id, connection);
    }

    private Task<HttpResponseMessage> SaveAsync(Ulid projectId, SaveProjectTrackerRequest request)
        => _client.PutAsJsonAsync($"/api/projects/{projectId}/tracker", request);

    private static SaveProjectTrackerRequest GitHubSource(Ulid connectionId) => new(
        connectionId, "acme", IntakeStatuses: ["Todo", "Ready"],
        StatusMap: new TrackerStatusMapDto(Developing: "In Progress", InReview: "In Review", Merged: "Done"),
        BoardId: ProjectNode, BoardName: "Shop roadmap", StatusField: " Stage ",
        RepoFilter: ["acme/web", " acme/api ", "ACME/api"],
        RoutingRules: null, ExtraClause: "labels = x", IncludeDescendants: true);

    [Fact]
    public async Task Save_RoundTripsTheProjectFieldAndFilter_AndDropsWhatGitHubHasNot()
    {
        var (projectId, connection) = await SeedAsync();

        var save = await SaveAsync(projectId, GitHubSource(connection.Id));
        save.StatusCode.Should().Be(HttpStatusCode.OK, await save.Content.ReadAsStringAsync());

        var read = await _client.GetFromJsonAsync<ProjectTrackerConfigResponse>(
            $"/api/projects/{projectId}/tracker", TestJsonOptions.Default);
        read!.Connection!.Kind.Should().Be(TrackerKinds.GitHub);
        read.Connection.AuthMode.Should().Be(TrackerAuthModes.GhCli);
        read.Scope.Should().Be("acme");
        read.BoardId.Should().Be(ProjectNode);
        read.BoardName.Should().Be("Shop roadmap");
        read.StatusField.Should().Be("Stage");
        read.RepoFilter.Should().Equal("acme/api", "acme/web");
        read.IntakeStatuses.Should().Equal("Todo", "Ready");
        read.StatusMap.Merged.Should().Be("Done");
        read.ExtraClause.Should().BeNull("GitHub has no query language to add to");
        read.IncludeDescendants.Should().BeFalse();
    }

    [Fact]
    public async Task Save_WithoutAProject_IsRefused()
    {
        var (projectId, connection) = await SeedAsync();

        var save = await SaveAsync(projectId, GitHubSource(connection.Id) with { BoardId = null });

        save.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await save.Content.ReadAsStringAsync()).Should().Contain("Pick the GitHub project");
    }

    [Fact]
    public async Task Save_EpicsOrAComponentRule_AreRefused_ButALabelRuleIsFine()
    {
        var (projectId, connection) = await SeedAsync();

        var epics = await SaveAsync(projectId, GitHubSource(connection.Id) with
        {
            ScopeMode = "Epics", Epics = [new TrackerEpicMappingDto("acme/shop#1", null, null, null)],
        });
        epics.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await epics.Content.ReadAsStringAsync()).Should().Contain("no epics");

        var component = await SaveAsync(projectId, GitHubSource(connection.Id) with
        {
            RoutingRules = [new TrackerRoutingRuleDto("component", "Backend", Ulid.NewUlid(), null)],
        });
        component.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Save_ARepositoryFilterThatIsNotOwnerSlashName_IsRefused()
    {
        var (projectId, connection) = await SeedAsync();

        var save = await SaveAsync(projectId, GitHubSource(connection.Id) with { RepoFilter = ["https://github.com/acme/web"] });

        save.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Poll_HandsTheSourceOnlyToARunnerThatKnowsGitHub()
    {
        var (projectId, connection) = await SeedAsync();
        (await SaveAsync(projectId, GitHubSource(connection.Id))).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = factory.Services.CreateScope())
        {
            var trackers = scope.ServiceProvider.GetRequiredService<ITrackersApi>();
            var old = await trackers.SyncAsync(TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
                new TrackerSyncRequest([connection.ConnectionKey], "old-runner", [TrackerSyncFeatures.Boards]));
            old.Assignments.Should().BeEmpty("it would ignore the status field and the repository filter");
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TrackersDbContext>();
            var config = await db.ProjectConfigs.AsNoTracking().SingleAsync(c => c.ProjectId == projectId);
            config.LastPollError.Should().Contain("too old to know GitHub");
            config.LeaseHolder.Should().BeNull();
            await db.ProjectConfigs.Where(c => c.Id == config.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastPolledAt, (DateTimeOffset?)null));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var trackers = scope.ServiceProvider.GetRequiredService<ITrackersApi>();
            var current = await trackers.SyncAsync(TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
                new TrackerSyncRequest([connection.ConnectionKey], "new-runner", [TrackerSyncFeatures.Boards, TrackerSyncFeatures.GitHub]));
            var a = current.Assignments.Should().ContainSingle().Subject;
            a.Kind.Should().Be(TrackerKinds.GitHub);
            a.Scope.Should().Be("acme");
            a.BoardId.Should().Be(ProjectNode);
            a.StatusField.Should().Be("Stage");
            a.RepoFilter.Should().Equal("acme/api", "acme/web");
            a.IntakeStatuses.Should().Equal("Todo", "Ready");
            a.ExtraClause.Should().BeNull();
        }
    }

    [Fact]
    public async Task AddingAGitHubConnection_WithTheGhLogin_NeedsNoToken_AndWithNoRunnerIsNotSupported()
    {
        var gh = await _client.PostAsJsonAsync("/api/trackers/connections",
            new { kind = "github", site = "github.com", token = "", authMode = "gh" });
        gh.StatusCode.Should().Be(HttpStatusCode.OK, await gh.Content.ReadAsStringAsync());
        (await gh.Content.ReadFromJsonAsync<AddTrackerConnectionResponse>(TestJsonOptions.Default))!.Supported.Should().BeFalse();

        var tokenMissing = await _client.PostAsJsonAsync("/api/trackers/connections",
            new { kind = "github", site = "github.com", token = "", authMode = "token" });
        tokenMissing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await tokenMissing.Content.ReadAsStringAsync()).Should().Contain("Paste the token");

        var badMode = await _client.PostAsJsonAsync("/api/trackers/connections",
            new { kind = "github", site = "github.com", token = "x", authMode = "oauth" });
        badMode.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task FieldsAndReposQueries_WithNoRunnerConnected_AreNotSupported_NotErrors()
    {
        var (_, connection) = await SeedAsync();

        foreach (var kind in new[] { TrackerQueryKinds.Fields, TrackerQueryKinds.Repos })
        {
            var response = await _client.PostAsJsonAsync($"/api/trackers/connections/{connection.Id}/query",
                new TrackerQueryBody(kind, Scope: "acme", BoardId: ProjectNode, StatusField: "Status", RepoFilter: ["acme/web"]));

            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            (await response.Content.ReadFromJsonAsync<TrackerQueryResponse>(TestJsonOptions.Default))!.Supported.Should().BeFalse();
        }
    }
}
