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

/// <summary>
/// Merge-failure surfacing: SetTaskMergeFailureAsync must be change-detecting (the merge
/// poller retries every cycle — an unchanged reason must not spam the task thread) and
/// MarkTaskMergedAsync must clear the flag so the UI badge disappears once the PR lands.
/// </summary>
[Collection("Integration")]
public sealed class TaskMergeFailureTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Ulid> SeedTaskAsync()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Merge Failure Test Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("merge-failure-repo", "https://github.com/test/merge-failure.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/tasks",
            new CreateTaskRequest(repo!.Id, "Merge failure task", "desc", "Medium"));
        var task = await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        return task!.Id;
    }

    private IServiceScope CreateScope() => factory.Services.CreateScope();

    [Fact]
    public async Task SetTaskMergeFailure_PersistsReasonAndErrorMessage()
    {
        var taskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        var update = await api.SetTaskMergeFailureAsync(taskId, "Required status check \"ci\" is expected.");

        update.Should().NotBeNull();
        update!.Changed.Should().BeTrue();
        update.Message.Should().NotBeNull("a new failure must land in the task thread");

        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.MergeFailureReason.Should().Be("Required status check \"ci\" is expected.");

        var errorMessages = await db.TaskMessages.AsNoTracking()
            .Where(m => m.TaskId == taskId && m.MessageType == MessageType.Error)
            .ToListAsync();
        errorMessages.Should().ContainSingle()
            .Which.Content.Should().Contain("Required status check");
    }

    [Fact]
    public async Task SetTaskMergeFailure_SameReasonTwice_IsDeduplicated()
    {
        var taskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        var first = await api.SetTaskMergeFailureAsync(taskId, "merge conflict");
        var second = await api.SetTaskMergeFailureAsync(taskId, "merge conflict");

        first!.Changed.Should().BeTrue();
        second!.Changed.Should().BeFalse("the poller retries every cycle — same reason must not re-notify");
        second.Message.Should().BeNull();

        (await db.TaskMessages.AsNoTracking()
                .CountAsync(m => m.TaskId == taskId && m.MessageType == MessageType.Error))
            .Should().Be(1, "an unchanged reason must not add another thread message");
    }

    [Fact]
    public async Task SetTaskMergeFailure_NewReason_RecordsAgain()
    {
        var taskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await api.SetTaskMergeFailureAsync(taskId, "merge conflict");
        var update = await api.SetTaskMergeFailureAsync(taskId, "Required status check \"ci\" is expected.");

        update!.Changed.Should().BeTrue("a different reason is new information");

        (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId)).MergeFailureReason
            .Should().Contain("Required status check");
        (await db.TaskMessages.AsNoTracking()
                .CountAsync(m => m.TaskId == taskId && m.MessageType == MessageType.Error))
            .Should().Be(2);
    }

    [Fact]
    public async Task SetTaskMergeFailure_LongReason_IsTruncated()
    {
        var taskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await api.SetTaskMergeFailureAsync(taskId, new string('x', 5000));

        (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId)).MergeFailureReason!.Length
            .Should().Be(1000, "the column is capped at 1000 chars");
    }

    [Fact]
    public async Task MarkTaskMerged_ClearsMergeFailureReason()
    {
        var taskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await api.SetTaskMergeFailureAsync(taskId, "merge conflict");
        await api.MarkTaskMergedAsync(taskId);

        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.IsMerged.Should().BeTrue();
        task.MergeFailureReason.Should().BeNull("a successful merge must clear the badge");
    }

    [Fact]
    public async Task SetTaskMergeFailure_UnknownTask_ReturnsNull()
    {
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        (await api.SetTaskMergeFailureAsync(Ulid.NewUlid(), "whatever")).Should().BeNull();
    }

    [Fact]
    public async Task ListTasksAwaitingMerge_IncludesArmedResolution_FromAnyLane()
    {
        var armedTaskId = await SeedTaskAsync();
        var idleTaskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        // Both tasks have an open PR but sit outside Done (freshly created lane) — only
        // the one a merge click armed carries a pending resolution strategy.
        await db.Tasks.Where(t => t.Id == armedTaskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.PrUrl, "https://github.com/test/merge-failure/pull/12")
                .SetProperty(t => t.MergeResolutionStrategy, "combine"));
        await db.Tasks.Where(t => t.Id == idleTaskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.PrUrl, "https://github.com/test/merge-failure/pull/13"));

        var awaiting = await api.ListTasksAwaitingMergeAsync();

        awaiting.Should().Contain(t => t.Id == armedTaskId,
            "a merge click that hit conflicts arms resolution from any lane, and the sweep must see it");
        awaiting.Should().NotContain(t => t.Id == idleTaskId,
            "a task outside Done with no pending resolution is not merge-eligible");
    }

    [Fact]
    public async Task SetTaskMergeFailure_SendsDoneTaskBackToCodeReview()
    {
        var taskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        // Review approved: Done, Completed, with an open PR still waiting on the merge sweep.
        await db.Tasks.Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CurrentLane, Lane.Done)
                .SetProperty(t => t.Status, Orkka.Modules.Tasks.Contracts.TaskStatus.Completed)
                .SetProperty(t => t.CompletedAt, DateTimeOffset.UtcNow)
                .SetProperty(t => t.PrUrl, "https://github.com/test/merge-failure/pull/20"));

        await api.SetTaskMergeFailureAsync(taskId, "the branch could not be pushed");

        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.CurrentLane.Should().Be(Lane.CodeReview,
            "Done means merged — unmerged work goes back to the lane that points at the PR");
        task.Status.Should().Be(Orkka.Modules.Tasks.Contracts.TaskStatus.WaitingCodeReview,
            "velocity and doc sync gate on Completed, so the status is what stops it counting as delivered");

        // Still swept: the demotion must not cost the task its automatic merge retries.
        var awaiting = await api.ListTasksAwaitingMergeAsync();
        awaiting.Should().Contain(t => t.Id == taskId);
    }

    [Fact]
    public async Task SetTaskMergeFailure_RepeatedReason_StillCorrectsTheLane()
    {
        var taskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        // A task carrying the reason but still in Done — what every task recorded before
        // this rule existed looks like. The poller's next identical retry must fix it.
        await db.Tasks.Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CurrentLane, Lane.Done)
                .SetProperty(t => t.Status, Orkka.Modules.Tasks.Contracts.TaskStatus.Completed)
                .SetProperty(t => t.PrUrl, "https://github.com/test/merge-failure/pull/21")
                .SetProperty(t => t.MergeFailureReason, "branch protection blocked the merge"));

        var update = await api.SetTaskMergeFailureAsync(taskId, "branch protection blocked the merge");

        update!.Changed.Should().BeFalse("an unchanged reason must not spam the task thread");
        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.CurrentLane.Should().Be(Lane.CodeReview);
    }

    [Fact]
    public async Task MarkTaskMerged_ReturnsDemotedTaskToDone()
    {
        var taskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await db.Tasks.Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CurrentLane, Lane.Done)
                .SetProperty(t => t.Status, Orkka.Modules.Tasks.Contracts.TaskStatus.Completed)
                .SetProperty(t => t.PrUrl, "https://github.com/test/merge-failure/pull/22"));

        await api.SetTaskMergeFailureAsync(taskId, "merge conflict with main");
        await api.MarkTaskMergedAsync(taskId, "abc123");

        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.CurrentLane.Should().Be(Lane.Done, "the merge landed, so the task is finished after all");
        task.Status.Should().Be(Orkka.Modules.Tasks.Contracts.TaskStatus.Completed);
        task.CompletedAt.Should().NotBeNull();
        task.MergeFailureReason.Should().BeNull();
    }

    [Fact]
    public async Task MarkPrClosed_SendsDoneTaskBackToCodeReview()
    {
        var taskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await db.Tasks.Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CurrentLane, Lane.Done)
                .SetProperty(t => t.Status, Orkka.Modules.Tasks.Contracts.TaskStatus.Completed)
                .SetProperty(t => t.CompletedAt, DateTimeOffset.UtcNow)
                .SetProperty(t => t.PrUrl, "https://github.com/test/merge-failure/pull/23"));

        await api.MarkPrClosedAsync(taskId);

        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.CurrentLane.Should().Be(Lane.CodeReview,
            "work rejected on the forge never shipped, so it cannot sit in Done");
    }

    [Fact]
    public async Task MarkPrClosed_ClearsPendingMergeResolution()
    {
        var taskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await db.Tasks.Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.PrUrl, "https://github.com/test/merge-failure/pull/14")
                .SetProperty(t => t.MergeResolutionStrategy, "combine")
                .SetProperty(t => t.MergeResolutionGuidance, "keep both sides"));

        await api.MarkPrClosedAsync(taskId);

        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.PrClosedAt.Should().NotBeNull();
        task.MergeResolutionStrategy.Should().BeNull(
            "a closed PR leaves the merge sweep, so a strategy left set would spin the merge page forever");
        task.MergeResolutionGuidance.Should().BeNull();

        var awaiting = await api.ListTasksAwaitingMergeAsync();
        awaiting.Should().NotContain(t => t.Id == taskId);
    }

    [Fact]
    public async Task ListRecentlyMergedTasks_ReturnsMergedTask_ExcludesUnmerged()
    {
        var mergedTaskId = await SeedTaskAsync();
        var unmergedTaskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        await api.MarkTaskMergedAsync(mergedTaskId);

        var recent = await api.ListRecentlyMergedTasksAsync(DateTimeOffset.UtcNow.AddHours(-1));

        recent.Should().Contain(t => t.Id == mergedTaskId,
            "the merged-worktree sweep must see freshly merged tasks");
        recent.Should().NotContain(t => t.Id == unmergedTaskId,
            "unmerged tasks still need their worktrees");
    }

    [Fact]
    public async Task ListRecentlyMergedTasks_ExcludesTasksMergedBeforeWindow()
    {
        var taskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await api.MarkTaskMergedAsync(taskId);
        // Push the merge outside the lookback window — old merges are the GC sweep's job.
        await db.Tasks.Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CompletedAt, DateTimeOffset.UtcNow.AddDays(-10))
                .SetProperty(t => t.UpdatedAt, DateTimeOffset.UtcNow.AddDays(-10)));

        var recent = await api.ListRecentlyMergedTasksAsync(DateTimeOffset.UtcNow.AddHours(-72));

        recent.Should().NotContain(t => t.Id == taskId);
    }
}
