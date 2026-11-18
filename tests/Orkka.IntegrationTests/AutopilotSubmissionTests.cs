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
/// Autopilot's submission guard, against the real database (an ExecuteUpdate path the in-memory
/// provider cannot run).
///
/// The property under test is that a Drafting task can be auto-submitted exactly once. Several
/// Worker replicas sweep the same project at the same time, so a guard that merely reads-then-
/// writes would let two of them submit the same task, double-count the session's task counter,
/// and fire the dependency analysis twice — at the cost of a model call each.
/// </summary>
[Collection("Integration")]
public sealed class AutopilotSubmissionTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Ulid> SeedDraftingTaskAsync()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Autopilot Test Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("autopilot-repo", "https://github.com/test/autopilot.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/tasks",
            new CreateTaskRequest(repo!.Id, "Autopilot task", "desc", "Medium"));
        var task = await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        return task!.Id;
    }

    private IServiceScope CreateScope() => factory.Services.CreateScope();

    [Fact]
    public async Task AutoSubmit_DraftingTask_QueuesItAndStampsTheSession()
    {
        var taskId = await SeedDraftingTaskAsync();
        var sessionId = Ulid.NewUlid();

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        (await api.TryAutoSubmitAsync(taskId, sessionId)).Should().BeTrue();

        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.CurrentLane.Should().Be(Lane.Ready);
        task.Status.Should().Be(TaskStatus.InQueue);
        task.AutopilotSessionId.Should().Be(sessionId,
            "the stamp is what makes the orchestrator run this task hands-free");
        task.QueuePosition.Should().NotBeNull();
    }

    [Fact]
    public async Task AutoSubmit_OpensTheDependencyGate()
    {
        var taskId = await SeedDraftingTaskAsync();

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await api.TryAutoSubmitAsync(taskId, Ulid.NewUlid());

        // Set in the same update as the lane move. Without it the queue can claim the task in
        // the seconds before edge analysis writes anything, which is how a whole milestone
        // starts at once instead of in dependency order.
        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.DependencyAnalysisStartedAt.Should().NotBeNull(
            "an unanalysed task must not be claimable");
    }

    [Fact]
    public async Task AutoSubmit_SecondCallerLoses()
    {
        var taskId = await SeedDraftingTaskAsync();
        var firstSession = Ulid.NewUlid();

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        (await api.TryAutoSubmitAsync(taskId, firstSession)).Should().BeTrue();
        (await api.TryAutoSubmitAsync(taskId, Ulid.NewUlid()))
            .Should().BeFalse("the task left Drafting, so the guard must reject the second sweep");

        (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId)).AutopilotSessionId
            .Should().Be(firstSession, "the loser must not overwrite the winner's stamp");
    }

    [Fact]
    public async Task AutoSubmit_RefusesATaskWithNoRepo()
    {
        var taskId = await SeedDraftingTaskAsync();

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await db.Tasks.Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RepoId, (Ulid?)null));

        // A task with no repo cannot be executed — the orchestrator has no worktree to create.
        // Queueing it would park it in the pipeline forever rather than failing visibly.
        (await api.TryAutoSubmitAsync(taskId, Ulid.NewUlid())).Should().BeFalse();

        (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId)).CurrentLane
            .Should().Be(Lane.Drafting);
    }

    [Fact]
    public async Task AutoSubmit_RefusesAnArchivedTask()
    {
        var taskId = await SeedDraftingTaskAsync();

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await db.Tasks.Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsArchived, true));

        (await api.TryAutoSubmitAsync(taskId, Ulid.NewUlid()))
            .Should().BeFalse("archiving is how a user takes work off the board");
    }

    [Fact]
    public async Task AutoSubmit_RefusesATaskAwaitingReset()
    {
        var taskId = await SeedDraftingTaskAsync();

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await db.Tasks.Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                t => t.ResetRequestedAt, DateTimeOffset.UtcNow));

        // The reset sweep is about to wipe this task. Re-queueing it would race the wipe.
        (await api.TryAutoSubmitAsync(taskId, Ulid.NewUlid())).Should().BeFalse();
    }

    [Fact]
    public async Task AutoSubmit_RefusesAnUnknownTask()
    {
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        (await api.TryAutoSubmitAsync(Ulid.NewUlid(), Ulid.NewUlid()))
            .Should().BeFalse("a task deleted mid-sweep must not throw");
    }
}
