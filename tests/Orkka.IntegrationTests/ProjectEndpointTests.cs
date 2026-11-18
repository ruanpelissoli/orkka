namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.Projects;
using FluentAssertions;

[Collection("Integration")]
public sealed class ProjectEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task CreateProject_ReturnsCreatedWithDefaults()
    {
        var request = new CreateProjectRequest("Test Project", "A test project");

        var response = await _client.PostAsJsonAsync("/api/projects", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        body.Should().NotBeNull();
        body!.Name.Should().Be("Test Project");
        body.Description.Should().Be("A test project");
        body.Settings.MaxParallelTasks.Should().Be(1);
        body.Settings.SameRepoParallelism.Should().BeFalse();
        body.TaskCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateProject_EmptyName_ReturnsBadRequest()
    {
        var request = new CreateProjectRequest("", "desc");

        var response = await _client.PostAsJsonAsync("/api/projects", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetProject_NonExistent_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/projects/{Ulid.NewUlid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task FullLifecycle_CreateGetUpdateListDelete()
    {
        // Create
        var createResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Lifecycle Project", "Original description"));
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        created.Should().NotBeNull();

        // Get by ID
        var getResp = await _client.GetAsync($"/api/projects/{created!.Id}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var fetched = await getResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        fetched!.Id.Should().Be(created.Id);
        fetched.Name.Should().Be("Lifecycle Project");

        // Update (PATCH) — only name
        var patchResp = await _client.PatchAsJsonAsync($"/api/projects/{created.Id}",
            new UpdateProjectRequest("Updated Name", null));
        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await patchResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        updated!.Name.Should().Be("Updated Name");
        updated.Description.Should().Be("Original description", "description should not change");

        // List
        var listResp = await _client.GetAsync("/api/projects");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var projects = await listResp.Content.ReadFromJsonAsync<List<ProjectResponse>>(TestJsonOptions.Default);
        projects.Should().NotBeNull();
        projects!.Should().Contain(p => p.Id == created.Id);

        // Delete
        var deleteResp = await _client.DeleteAsync($"/api/projects/{created.Id}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify deleted
        var getAfterDelete = await _client.GetAsync($"/api/projects/{created.Id}");
        getAfterDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateProject_NonExistent_ReturnsNotFound()
    {
        var response = await _client.PatchAsJsonAsync($"/api/projects/{Ulid.NewUlid()}",
            new UpdateProjectRequest("Name", null));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteProject_NonExistent_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync($"/api/projects/{Ulid.NewUlid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
