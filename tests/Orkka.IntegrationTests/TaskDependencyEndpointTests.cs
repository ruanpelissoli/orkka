namespace Orkka.IntegrationTests;

using System.Net;
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

[Collection("Integration")]
public sealed class TaskDependencyEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private MockTaskNotifier GetNotifier() =>
        (MockTaskNotifier)factory.Services.GetRequiredService<ITaskNotifier>();

    private async Task<(Ulid ProjectId, Ulid RepoId)> SeedProjectAndRepo(string name = "Dependency Test Project")
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest(name, "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("dep-repo", "https://github.com/test/dep.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        return (project.Id, repo!.Id);
    }

    private async Task<TaskResponse> CreateTask(Ulid projectId, Ulid repoId, string title = "Dep Task")
    {
        var request = new CreateTaskRequest(repoId, title, "Description", "Medium");
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!;
    }

    private Task<HttpResponseMessage> AddDependency(Ulid taskId, Ulid dependsOnTaskId, string? reason = null) =>
        _client.PostAsJsonAsync($"/api/tasks/{taskId}/dependencies",
            new AddTaskDependencyRequest(dependsOnTaskId, reason));

    [Fact]
    public async Task AddDependency_AppearsInBothDirections()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var a = await CreateTask(projectId, repoId, "Task A");
        var b = await CreateTask(projectId, repoId, "Task B");

        var response = await AddDependency(b.Id, a.Id, "B needs A's schema");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var lists = await response.Content.ReadFromJsonAsync<TaskDependenciesResponse>(TestJsonOptions.Default);
        lists!.DependsOn.Should().ContainSingle(d => d.TaskId == a.Id && d.Source == "Manual");
        lists.DependsOn[0].Reason.Should().Be("B needs A's schema");

        // The detail endpoint carries the same lists
        var bDetail = await _client.GetFromJsonAsync<TaskResponse>($"/api/tasks/{b.Id}", TestJsonOptions.Default);
        bDetail!.DependsOn.Should().ContainSingle(d => d.TaskId == a.Id);
        bDetail.HasUnmetDependencies.Should().BeTrue();

        // ...and the reverse direction on the blocking task
        var aDetail = await _client.GetFromJsonAsync<TaskResponse>($"/api/tasks/{a.Id}", TestJsonOptions.Default);
        aDetail!.Dependents.Should().ContainSingle(d => d.TaskId == b.Id);
        aDetail.DependsOn.Should().BeEmpty();
    }

    [Fact]
    public async Task AddDependency_Self_ReturnsBadRequest()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var a = await CreateTask(projectId, repoId);

        var response = await AddDependency(a.Id, a.Id);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddDependency_Duplicate_ReturnsBadRequest()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var a = await CreateTask(projectId, repoId, "Task A");
        var b = await CreateTask(projectId, repoId, "Task B");

        (await AddDependency(b.Id, a.Id)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AddDependency(b.Id, a.Id)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddDependency_UnknownTask_ReturnsBadRequest()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var a = await CreateTask(projectId, repoId);

        var response = await AddDependency(a.Id, Ulid.NewUlid());
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddDependency_CrossProject_ReturnsBadRequest()
    {
        var (projectId, repoId) = await SeedProjectAndRepo("Dep Project One");
        var (otherProjectId, otherRepoId) = await SeedProjectAndRepo("Dep Project Two");
        var a = await CreateTask(projectId, repoId);
        var other = await CreateTask(otherProjectId, otherRepoId, "Other Project Task");

        var response = await AddDependency(a.Id, other.Id);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddDependency_DirectCycle_ReturnsBadRequest()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var a = await CreateTask(projectId, repoId, "Task A");
        var b = await CreateTask(projectId, repoId, "Task B");

        (await AddDependency(b.Id, a.Id)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AddDependency(a.Id, b.Id)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddDependency_TransitiveCycle_ReturnsBadRequest()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var a = await CreateTask(projectId, repoId, "Task A");
        var b = await CreateTask(projectId, repoId, "Task B");
        var c = await CreateTask(projectId, repoId, "Task C");

        (await AddDependency(a.Id, b.Id)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AddDependency(b.Id, c.Id)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AddDependency(c.Id, a.Id)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RemoveDependency_ClearsBothDirections()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var a = await CreateTask(projectId, repoId, "Task A");
        var b = await CreateTask(projectId, repoId, "Task B");
        await AddDependency(b.Id, a.Id);

        var response = await _client.DeleteAsync($"/api/tasks/{b.Id}/dependencies/{a.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var bDetail = await _client.GetFromJsonAsync<TaskResponse>($"/api/tasks/{b.Id}", TestJsonOptions.Default);
        bDetail!.DependsOn.Should().BeEmpty();
        bDetail.HasUnmetDependencies.Should().BeFalse();

        var aDetail = await _client.GetFromJsonAsync<TaskResponse>($"/api/tasks/{a.Id}", TestJsonOptions.Default);
        aDetail!.Dependents.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveDependency_Nonexistent_ReturnsNotFound()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var a = await CreateTask(projectId, repoId, "Task A");
        var b = await CreateTask(projectId, repoId, "Task B");

        var response = await _client.DeleteAsync($"/api/tasks/{b.Id}/dependencies/{a.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Board_CarriesUnmetBlockers_AndClearsOnlyWhenBlockerIsDoneAndMerged()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var a = await CreateTask(projectId, repoId, "Blocker Task");
        var b = await CreateTask(projectId, repoId, "Blocked Task");
        await AddDependency(b.Id, a.Id);

        var board = await _client.GetFromJsonAsync<TaskBoardResponse>(
            $"/api/projects/{projectId}/board", TestJsonOptions.Default);
        var bCard = board!.Lanes.SelectMany(l => l.Tasks).Single(t => t.Id == b.Id);
        bCard.BlockedBy.Should().ContainSingle(r => r.TaskId == a.Id);
        bCard.BlockedBy[0].Code.Should().Be(a.Code);
        bCard.HasUnmetDependencies.Should().BeTrue();

        // Complete the blocker via a tracked save — the same path every completion site uses.
        // Its PR hasn't merged, so the dependent must STILL be blocked (agents build on merged code).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var entity = await db.Tasks.IgnoreQueryFilters().FirstAsync(t => t.Id == a.Id);
            entity.Status = TaskStatus.Completed;
            entity.PrUrl = "https://github.com/test/dep/pull/1";
            await db.SaveChangesAsync();
        }

        var boardCompleted = await _client.GetFromJsonAsync<TaskBoardResponse>(
            $"/api/projects/{projectId}/board", TestJsonOptions.Default);
        var bCardCompleted = boardCompleted!.Lanes.SelectMany(l => l.Tasks).Single(t => t.Id == b.Id);
        bCardCompleted.BlockedBy.Should().ContainSingle(r => r.TaskId == a.Id);
        bCardCompleted.BlockedBy[0].Status.Should().Be("Completed");
        bCardCompleted.BlockedBy[0].IsMerged.Should().BeFalse();
        bCardCompleted.HasUnmetDependencies.Should().BeTrue();

        // Merge the blocker's PR — now the dependency is satisfied
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var entity = await db.Tasks.IgnoreQueryFilters().FirstAsync(t => t.Id == a.Id);
            entity.IsMerged = true;
            await db.SaveChangesAsync();
        }

        var boardAfter = await _client.GetFromJsonAsync<TaskBoardResponse>(
            $"/api/projects/{projectId}/board", TestJsonOptions.Default);
        var bCardAfter = boardAfter!.Lanes.SelectMany(l => l.Tasks).Single(t => t.Id == b.Id);
        bCardAfter.BlockedBy.Should().BeEmpty();
        bCardAfter.HasUnmetDependencies.Should().BeFalse();
    }

    [Fact]
    public async Task AddAndRemoveDependency_FireDependenciesChangedNotifications()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var a = await CreateTask(projectId, repoId, "Task A");
        var b = await CreateTask(projectId, repoId, "Task B");
        var notifier = GetNotifier();

        var countBefore = notifier.SentEvents.Count;
        await AddDependency(b.Id, a.Id);

        var addEvt = notifier.SentEvents.Skip(countBefore)
            .OfType<TaskDependenciesChangedEvent>().Should().ContainSingle().Subject;
        addEvt.TaskId.Should().Be(b.Id);
        addEvt.BlockedBy.Should().ContainSingle(r => r.TaskId == a.Id);

        countBefore = notifier.SentEvents.Count;
        await _client.DeleteAsync($"/api/tasks/{b.Id}/dependencies/{a.Id}");

        var removeEvt = notifier.SentEvents.Skip(countBefore)
            .OfType<TaskDependenciesChangedEvent>().Should().ContainSingle().Subject;
        removeEvt.TaskId.Should().Be(b.Id);
        removeEvt.BlockedBy.Should().BeEmpty();
    }

    [Fact]
    public async Task BlockerCompletion_FiresDependenciesChangedForDependent_StillBlockedUntilMerge()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var a = await CreateTask(projectId, repoId, "Blocker Task");
        var b = await CreateTask(projectId, repoId, "Dependent Task");
        await AddDependency(b.Id, a.Id);

        var notifier = GetNotifier();
        var countBefore = notifier.SentEvents.Count;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var entity = await db.Tasks.IgnoreQueryFilters().FirstAsync(t => t.Id == a.Id);
            entity.Status = TaskStatus.Completed;
            entity.PrUrl = "https://github.com/test/dep/pull/1";
            await db.SaveChangesAsync();
        }

        // The TaskStatusChanged event is drained by a background dispatcher — poll briefly.
        // The badge refreshes, but the blocker is still listed: done ≠ merged.
        var evt = await WaitForDependenciesChangedAsync(notifier, countBefore, b.Id);
        evt.Should().NotBeNull("completing a blocker should refresh its dependents' badges");
        evt!.BlockedBy.Should().ContainSingle(r => r.TaskId == a.Id);
        evt.BlockedBy[0].Status.Should().Be("Completed");
        evt.BlockedBy[0].IsMerged.Should().BeFalse();
    }

    [Fact]
    public async Task BlockerMerge_FiresDependenciesChanged_WithBlockerCleared()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var a = await CreateTask(projectId, repoId, "Blocker Task");
        var b = await CreateTask(projectId, repoId, "Dependent Task");
        await AddDependency(b.Id, a.Id);

        var notifier = GetNotifier();
        var countBefore = notifier.SentEvents.Count;

        // MarkTaskMergedAsync is the shared merge path (auto-merge sweep, webhook, in-app
        // merge) — it sets Done+Completed+IsMerged and publishes TaskMerged.
        using (var scope = factory.Services.CreateScope())
        {
            var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
            await tasksApi.MarkTaskMergedAsync(a.Id);
        }

        var evt = await WaitForDependenciesChangedAsync(notifier, countBefore, b.Id);
        evt.Should().NotBeNull("merging a blocker should refresh its dependents' badges");
        evt!.BlockedBy.Should().BeEmpty();
    }

    [Fact]
    public async Task AddDependency_OnDoneButUnmergedTask_Succeeds()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var a = await CreateTask(projectId, repoId, "Done Unmerged Blocker");
        var b = await CreateTask(projectId, repoId, "Dependent Task");

        // Done + archived, but the PR hasn't landed — still a meaningful thing to wait on
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var entity = await db.Tasks.IgnoreQueryFilters().FirstAsync(t => t.Id == a.Id);
            entity.CurrentLane = Lane.Done;
            entity.Status = TaskStatus.Completed;
            entity.IsArchived = true;
            entity.PrUrl = "https://github.com/test/dep/pull/2";
            await db.SaveChangesAsync();
        }

        var response = await AddDependency(b.Id, a.Id);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var lists = await response.Content.ReadFromJsonAsync<TaskDependenciesResponse>(TestJsonOptions.Default);
        lists!.DependsOn.Should().ContainSingle(d => d.TaskId == a.Id && !d.IsMerged);
    }

    [Fact]
    public async Task AddDependency_OnDoneAndMergedTask_ReturnsBadRequest()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var a = await CreateTask(projectId, repoId, "Merged Blocker");
        var b = await CreateTask(projectId, repoId, "Dependent Task");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var entity = await db.Tasks.IgnoreQueryFilters().FirstAsync(t => t.Id == a.Id);
            entity.CurrentLane = Lane.Done;
            entity.Status = TaskStatus.Completed;
            entity.IsArchived = true;
            entity.IsMerged = true;
            await db.SaveChangesAsync();
        }

        var response = await AddDependency(b.Id, a.Id);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MoveLane_Forward_WithUnmetDependencies_IsRejected_BackwardAllowed()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var a = await CreateTask(projectId, repoId, "Blocker Task");
        var b = await CreateTask(projectId, repoId, "Dependent Task");
        await AddDependency(b.Id, a.Id);

        // Submitting to Ready is allowed — blocked tasks wait there
        (await _client.PostAsync($"/api/tasks/{b.Id}/submit", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Forward moves are rejected while the dependency is unmet, naming the blocker
        var forward = await _client.PatchAsJsonAsync($"/api/tasks/{b.Id}/lane",
            new MoveTaskLaneRequest("Planning"));
        forward.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await forward.Content.ReadAsStringAsync()).Should().Contain(a.Code);

        // Backward moves stay open: park the dependent further down the pipeline first
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var entity = await db.Tasks.IgnoreQueryFilters().FirstAsync(t => t.Id == b.Id);
            entity.CurrentLane = Lane.Developing;
            await db.SaveChangesAsync();
        }

        var backward = await _client.PatchAsJsonAsync($"/api/tasks/{b.Id}/lane",
            new MoveTaskLaneRequest("Ready"));
        backward.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MoveLane_Forward_Succeeds_OnceBlockerIsDoneAndMerged()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var a = await CreateTask(projectId, repoId, "Blocker Task");
        var b = await CreateTask(projectId, repoId, "Dependent Task");
        await AddDependency(b.Id, a.Id);
        await _client.PostAsync($"/api/tasks/{b.Id}/submit", null);

        using (var scope = factory.Services.CreateScope())
        {
            var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
            await tasksApi.MarkTaskMergedAsync(a.Id);
        }

        var forward = await _client.PatchAsJsonAsync($"/api/tasks/{b.Id}/lane",
            new MoveTaskLaneRequest("Planning"));
        forward.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<TaskDependenciesChangedEvent?> WaitForDependenciesChangedAsync(
        MockTaskNotifier notifier, int countBefore, Ulid taskId)
    {
        TaskDependenciesChangedEvent? evt = null;
        for (var i = 0; i < 50 && evt is null; i++)
        {
            evt = notifier.SentEvents.Skip(countBefore)
                .OfType<TaskDependenciesChangedEvent>()
                .FirstOrDefault(e => e.TaskId == taskId);
            if (evt is null)
                await Task.Delay(100);
        }
        return evt;
    }
}
