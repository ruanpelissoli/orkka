namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Features.TaskMessages;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

[Collection("Integration")]
public sealed class TaskNotificationTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private MockTaskNotifier GetNotifier() =>
        (MockTaskNotifier)factory.Services.GetRequiredService<ITaskNotifier>();

    private async Task<(Ulid ProjectId, Ulid RepoId)> SeedProjectAndRepo()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Notify Test Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("notify-repo", "https://github.com/test/notify.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        return (project.Id, repo!.Id);
    }

    private async Task<TaskResponse> CreateTask(Ulid projectId, Ulid repoId, string title = "Notify Task")
    {
        var request = new CreateTaskRequest(repoId, title, "Description", "Medium");
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!;
    }

    [Fact]
    public async Task SubmitTask_FiresLaneChangedNotification()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var task = await CreateTask(projectId, repoId);
        var notifier = GetNotifier();
        var countBefore = notifier.SentEvents.Count;

        await _client.PostAsync($"/api/tasks/{task.Id}/submit", null);

        notifier.SentEvents.Skip(countBefore)
            .Should().ContainSingle(e => e is LaneChangedEvent);

        var evt = notifier.SentEvents.Skip(countBefore).OfType<LaneChangedEvent>().First();
        evt.TaskId.Should().Be(task.Id);
        evt.FromLane.Should().Be("Drafting");
        evt.ToLane.Should().Be("Ready");
    }

    [Fact]
    public async Task MoveTaskLane_FiresLaneChangedNotification()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var task = await CreateTask(projectId, repoId);
        await _client.PostAsync($"/api/tasks/{task.Id}/submit", null);

        var notifier = GetNotifier();
        var countBefore = notifier.SentEvents.Count;

        var moveReq = new MoveTaskLaneRequest("Planning");
        await _client.PatchAsJsonAsync($"/api/tasks/{task.Id}/lane", moveReq);

        notifier.SentEvents.Skip(countBefore)
            .Should().ContainSingle(e => e is LaneChangedEvent);

        var evt = notifier.SentEvents.Skip(countBefore).OfType<LaneChangedEvent>().First();
        evt.FromLane.Should().Be("Ready");
        evt.ToLane.Should().Be("Planning");
    }

    [Fact]
    public async Task BlockTask_FiresStatusChangedNotification()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var task = await CreateTask(projectId, repoId);
        await _client.PostAsync($"/api/tasks/{task.Id}/submit", null);

        var notifier = GetNotifier();
        var countBefore = notifier.SentEvents.Count;

        await _client.PostAsync($"/api/tasks/{task.Id}/block", null);

        notifier.SentEvents.Skip(countBefore)
            .Should().ContainSingle(e => e is StatusChangedEvent);

        var evt = notifier.SentEvents.Skip(countBefore).OfType<StatusChangedEvent>().First();
        evt.TaskId.Should().Be(task.Id);
        evt.NewStatus.Should().Be("Blocked");
    }

    [Fact]
    public async Task SendMessage_FiresNewMessageNotification()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var task = await CreateTask(projectId, repoId);

        var notifier = GetNotifier();
        var countBefore = notifier.SentEvents.Count;

        var sendReq = new SendMessageRequest("Hello from test!");
        await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/messages", sendReq);

        notifier.SentEvents.Skip(countBefore)
            .Should().ContainSingle(e => e is NewMessageEvent);

        var evt = notifier.SentEvents.Skip(countBefore).OfType<NewMessageEvent>().First();
        evt.TaskId.Should().Be(task.Id);
        evt.Content.Should().Be("Hello from test!");
    }

    [Fact]
    public async Task ArchiveTask_FiresTaskUpdatedNotification()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var task = await CreateTask(projectId, repoId);

        var notifier = GetNotifier();
        var countBefore = notifier.SentEvents.Count;

        await _client.PostAsync($"/api/tasks/{task.Id}/archive", null);

        notifier.SentEvents.Skip(countBefore)
            .Should().ContainSingle(e => e is TaskUpdatedEvent);

        var evt = notifier.SentEvents.Skip(countBefore).OfType<TaskUpdatedEvent>().First();
        evt.TaskId.Should().Be(task.Id);
        evt.IsArchived.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateTask_FiresTaskUpdatedNotification()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var task = await CreateTask(projectId, repoId);

        var notifier = GetNotifier();
        var countBefore = notifier.SentEvents.Count;

        var updateReq = new UpdateTaskRequest("New Title", null, null, null, null, null);
        await _client.PatchAsJsonAsync($"/api/tasks/{task.Id}", updateReq);

        notifier.SentEvents.Skip(countBefore)
            .Should().ContainSingle(e => e is TaskUpdatedEvent);

        var evt = notifier.SentEvents.Skip(countBefore).OfType<TaskUpdatedEvent>().First();
        evt.TaskId.Should().Be(task.Id);
        evt.Title.Should().Be("New Title");
    }
}
