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
public sealed class TaskMessageEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<TaskResponse> SeedTask()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Message Test Project", null));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("msg-repo", "https://github.com/test/msg-repo.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var taskReq = new CreateTaskRequest(repo!.Id, "Message Task", "desc");
        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/tasks", taskReq);
        return (await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!;
    }

    [Fact]
    public async Task SendAndListMessages_WorksCorrectly()
    {
        var task = await SeedTask();

        // Send a message
        var sendReq = new SendMessageRequest("Hello agent!");
        var sendResp = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/messages", sendReq);
        sendResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var message = await sendResp.Content.ReadFromJsonAsync<TaskMessageResponse>(TestJsonOptions.Default);
        message!.Content.Should().Be("Hello agent!");
        message.Role.Should().Be("user");
        message.SenderId.Should().Be(TestCurrentUser.TestUserId);

        // List messages
        var listResp = await _client.GetAsync($"/api/tasks/{task.Id}/messages");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var messages = await listResp.Content.ReadFromJsonAsync<List<TaskMessageResponse>>(TestJsonOptions.Default);
        messages!.Should().ContainSingle(m => m.Content == "Hello agent!");
    }

    [Fact]
    public async Task SendMessage_NonExistentTask_ReturnsNotFound()
    {
        var sendReq = new SendMessageRequest("Hello!");
        var response = await _client.PostAsJsonAsync($"/api/tasks/{Ulid.NewUlid()}/messages", sendReq);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
