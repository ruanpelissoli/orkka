namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Trackers.Domain;
using Orkka.Modules.Trackers.Features.Connections;
using Orkka.Modules.Trackers.Features.ProjectConfig;
using Orkka.Modules.Trackers.Infrastructure;

/// <summary>
/// A project's ticket source, end to end: the caller's connections, saving a source onto one
/// of them (and only one of their own), reading it back, and removing it. Tracker queries need
/// the caller's runner; with none connected they answer "not supported", never an error.
/// </summary>
[Collection("Integration")]
public sealed class ProjectTrackerEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<TrackerConnection> SeedConnectionAsync(string userId, string key)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackersDbContext>();
        var connection = new TrackerConnection
        {
            Id = Ulid.NewUlid(),
            OrgId = TestCurrentUser.TestOrgId,
            UserId = userId,
            ConnectionKey = key,
            Kind = TrackerKinds.Jira,
            SiteUrl = "https://acme.atlassian.net",
            DisplayName = "Dana",
            Healthy = true,
            MachineName = "dana-laptop",
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        db.Connections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private async Task<(Ulid ProjectId, Ulid RepoId)> SeedProjectAndRepoAsync()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Ticket Source Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("shop-api", "git@github.com:acme/shop-api.git", "main", null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);
        return (project.Id, repo!.Id);
    }

    [Fact]
    public async Task ListConnections_ShowsOnlyTheCallersOwn()
    {
        var mine = await SeedConnectionAsync(TestCurrentUser.TestUserId, $"jira-mine-{Ulid.NewUlid()}");
        var theirs = await SeedConnectionAsync("user_someone_else", $"jira-theirs-{Ulid.NewUlid()}");

        var list = await _client.GetFromJsonAsync<TrackerConnectionResponse[]>(
            "/api/trackers/connections", TestJsonOptions.Default);

        list!.Select(c => c.Id).Should().Contain(mine.Id).And.NotContain(theirs.Id);
    }

    [Fact]
    public async Task Save_Get_Delete_RoundTrips()
    {
        var connection = await SeedConnectionAsync(TestCurrentUser.TestUserId, $"jira-{Ulid.NewUlid()}");
        var (projectId, repoId) = await SeedProjectAndRepoAsync();

        (await _client.GetAsync($"/api/projects/{projectId}/tracker")).StatusCode
            .Should().Be(HttpStatusCode.NoContent, "a project starts with no ticket source");

        var save = await _client.PutAsJsonAsync($"/api/projects/{projectId}/tracker",
            new SaveProjectTrackerRequest(
                connection.Id, "SHOP",
                [new TrackerEpicMappingDto("SHOP-12", "Checkout", repoId, @"packages\api\")],
                ExtraClause: "labels = backend",
                StatusMap: new TrackerStatusMapDto(Planning: "In Progress", PrOpened: "In Review")));
        save.StatusCode.Should().Be(HttpStatusCode.OK, await save.Content.ReadAsStringAsync());

        var read = await _client.GetFromJsonAsync<ProjectTrackerConfigResponse>(
            $"/api/projects/{projectId}/tracker", TestJsonOptions.Default);
        read!.Scope.Should().Be("SHOP");
        read.OwnerUserId.Should().Be(TestCurrentUser.TestUserId);
        read.Connection!.Key.Should().Be(connection.ConnectionKey);
        read.ExtraClause.Should().Be("labels = backend");
        read.AutoSubmit.Should().BeTrue();
        read.PollIntervalSeconds.Should().Be(ProjectTrackerConfig.DefaultPollIntervalSeconds);
        read.BranchTemplate.Should().Be(ProjectTrackerConfig.DefaultBranchTemplate);
        read.StatusMap.Should().Be(new TrackerStatusMapDto(Planning: "In Progress", PrOpened: "In Review"));
        read.Epics.Should().ContainSingle()
            .Which.Should().Be(new TrackerEpicMappingDto("SHOP-12", "Checkout", repoId, "packages/api"));

        (await _client.DeleteAsync($"/api/projects/{projectId}/tracker")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        (await _client.GetAsync($"/api/projects/{projectId}/tracker")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeletingTheProject_RemovesItsTicketSource_SoNothingPollsIt()
    {
        // R5 e2e run: the runner kept polling the tracker of a project deleted minutes before.
        var connection = await SeedConnectionAsync(TestCurrentUser.TestUserId, $"jira-{Ulid.NewUlid()}");
        var (projectId, repoId) = await SeedProjectAndRepoAsync();
        var (otherProjectId, otherRepoId) = await SeedProjectAndRepoAsync();
        foreach (var (pid, rid) in new[] { (projectId, repoId), (otherProjectId, otherRepoId) })
        {
            var save = await _client.PutAsJsonAsync($"/api/projects/{pid}/tracker",
                new SaveProjectTrackerRequest(connection.Id, "SHOP",
                    [new TrackerEpicMappingDto("SHOP-12", "Checkout", rid, null)]));
            save.StatusCode.Should().Be(HttpStatusCode.OK, await save.Content.ReadAsStringAsync());
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TrackersDbContext>();
            var config = await db.ProjectConfigs.SingleAsync(c => c.ProjectId == projectId);
            db.TicketLinks.Add(new TicketLink
            {
                Id = Ulid.NewUlid(), OrgId = TestCurrentUser.TestOrgId, ConfigId = config.Id, ProjectId = projectId,
                TaskId = Ulid.NewUlid(), Kind = TrackerKinds.Jira, SiteUrl = "https://acme.atlassian.net",
                TicketKey = "SHOP-1", TicketId = "10001", TicketUrl = "https://acme.atlassian.net/browse/SHOP-1",
            });
            await db.SaveChangesAsync();
        }

        (await _client.DeleteAsync($"/api/projects/{projectId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // ProjectDeleted is dispatched in the background (a channel drained by
        // EventDispatcherService, in a scope with no tenant): wait for it, bounded.
        for (var i = 0; i < 100; i++)
        {
            using var poll = factory.Services.CreateScope();
            if (!await poll.ServiceProvider.GetRequiredService<TrackersDbContext>().ProjectConfigs.AnyAsync(c => c.ProjectId == projectId))
                break;
            await Task.Delay(100);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TrackersDbContext>();
            (await db.ProjectConfigs.AnyAsync(c => c.ProjectId == projectId)).Should().BeFalse("a deleted project has no ticket source");
            (await db.TicketLinks.AnyAsync(l => l.ProjectId == projectId)).Should().BeFalse();
            var otherConfig = await db.ProjectConfigs.Include(c => c.Epics).SingleAsync(c => c.ProjectId == otherProjectId);
            otherConfig.Epics.Should().ContainSingle("another project's source is untouched");
            (await db.EpicMappings.CountAsync(m => m.ConfigId == otherConfig.Id)).Should().Be(1);
            (await db.EpicMappings.AnyAsync(m => !db.ProjectConfigs.Select(c => c.Id).Contains(m.ConfigId)))
                .Should().BeFalse("the deleted source's epic rules went with it");
        }
    }

    [Fact]
    public async Task Save_OnSomeoneElsesConnection_IsRefused()
    {
        var theirs = await SeedConnectionAsync("user_someone_else", $"jira-{Ulid.NewUlid()}");
        var (projectId, repoId) = await SeedProjectAndRepoAsync();

        var save = await _client.PutAsJsonAsync($"/api/projects/{projectId}/tracker",
            new SaveProjectTrackerRequest(theirs.Id, "SHOP",
                [new TrackerEpicMappingDto("SHOP-12", null, repoId, null)]));

        save.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await save.Content.ReadAsStringAsync()).Should().Contain("./orkka tracker add");
    }

    [Fact]
    public async Task Save_MappingAnEpicToAnotherProjectsRepo_IsRefused()
    {
        var connection = await SeedConnectionAsync(TestCurrentUser.TestUserId, $"jira-{Ulid.NewUlid()}");
        var (projectId, _) = await SeedProjectAndRepoAsync();
        var (_, otherRepo) = await SeedProjectAndRepoAsync();

        var save = await _client.PutAsJsonAsync($"/api/projects/{projectId}/tracker",
            new SaveProjectTrackerRequest(connection.Id, "SHOP",
                [new TrackerEpicMappingDto("SHOP-12", null, otherRepo, null)]));

        save.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await save.Content.ReadAsStringAsync()).Should().Contain("not in this project");
    }

    [Fact]
    public async Task Query_WithNoRunnerConnected_IsNotSupported_NotAnError()
    {
        var connection = await SeedConnectionAsync(TestCurrentUser.TestUserId, $"jira-{Ulid.NewUlid()}");

        var response = await _client.PostAsJsonAsync(
            $"/api/trackers/connections/{connection.Id}/query", new TrackerQueryBody(TrackerQueryKinds.Test));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<TrackerQueryResponse>(TestJsonOptions.Default);
        body!.Supported.Should().BeFalse();
    }

    [Fact]
    public async Task Query_OnSomeoneElsesConnection_IsNotFound()
    {
        var theirs = await SeedConnectionAsync("user_someone_else", $"jira-{Ulid.NewUlid()}");

        var response = await _client.PostAsJsonAsync(
            $"/api/trackers/connections/{theirs.Id}/query", new TrackerQueryBody(TrackerQueryKinds.Scopes));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Intake v2: no epics, routing rules ──────────────────────────────────

    [Fact]
    public async Task Save_AllMyTickets_WithRoutingRules_RoundTrips()
    {
        var connection = await SeedConnectionAsync(TestCurrentUser.TestUserId, $"jira-{Ulid.NewUlid()}");
        var (projectId, repoId) = await SeedProjectAndRepoAsync();

        var save = await _client.PutAsJsonAsync($"/api/projects/{projectId}/tracker",
            new SaveProjectTrackerRequest(connection.Id, "SHOP",
                ScopeMode: "All",
                IntakeStatuses: ["To Do", "Selected for Development"],
                RoutingRules:
                [
                    new TrackerRoutingRuleDto("label", "backend", repoId, null),
                    new TrackerRoutingRuleDto("component", "Payments", repoId, "packages/pay"),
                ],
                DefaultRepoId: repoId));
        save.StatusCode.Should().Be(HttpStatusCode.OK, await save.Content.ReadAsStringAsync());

        var read = await _client.GetFromJsonAsync<ProjectTrackerConfigResponse>(
            $"/api/projects/{projectId}/tracker", TestJsonOptions.Default);
        read!.ScopeMode.Should().Be("All");
        read.Epics.Should().BeEmpty();
        read.IntakeStatuses.Should().Equal("To Do", "Selected for Development");
        read.RoutingRules.Should().Equal(
            new TrackerRoutingRuleDto("label", "backend", repoId, null),
            new TrackerRoutingRuleDto("component", "Payments", repoId, "packages/pay"));
        read.DefaultRepoId.Should().Be(repoId);
        read.StoryMode.Should().Be(1);
    }

    [Fact]
    public async Task Save_InEpicsModeWithoutEpics_IsRefused()
    {
        var connection = await SeedConnectionAsync(TestCurrentUser.TestUserId, $"jira-{Ulid.NewUlid()}");
        var (projectId, _) = await SeedProjectAndRepoAsync();

        var save = await _client.PutAsJsonAsync($"/api/projects/{projectId}/tracker",
            new SaveProjectTrackerRequest(connection.Id, "SHOP", ScopeMode: "Epics"));

        save.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Connections from the settings page ──────────────────────────────────

    private const string Token = "ATATT3xFfGF0-from-the-settings-page";

    [Fact]
    public async Task AddConnection_WithNoRunnerConnected_IsNotSupported_AndTheTokenIsNotEchoed()
    {
        var response = await _client.PostAsJsonAsync("/api/trackers/connections",
            new AddTrackerConnectionBody("jira", "acme.atlassian.net", "dana@acme.com", Token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain(Token);
        var body = await response.Content.ReadFromJsonAsync<AddTrackerConnectionResponse>(TestJsonOptions.Default);
        body!.Supported.Should().BeFalse();
        body.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task AddConnection_Invalid_IsA400_ThatDoesNotQuoteTheToken()
    {
        var response = await _client.PostAsJsonAsync("/api/trackers/connections",
            new AddTrackerConnectionBody("github", "acme", null, Token));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(Token).And.Contain("Jira");
    }

    [Fact]
    public async Task RemoveConnection_SomeoneElses_IsNotFound_AndOwnWithNoRunner_IsNotSupported()
    {
        var theirs = await SeedConnectionAsync("user_someone_else", $"jira-{Ulid.NewUlid()}");
        var mine = await SeedConnectionAsync(TestCurrentUser.TestUserId, $"jira-{Ulid.NewUlid()}");

        (await _client.DeleteAsync($"/api/trackers/connections/{theirs.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var response = await _client.DeleteAsync($"/api/trackers/connections/{mine.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RemoveTrackerConnectionResponse>(TestJsonOptions.Default))!
            .Supported.Should().BeFalse();
        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<TrackersDbContext>().Connections.IgnoreQueryFilters()
            .AnyAsync(c => c.Id == mine.Id)).Should().BeTrue("nothing was removed on the machine");
    }
}
