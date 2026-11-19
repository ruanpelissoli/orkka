namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Runners.Features.Presence;
using Orkka.Modules.Trackers.Contracts;
using Orkka.Modules.Trackers.Domain;
using Orkka.Modules.Trackers.Features.ProjectConfig;
using Orkka.Modules.Trackers.Infrastructure;

/// <summary>
/// The guided project setup's endpoints: a new project starts with its setup open (no step
/// confirmed, not started); steps are confirmed one by one; Start opens intake — before it the
/// runner is handed nothing to poll for the project, after it the source is due like any other —
/// and is idempotent. The dry run and the command check answer "your runner is offline" rather
/// than failing when no runner is connected, and the dry run needs a saved source.
/// </summary>
[Collection("Integration")]
public sealed class ProjectSetupEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<ProjectResponse> CreateProjectAsync(string name = "Setup Project")
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name, "desc"));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default))!;
    }

    private async Task<string> SeedSourceAsync(Ulid projectId)
    {
        var key = $"jira-{Ulid.NewUlid()}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackersDbContext>();
        var connection = new TrackerConnection
        {
            Id = Ulid.NewUlid(), OrgId = TestCurrentUser.TestOrgId, UserId = TestCurrentUser.TestUserId,
            ConnectionKey = key, Kind = TrackerKinds.Jira, SiteUrl = "https://acme.atlassian.net",
            MachineName = "dana-laptop", Healthy = true, LastSeenAt = DateTimeOffset.UtcNow,
        };
        db.Connections.Add(connection);
        db.ProjectConfigs.Add(new ProjectTrackerConfig
        {
            Id = Ulid.NewUlid(), OrgId = TestCurrentUser.TestOrgId, ProjectId = projectId,
            OwnerUserId = TestCurrentUser.TestUserId, TrackerConnectionId = connection.Id, Scope = "SHOP",
        });
        await db.SaveChangesAsync();
        return key;
    }

    private async Task<TrackerSyncResponse> SyncAsync(string connectionKey)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ITrackersApi>().SyncAsync(
            TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
            new TrackerSyncRequest([connectionKey], $"runner-{Ulid.NewUlid()}"), supersedesOtherInstances: true);
    }

    [Fact]
    public async Task ANewProject_StartsWithItsSetupOpen()
    {
        var project = await CreateProjectAsync();

        project.SetupCompletedAt.Should().BeNull();
        project.SetupSteps.Should().BeEmpty();

        var fetched = await _client.GetFromJsonAsync<ProjectResponse>($"/api/projects/{project.Id}", TestJsonOptions.Default);
        fetched!.SetupCompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Steps_AreConfirmedAndTakenBack_InSetupOrder_AndUnknownStepsAreRefused()
    {
        var project = await CreateProjectAsync();

        await _client.PutAsJsonAsync($"/api/projects/{project.Slug}/setup/steps", new ProjectSetupStepRequest("source"));
        var response = await _client.PutAsJsonAsync($"/api/projects/{project.Id}/setup/steps", new ProjectSetupStepRequest("folder"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default))!.SetupSteps
            .Should().Equal("folder", "source");

        response = await _client.PutAsJsonAsync($"/api/projects/{project.Id}/setup/steps", new ProjectSetupStepRequest("source", Done: false));
        (await response.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default))!.SetupSteps.Should().Equal("folder");

        (await _client.PutAsJsonAsync($"/api/projects/{project.Id}/setup/steps", new ProjectSetupStepRequest("review")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "Review is finished by Start, not confirmed");
        (await _client.PutAsJsonAsync($"/api/projects/{Ulid.NewUlid()}/setup/steps", new ProjectSetupStepRequest("folder")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BeforeStart_TheSourceIsNotPolled_AfterStart_ItIs()
    {
        var project = await CreateProjectAsync();
        var key = await SeedSourceAsync(project.Id);

        (await SyncAsync(key)).Assignments.Should().BeEmpty("nothing is pulled before the developer presses Start");

        var started = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/setup/start",
            new StartProjectSetupRequest(["Claude is not signed in"]));
        started.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await started.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default))!;
        body.SetupCompletedAt.Should().NotBeNull();
        body.SetupSteps.Should().Equal("folder", "machine", "source", "team");

        (await SyncAsync(key)).Assignments.Should().ContainSingle().Which.Scope.Should().Be("SHOP");

        using var scope = factory.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<AgentsDbContext>().AgentAuditEvents.IgnoreQueryFilters()
            .Where(e => e.ProjectId == project.Id && e.EventType == AgentAuditEventType.ProjectSettingsChanged)
            .ToListAsync();
        audit.Should().ContainSingle().Which.Summary.Should().Contain("started anyway with 1 warning");
    }

    [Fact]
    public async Task Start_IsIdempotent_TheFirstStartTimeStays()
    {
        var project = await CreateProjectAsync();
        var first = await (await _client.PostAsJsonAsync($"/api/projects/{project.Id}/setup/start", new StartProjectSetupRequest()))
            .Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        var second = await (await _client.PostAsJsonAsync($"/api/projects/{project.Id}/setup/start", new StartProjectSetupRequest()))
            .Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        second!.SetupCompletedAt.Should().Be(first!.SetupCompletedAt);
    }

    [Fact]
    public async Task ThePreview_NeedsASavedSource_AndSaysTheRunnerIsOffline_WhenNoneIsConnected()
    {
        var project = await CreateProjectAsync();

        (await _client.PostAsync($"/api/projects/{project.Id}/tracker/preview", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        await SeedSourceAsync(project.Id);
        var response = await _client.PostAsync($"/api/projects/{project.Id}/tracker/preview", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await response.Content.ReadFromJsonAsync<TrackerIntakePreviewResponse>(TestJsonOptions.Default);
        preview!.Supported.Should().BeFalse();
        preview.Preview.Should().BeNull();
    }

    [Fact]
    public async Task TheCommandCheck_HasNothingToAsk_WithoutCommands_AndSaysTheRunnerIsOffline_WithThem()
    {
        var project = await CreateProjectAsync();
        var repoResponse = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/repos",
            new AddProjectRepoRequest("shop", "git@github.com:acme/shop.git", "main", null));
        var repo = (await repoResponse.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default))!;

        var empty = await (await _client.PostAsync($"/api/projects/{project.Id}/setup/commands", null))
            .Content.ReadFromJsonAsync<RepoCommandsCheckResponse>(TestJsonOptions.Default);
        empty!.Supported.Should().BeTrue();
        empty.Repos.Should().ContainSingle().Which.Commands.Should().BeEmpty("orkka.json commands need no approval");

        await _client.PatchAsJsonAsync($"/api/projects/{project.Id}/repos/{repo.Id}",
            new UpdateProjectRepoRequest("main", SetupCommand: "npm ci", CheckCommand: "npm test"));
        var offline = await (await _client.PostAsync($"/api/projects/{project.Id}/setup/commands", null))
            .Content.ReadFromJsonAsync<RepoCommandsCheckResponse>(TestJsonOptions.Default);
        offline!.Supported.Should().BeFalse();
    }
}
