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
/// A ticket source limited to one board (a Jira board, an Azure DevOps team): saved and read
/// back, cleared, handed to the polling runner — and never handed to a runner too old to honour
/// it, which would pick up the whole project instead.
/// </summary>
[Collection("Integration")]
public sealed class TrackerBoardEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(Ulid ProjectId, TrackerConnection Connection)> SeedAsync()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Board Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        await _client.StartSetupAsync(project!.Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackersDbContext>();
        var connection = new TrackerConnection
        {
            Id = Ulid.NewUlid(), OrgId = TestCurrentUser.TestOrgId, UserId = TestCurrentUser.TestUserId,
            ConnectionKey = $"ado-{Ulid.NewUlid()}", Kind = TrackerKinds.AzureDevOps, SiteUrl = "https://dev.azure.com/contoso",
            MachineName = "dana-laptop", Healthy = true, LastSeenAt = DateTimeOffset.UtcNow,
        };
        db.Connections.Add(connection);
        await db.SaveChangesAsync();
        return (project!.Id, connection);
    }

    private Task<HttpResponseMessage> SaveAsync(Ulid projectId, Ulid connectionId, string? boardId, string? boardName)
        => _client.PutAsJsonAsync($"/api/projects/{projectId}/tracker",
            new SaveProjectTrackerRequest(connectionId, "Shop", BoardId: boardId, BoardName: boardName));

    [Fact]
    public async Task Save_WithABoard_RoundTrips_AndClearingItMeansTheWholeProject()
    {
        var (projectId, connection) = await SeedAsync();

        var save = await SaveAsync(projectId, connection.Id, " team-web-0001 ", " Web ");
        save.StatusCode.Should().Be(HttpStatusCode.OK, await save.Content.ReadAsStringAsync());
        var read = await _client.GetFromJsonAsync<ProjectTrackerConfigResponse>(
            $"/api/projects/{projectId}/tracker", TestJsonOptions.Default);
        read!.BoardId.Should().Be("team-web-0001");
        read.BoardName.Should().Be("Web");

        (await SaveAsync(projectId, connection.Id, "team-api-0002", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        read = await _client.GetFromJsonAsync<ProjectTrackerConfigResponse>(
            $"/api/projects/{projectId}/tracker", TestJsonOptions.Default);
        read!.BoardName.Should().Be("team-api-0002", "a board saved without a name is shown by its id");

        (await SaveAsync(projectId, connection.Id, "", "Web")).StatusCode.Should().Be(HttpStatusCode.OK);
        read = await _client.GetFromJsonAsync<ProjectTrackerConfigResponse>(
            $"/api/projects/{projectId}/tracker", TestJsonOptions.Default);
        read!.BoardId.Should().BeNull();
        read.BoardName.Should().BeNull("a name without a board means nothing");
    }

    [Fact]
    public async Task Save_ABoardIdTooLong_IsRefused()
    {
        var (projectId, connection) = await SeedAsync();

        var save = await SaveAsync(projectId, connection.Id, new string('x', 129), "Web");

        save.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Poll_HandsTheBoardToTheRunner_ButNotToOneThatPredatesBoards()
    {
        var (projectId, connection) = await SeedAsync();
        (await SaveAsync(projectId, connection.Id, "team-web-0001", "Web")).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = factory.Services.CreateScope())
        {
            var trackers = scope.ServiceProvider.GetRequiredService<ITrackersApi>();
            var old = await trackers.SyncAsync(TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
                new TrackerSyncRequest([connection.ConnectionKey], "old-runner"));
            old.Assignments.Should().BeEmpty("it would ignore the board and pick up the whole project");
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TrackersDbContext>();
            var config = await db.ProjectConfigs.AsNoTracking().SingleAsync(c => c.ProjectId == projectId);
            config.LastPollError.Should().Contain("too old to know boards");
            config.LeaseHolder.Should().BeNull("the old runner was not given the source");
            // Due again at once, as if never polled.
            await db.ProjectConfigs.Where(c => c.Id == config.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastPolledAt, (DateTimeOffset?)null));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var trackers = scope.ServiceProvider.GetRequiredService<ITrackersApi>();
            var current = await trackers.SyncAsync(TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
                new TrackerSyncRequest([connection.ConnectionKey], "new-runner", [TrackerSyncFeatures.Boards]));
            var assignment = current.Assignments.Should().ContainSingle().Subject;
            assignment.BoardId.Should().Be("team-web-0001");
            assignment.BoardName.Should().Be("Web");
            assignment.Scope.Should().Be("Shop");
        }
    }

    [Fact]
    public async Task Poll_WithoutABoard_IsUnchanged_ForAnyRunner()
    {
        var (projectId, connection) = await SeedAsync();
        (await SaveAsync(projectId, connection.Id, null, null)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var sync = await scope.ServiceProvider.GetRequiredService<ITrackersApi>().SyncAsync(
            TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
            new TrackerSyncRequest([connection.ConnectionKey], "old-runner"));

        var assignment = sync.Assignments.Should().ContainSingle().Subject;
        assignment.BoardId.Should().BeNull();
        assignment.BoardName.Should().BeNull();
    }

    [Fact]
    public async Task BoardsQuery_WithNoRunnerConnected_IsNotSupported_NotAnError()
    {
        var (_, connection) = await SeedAsync();

        var response = await _client.PostAsJsonAsync($"/api/trackers/connections/{connection.Id}/query",
            new TrackerQueryBody(TrackerQueryKinds.Boards, Scope: "Shop"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadFromJsonAsync<TrackerQueryResponse>(TestJsonOptions.Default))!.Supported.Should().BeFalse();
    }
}
