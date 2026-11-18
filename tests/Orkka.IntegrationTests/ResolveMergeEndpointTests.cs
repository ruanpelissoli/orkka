namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Features.MergeResolution;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The merge page's write endpoint. Its job is to be safe to press: a person watching a
/// conflict cannot see that a merge click already armed the resolver server-side, so asking
/// again for the resolution that is already queued must not read as a failure.
/// </summary>
[Collection("Integration")]
public sealed class ResolveMergeEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Ulid> SeedTaskWithPrAsync(string? pendingStrategy)
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Resolve Merge Test Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("resolve-merge-repo", "https://github.com/test/resolve-merge.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/tasks",
            new CreateTaskRequest(repo!.Id, "Resolve merge task", "desc", "Medium"));
        var task = await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        await db.Tasks.Where(t => t.Id == task!.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.PrUrl, "https://github.com/test/resolve-merge/pull/1")
                .SetProperty(t => t.MergeResolutionStrategy, pendingStrategy));

        return task!.Id;
    }

    private Task<HttpResponseMessage> ResolveAsync(Ulid taskId, string strategy, string? guidance = null)
        => _client.PostAsJsonAsync($"/api/tasks/{taskId}/resolve-merge",
            new ResolveMergeRequest(strategy, guidance, null));

    [Fact]
    public async Task RepeatingThePendingStrategy_Succeeds()
    {
        var taskId = await SeedTaskWithPrAsync(pendingStrategy: "combine");

        var response = await ResolveAsync(taskId, "combine");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "the click asks for the resolution that is already queued — that intent is already served");
    }

    [Fact]
    public async Task ChoosingADifferentStrategyMidRun_QueuesItBehindTheRunningOne()
    {
        var taskId = await SeedTaskWithPrAsync(pendingStrategy: "combine");

        var response = await ResolveAsync(taskId, "task");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.MergeResolutionStrategy.Should().Be("combine",
            "the attempt in flight is never interrupted — it may still succeed, and killing it "
            + "mid-write would leave the worktree half-resolved");
        task.QueuedMergeStrategy.Should().Be("task");
    }

    [Fact]
    public async Task GuidanceForARunningAttempt_IsQueuedRatherThanDropped()
    {
        var taskId = await SeedTaskWithPrAsync(pendingStrategy: "combine");

        var response = await ResolveAsync(taskId, "combine", "keep the new colors");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.QueuedMergeStrategy.Should().Be("combine");
        task.QueuedMergeGuidance.Should().Be("keep the new colors");
    }

    [Fact]
    public async Task QueuingAgain_ReplacesTheFollowUp()
    {
        // A queue of one: steering is "no, do it this way instead", so the newest instruction
        // is the only one worth an agent run.
        var taskId = await SeedTaskWithPrAsync(pendingStrategy: "combine");

        await ResolveAsync(taskId, "task");
        await ResolveAsync(taskId, "current");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.QueuedMergeStrategy.Should().Be("current");
    }

    [Fact]
    public async Task CancellingTheFollowUp_LeavesTheRunningAttemptAlone()
    {
        var taskId = await SeedTaskWithPrAsync(pendingStrategy: "combine");
        await ResolveAsync(taskId, "task");

        var response = await _client.DeleteAsync($"/api/tasks/{taskId}/resolve-merge/queued");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.QueuedMergeStrategy.Should().BeNull();
        task.MergeResolutionStrategy.Should().Be("combine");
    }

    [Fact]
    public async Task FinishingUnmerged_PromotesTheQueuedFollowUp()
    {
        var taskId = await SeedTaskWithPrAsync(pendingStrategy: "combine");
        await ResolveAsync(taskId, "task");

        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        var promoted = await api.ClearTaskMergeResolutionAsync(taskId);

        promoted.Should().Be("task");
        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.MergeResolutionStrategy.Should().Be("task", "the sweep starts it on its next cycle");
        task.QueuedMergeStrategy.Should().BeNull();
        task.MergeResolutionStartedAt.Should().NotBeNull("the follow-up's own clock starts now");
    }

    [Fact]
    public async Task MergingDropsTheQueuedFollowUp()
    {
        // Nothing left to steer: the conflict it was aimed at no longer exists, and running it
        // would spend a full agent run resolving a merge that already landed.
        var taskId = await SeedTaskWithPrAsync(pendingStrategy: "combine");
        await ResolveAsync(taskId, "task");

        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await api.MarkTaskMergedAsync(taskId, "abc123");
        var promoted = await api.ClearTaskMergeResolutionAsync(taskId);

        promoted.Should().BeNull();
        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.QueuedMergeStrategy.Should().BeNull();
        task.MergeResolutionStrategy.Should().BeNull();
        task.MergeResolutionStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task FirstChoice_IsPersistedForTheWorker()
    {
        var taskId = await SeedTaskWithPrAsync(pendingStrategy: null);

        var response = await ResolveAsync(taskId, "combine", "keep the new colors");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.MergeResolutionStrategy.Should().Be("combine");
        task.MergeResolutionGuidance.Should().Be("keep the new colors");
        task.MergeResolutionStartedAt.Should().NotBeNull(
            "the merge page reads its elapsed clock off this, not off when the browser opened");
    }

    [Fact]
    public async Task RepeatingPerFile_IsNotTreatedAsTheSameRequest()
    {
        // Per-file carries the choices in its payload, so a second call is a new instruction
        // rather than a repeat of the first — it must not be absorbed silently.
        var taskId = await SeedTaskWithPrAsync(pendingStrategy: "perFile");

        var response = await _client.PostAsJsonAsync($"/api/tasks/{taskId}/resolve-merge",
            new ResolveMergeRequest("perFile", null,
                [new ResolveMergeFileChoice("src/app.ts", "task")]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
