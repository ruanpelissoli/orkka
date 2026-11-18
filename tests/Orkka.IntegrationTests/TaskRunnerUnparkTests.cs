namespace Orkka.IntegrationTests;

using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// A task parked because its runner vanished mid-run must come back on its own when the
/// runner returns. The hosted edition sweeps per runner identity; Orkka Local has one
/// machine and one runner, and its tasks carry no RunnerUserId to filter on — so it needs
/// the unfiltered sweep. Getting this wrong strands the task in WaitingForRunner forever,
/// which no queue query picks up.
/// </summary>
[Collection("Integration")]
public sealed class TaskRunnerUnparkTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Ulid> SeedTaskAsync()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Unpark Test Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("unpark-repo", "https://github.com/test/unpark.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/tasks",
            new CreateTaskRequest(repo!.Id, "Unpark task", "desc", "Medium"));
        var task = await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        return task!.Id;
    }

    /// <summary>Parks a task the way a mid-run disconnect does, in the given lane.</summary>
    private static Task ParkAsync(TasksDbContext db, Ulid taskId, Lane lane, string? runnerUserId)
        => db.Tasks.Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TaskStatus.WaitingForRunner)
                .SetProperty(t => t.HasAgentWorking, false)
                .SetProperty(t => t.CurrentLane, lane)
                .SetProperty(t => t.RunnerUserId, runnerUserId));

    [Fact]
    public async Task RequeueAll_RequeuesParkedTaskThatHasNoRunnerUserId()
    {
        var taskId = await SeedTaskAsync();
        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        // The Orkka Local shape: parked in the lane it died in, no runner identity.
        await ParkAsync(db, taskId, Lane.CodeReview, runnerUserId: null);

        await api.RequeueAllWaitingForRunnerTasksAsync();

        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.Status.Should().Be(TaskStatus.InQueue,
            "the queue processor only picks up InQueue — anything else strands the task");
        task.CurrentLane.Should().Be(Lane.CodeReview,
            "re-entry re-runs the phase it died in, it does not rewind the pipeline");
    }

    [Fact]
    public async Task PerUserRequeue_DoesNotMatchATaskWithNoRunnerUserId()
    {
        var taskId = await SeedTaskAsync();
        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        var orgId = (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId)).OrgId;
        await ParkAsync(db, taskId, Lane.CodeReview, runnerUserId: null);

        await api.RequeueWaitingForRunnerTasksAsync(orgId, "some-runner-user");

        (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId)).Status
            .Should().Be(TaskStatus.WaitingForRunner,
                "the identity-filtered sweep is why Local needs its own — this is the regression");
    }

    [Fact]
    public async Task PerUserRequeue_RequeuesOnlyTheMatchingRunnersTasks()
    {
        var mine = await SeedTaskAsync();
        var theirs = await SeedTaskAsync();
        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        var orgId = (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == mine)).OrgId;
        await ParkAsync(db, mine, Lane.Developing, "runner-user-a");
        await ParkAsync(db, theirs, Lane.Developing, "runner-user-b");

        await api.RequeueWaitingForRunnerTasksAsync(orgId, "runner-user-a");

        (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == mine)).Status
            .Should().Be(TaskStatus.InQueue);
        (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == theirs)).Status
            .Should().Be(TaskStatus.WaitingForRunner,
                "another user's runner is still offline — unparking their work would spin it against nothing");
    }

    [Fact]
    public async Task RequeueAll_LeavesTasksThatAreNotParkedAlone()
    {
        var taskId = await SeedTaskAsync();
        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await db.Tasks.Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TaskStatus.Blocked));

        await api.RequeueAllWaitingForRunnerTasksAsync();

        (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId)).Status
            .Should().Be(TaskStatus.Blocked, "a genuine agent failure still waits for a human");
    }
}
