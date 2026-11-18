namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Features.TaskMessages;
using Orkka.Modules.Tasks.Features.Tasks;
using FluentAssertions;

[Collection("Integration")]
public sealed class BoardSearchEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(Ulid ProjectId, Ulid RepoId)> SeedProjectAndRepo()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Board Search Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("test-repo", "https://github.com/test/repo.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        return (project.Id, repo!.Id);
    }

    private async Task<TaskResponse> CreateTask(Ulid projectId, Ulid repoId, string title)
    {
        var request = new CreateTaskRequest(repoId, title, "Description", "Medium");
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!;
    }

    private async Task<List<TaskSummaryResponse>> Search(Ulid projectId, string query)
    {
        var response = await _client.GetAsync($"/api/projects/{projectId}/board/search?{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<TaskSummaryResponse>>(TestJsonOptions.Default))!;
    }

    [Fact]
    public async Task Search_MatchesTitleSubstring()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var match = await CreateTask(projectId, repoId, "Fix login bug");
        await CreateTask(projectId, repoId, "Add dashboard");

        var results = await Search(projectId, "q=login");

        results.Should().ContainSingle(t => t.Id == match.Id);
        var summary = results.Single(t => t.Id == match.Id);
        summary.Title.Should().Be("Fix login bug");
        summary.Code.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Search_IsCaseInsensitive()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var match = await CreateTask(projectId, repoId, "Fix login bug");

        var results = await Search(projectId, "q=LOGIN");

        results.Should().Contain(t => t.Id == match.Id);
    }

    [Fact]
    public async Task Search_MessageContent_OnlyWithIncludeMessages()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var task = await CreateTask(projectId, repoId, "Plain title");
        var msgResp = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/messages",
            new SendMessageRequest("contains magicneedle inside"));
        msgResp.EnsureSuccessStatusCode();

        var withoutFlag = await Search(projectId, "q=magicneedle");
        withoutFlag.Should().BeEmpty();

        var withFlag = await Search(projectId, "q=magicneedle&includeMessages=true");
        withFlag.Should().ContainSingle(t => t.Id == task.Id);
    }

    [Fact]
    public async Task Search_ArchivedTasks_OnlyWithIncludeArchived()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var task = await CreateTask(projectId, repoId, "Archived treasure");
        var archiveResp = await _client.PostAsync($"/api/tasks/{task.Id}/archive", null);
        archiveResp.EnsureSuccessStatusCode();

        var withoutFlag = await Search(projectId, "q=treasure");
        withoutFlag.Should().BeEmpty();

        var withFlag = await Search(projectId, "q=treasure&includeArchived=true");
        withFlag.Should().ContainSingle(t => t.Id == task.Id);
    }

    [Fact]
    public async Task Search_WhitespaceQuery_ReturnsEmptyOk()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        await CreateTask(projectId, repoId, "Some task");

        var results = await Search(projectId, "q=%20%20");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_EscapesLikeWildcards()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        await CreateTask(projectId, repoId, "Ordinary task");
        var match = await CreateTask(projectId, repoId, "Progress at 50% done");

        // sanity: the task is findable by a plain word
        var plain = await Search(projectId, "q=Progress");
        plain.Should().ContainSingle(t => t.Id == match.Id);

        // "%" must match literally, not as a wildcard swallowing everything
        var results = await Search(projectId, "q=50%25");

        results.Should().ContainSingle(t => t.Id == match.Id);
    }
}
