namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using FluentAssertions;

[Collection("Integration")]
public sealed class ProjectRepoEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<ProjectResponse> CreateTestProjectAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Repo Test Project", "For repo tests"));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var project = await resp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        project.Should().NotBeNull();
        return project!;
    }

    [Fact]
    public async Task AddRepo_ReturnsCreatedWithPendingStatus()
    {
        var project = await CreateTestProjectAsync();

        var request = new AddProjectRepoRequest(
            "my-repo",
            "https://github.com/org/my-repo.git",
            "main",
            "github",
            null);

        var response = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/repos", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);
        body.Should().NotBeNull();
        body!.Name.Should().Be("my-repo");
        body.GitUrl.Should().Be("https://github.com/org/my-repo.git");
        body.DefaultBranch.Should().Be("main");
        body.Provider.Should().Be("github");
        body.WorkspaceStatus.Should().Be("active"); // Pending maps to "active"
        body.CardColor.Should().StartWith("#");
        body.ProjectId.Should().Be(project.Id);
    }

    [Fact]
    public async Task AddRepo_NonExistentProject_ReturnsNotFound()
    {
        var request = new AddProjectRepoRequest(
            "repo", "https://github.com/org/repo.git", "main", null, null);

        var response = await _client.PostAsJsonAsync($"/api/projects/{Ulid.NewUlid()}/repos", request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddRepo_InvalidGitUrl_ReturnsBadRequest()
    {
        var project = await CreateTestProjectAsync();

        var request = new AddProjectRepoRequest(
            "repo", "not-a-url", "main", null, null);

        var response = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/repos", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListRepos_EmptyProject_ReturnsEmptyList()
    {
        var project = await CreateTestProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{project.Id}/repos");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var repos = await response.Content.ReadFromJsonAsync<List<ProjectRepoResponse>>(TestJsonOptions.Default);
        repos.Should().NotBeNull();
        repos!.Should().BeEmpty();
    }

    [Fact]
    public async Task FullLifecycle_AddListDeleteRepo()
    {
        var project = await CreateTestProjectAsync();

        // Add repo
        var addResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/repos",
            new AddProjectRepoRequest(
                "lifecycle-repo",
                "https://github.com/org/lifecycle-repo.git",
                "develop",
                "github",
                null));
        addResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var repo = await addResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);
        repo.Should().NotBeNull();

        // List repos
        var listResp = await _client.GetAsync($"/api/projects/{project.Id}/repos");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var repos = await listResp.Content.ReadFromJsonAsync<List<ProjectRepoResponse>>(TestJsonOptions.Default);
        repos.Should().NotBeNull();
        repos!.Should().ContainSingle(r => r.Id == repo!.Id);

        // Delete repo (note: /api/repos/{repoId}, not nested)
        var deleteResp = await _client.DeleteAsync($"/api/repos/{repo!.Id}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify deleted
        var listAfterDelete = await _client.GetAsync($"/api/projects/{project.Id}/repos");
        var reposAfterDelete = await listAfterDelete.Content
            .ReadFromJsonAsync<List<ProjectRepoResponse>>(TestJsonOptions.Default);
        reposAfterDelete!.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteRepo_NonExistent_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync($"/api/repos/{Ulid.NewUlid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
