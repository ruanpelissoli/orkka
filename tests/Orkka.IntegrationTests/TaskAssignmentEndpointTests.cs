namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Agents.Features.Agents;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Features.Tasks;
using FluentAssertions;

/// <summary>
/// Pinning a task to a dev agent and repo-scoping an agent — the two user-facing ways to
/// route work to a specific agent (e.g. the Game Dev specialist). Validation must reject
/// non-dev and inactive assignees because AgentAssigner honors AssignedAgentId verbatim.
/// </summary>
[Collection("Integration")]
public sealed class TaskAssignmentEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(Ulid ProjectId, Ulid RepoId)> SeedProjectAndRepo()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Assignment Test Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("game-repo", "https://github.com/test/game.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        return (project.Id, repo!.Id);
    }

    // Rune (GameDev) is a singleton specialist that ships with every project — there is no
    // add/enable flow, so pick it out of the seeded roster.
    private async Task<AgentResponse> GetGameDev(Ulid projectId) =>
        (await WaitForSeededAgents(projectId)).First(a => a.Role == "GameDev");

    // The default pod is seeded asynchronously on the project-created event — poll
    // briefly so tests that need a seeded agent don't race the handler.
    private async Task<List<AgentResponse>> WaitForSeededAgents(Ulid projectId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var agents = await _client.GetFromJsonAsync<List<AgentResponse>>(
                $"/api/projects/{projectId}/agents", TestJsonOptions.Default);
            if (agents is { Count: > 0 }) return agents;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Agents were never seeded for project {projectId}.");
    }

    [Fact]
    public async Task CreateTask_WithAssignedDevAgent_PersistsAssignment()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var gameDev = await GetGameDev(projectId);

        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(repoId, "Add player dash", "Dash ability", AssignedAgentId: gameDev.Id));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var task = await resp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        task!.AssignedAgentId.Should().Be(gameDev.Id);
    }

    [Fact]
    public async Task CreateTask_WithNonDevAgent_ReturnsBadRequest()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var pm = (await WaitForSeededAgents(projectId)).First(a => a.Role == "PM");

        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(repoId, "Add player dash", "Dash ability", AssignedAgentId: pm.Id));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("only dev agents");
    }

    [Fact]
    public async Task CreateTask_WithForeignAgent_ReturnsBadRequest()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();

        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(repoId, "Task", "Desc", AssignedAgentId: Ulid.NewUlid()));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateTask_PinAndClearAssignment_TriState()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var gameDev = await GetGameDev(projectId);

        var createResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(repoId, "Task", "Desc"));
        var task = await createResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);

        // Pin
        var pin = await _client.PatchAsJsonAsync($"/api/tasks/{task!.Id}",
            new UpdateTaskRequest(null, null, null, null, null, null, AssignedAgentId: gameDev.Id.ToString()));
        pin.StatusCode.Should().Be(HttpStatusCode.OK);
        (await pin.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!
            .AssignedAgentId.Should().Be(gameDev.Id);

        // Absent field = unchanged
        var noop = await _client.PatchAsJsonAsync($"/api/tasks/{task.Id}",
            new UpdateTaskRequest("New title", null, null, null, null, null));
        (await noop.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!
            .AssignedAgentId.Should().Be(gameDev.Id);

        // Empty string = clear back to auto-assign
        var clear = await _client.PatchAsJsonAsync($"/api/tasks/{task.Id}",
            new UpdateTaskRequest(null, null, null, null, null, null, AssignedAgentId: ""));
        (await clear.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!
            .AssignedAgentId.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAgent_RepoScope_SetSubsetAndClear()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var secondRepoResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new AddProjectRepoRequest("assets-repo", "https://github.com/test/assets.git", "main", null, null));
        var secondRepoId = (await secondRepoResp.Content
            .ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default))!.Id;

        var gameDev = await GetGameDev(projectId);
        gameDev.RepoIds.Should().BeEmpty();

        // Scope to a two-repo subset
        var scope = await _client.PutAsJsonAsync($"/api/projects/{projectId}/agents/{gameDev.Id}",
            new UpdateAgentRequest(null, null, null, null,
                RepoIds: [repoId.ToString(), secondRepoId.ToString()]));
        scope.StatusCode.Should().Be(HttpStatusCode.OK);
        (await scope.Content.ReadFromJsonAsync<AgentResponse>(TestJsonOptions.Default))!
            .RepoIds.Should().BeEquivalentTo([repoId, secondRepoId]);

        // Unrelated update leaves the scope alone
        var noop = await _client.PutAsJsonAsync($"/api/projects/{projectId}/agents/{gameDev.Id}",
            new UpdateAgentRequest("Rune Prime", null, null, null));
        (await noop.Content.ReadFromJsonAsync<AgentResponse>(TestJsonOptions.Default))!
            .RepoIds.Should().BeEquivalentTo([repoId, secondRepoId]);

        // Empty list clears back to cross-repo
        var clear = await _client.PutAsJsonAsync($"/api/projects/{projectId}/agents/{gameDev.Id}",
            new UpdateAgentRequest(null, null, null, null, RepoIds: []));
        (await clear.Content.ReadFromJsonAsync<AgentResponse>(TestJsonOptions.Default))!
            .RepoIds.Should().BeEmpty();
    }
}
