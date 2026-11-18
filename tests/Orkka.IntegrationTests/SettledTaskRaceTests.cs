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
/// A review agent's verdict must never resurrect a task that was settled while it was running.
///
/// The observed failure (2026-08-23, task DEN-1): a human approved at 15:28:00, the merge poller
/// landed the PR, and at 15:28:01 the still-running reviewer produced an unreadable verdict and
/// stamped WaitingCodeReview over the finished task. The board then showed the card sitting in
/// the Done lane wearing an "Awaiting Code Review" badge, with a worktree nobody would clean up
/// and a chat message asking a human to review a pull request that had already merged.
///
/// An agent phase is fire-and-forget and can run for minutes while the poll cycle that launched
/// it carries on in the same process — so this is reachable on a single replica, and no
/// in-process lock would have prevented it. The guard has to be at the write.
/// </summary>
[Collection("Integration")]
public sealed class SettledTaskRaceTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Ulid> SeedTaskAsync(string name)
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest(name, "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest($"{name}-repo", $"https://github.com/test/{name}.git",
                "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/tasks",
            new CreateTaskRequest(repo!.Id, "Clinic & staff data model", "desc", "Medium"));
        var task = await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        return task!.Id;
    }

    /// <summary>Puts the task where the merge poller leaves it: merged, Done, Completed.</summary>
    private async Task SettleAsync(IServiceScope scope, Ulid taskId)
    {
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var entity = await db.Tasks.FirstAsync(t => t.Id == taskId);
        entity.CurrentLane = Lane.CodeReview;
        entity.Status = TaskStatus.WaitingCodeReview;
        entity.BranchName = "orkka/DEN-1";
        entity.PrUrl = "https://github.com/test/x/pull/2";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await scope.ServiceProvider.GetRequiredService<ITasksApi>()
            .MarkTaskMergedAsync(taskId, "merge-sha");
    }

    private static async Task<(Lane Lane, TaskStatus Status)> ReadAsync(
        IServiceScope scope, Ulid taskId)
    {
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var entity = await db.Tasks.AsNoTracking().FirstAsync(t => t.Id == taskId);
        return (entity.CurrentLane, entity.Status);
    }

    [Fact]
    public async Task AnUnreadableVerdict_CannotUnsettleAMergedTask()
    {
        var taskId = await SeedTaskAsync("race-unreadable");
        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        await SettleAsync(scope, taskId);

        // What the reviewer did one second after the merge.
        var wrote = await api.SetTaskWaitingCodeReviewAsync(taskId);

        wrote.Should().BeFalse("the task had already merged — there is nothing left to review");
        var (lane, status) = await ReadAsync(scope, taskId);
        lane.Should().Be(Lane.Done);
        status.Should().Be(TaskStatus.Completed, "a merged task must not read as awaiting review");
    }

    [Fact]
    public async Task ALateRejection_CannotSendMergedCodeBackToDeveloping()
    {
        var taskId = await SeedTaskAsync("race-reject");
        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        await SettleAsync(scope, taskId);

        // The worse half of the same race: a reviewer that finishes with "request changes"
        // after the merge would reopen shipped work for another development pass.
        var transition = await api.ApplyCodeReviewVerdictAsync(taskId, approved: false, maxReviewCycles: 3);

        transition.Should().BeNull();
        var (lane, status) = await ReadAsync(scope, taskId);
        lane.Should().Be(Lane.Done);
        status.Should().Be(TaskStatus.Completed);
    }

    [Fact]
    public async Task ALateApproval_DoesNotRestampAFinishedTask()
    {
        var taskId = await SeedTaskAsync("race-approve");
        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        await SettleAsync(scope, taskId);
        var before = await ReadAsync(scope, taskId);

        var transition = await api.ApplyCodeReviewVerdictAsync(taskId, approved: true, maxReviewCycles: 3);

        transition.Should().BeNull("agreeing with an outcome that already happened is not a transition");
        (await ReadAsync(scope, taskId)).Should().Be(before);
    }

    [Fact]
    public async Task ALateQaEscalation_CannotUnsettleAMergedTask()
    {
        var taskId = await SeedTaskAsync("race-qa");
        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        await SettleAsync(scope, taskId);

        var wrote = await api.SetTaskWaitingQaReviewAsync(taskId);

        wrote.Should().BeFalse();
        (await ReadAsync(scope, taskId)).Status.Should().Be(TaskStatus.Completed);
    }

    [Fact]
    public async Task AnUnsettledTask_StillParksForReviewNormally()
    {
        var taskId = await SeedTaskAsync("race-normal");
        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var entity = await db.Tasks.FirstAsync(t => t.Id == taskId);
        entity.CurrentLane = Lane.CodeReview;
        entity.Status = TaskStatus.Active;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The guard must only catch the race. An ordinary escalation — the overwhelmingly
        // common case — has to keep working, or reviews would silently stop parking for humans.
        var wrote = await api.SetTaskWaitingCodeReviewAsync(taskId);

        wrote.Should().BeTrue();
        (await ReadAsync(scope, taskId)).Status.Should().Be(TaskStatus.WaitingCodeReview);
    }

    [Fact]
    public async Task AVerdictFromAScopeThatTrackedTheTaskBeforeTheMerge_IsStillDiscarded()
    {
        var taskId = await SeedTaskAsync("race-stale-scope");

        // The orchestrator's shape: ONE DI scope lives for the whole review phase, and its
        // context tracks the task from phase start. DEN-3's sequence — tracked at 20:03,
        // merged by other processes at 20:08, rejection applied through the stale scope at
        // 20:09. FirstOrDefaultAsync on a tracking context returns the TRACKED instance and
        // discards the fresh database values, so a guard reading it judges six-minute-old
        // state. The guard must read the database, not the scope's memory.
        using var phaseScope = factory.Services.CreateScope();
        var phaseApi = phaseScope.ServiceProvider.GetRequiredService<ITasksApi>();
        await phaseApi.SetTaskBranchAsync(taskId, "orkka/DEN-3");  // tracks the entity, pre-merge

        using (var otherProcess = factory.Services.CreateScope())
        {
            var db = otherProcess.ServiceProvider.GetRequiredService<TasksDbContext>();
            var entity = await db.Tasks.FirstAsync(t => t.Id == taskId);
            entity.CurrentLane = Lane.CodeReview;
            entity.PrUrl = "https://github.com/test/x/pull/3";
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await otherProcess.ServiceProvider.GetRequiredService<ITasksApi>()
                .MarkTaskMergedAsync(taskId, "merge-sha");
        }

        // The late rejection, arriving through the scope that still remembers 20:03.
        var transition = await phaseApi.ApplyCodeReviewVerdictAsync(taskId, approved: false, maxReviewCycles: 5);

        transition.Should().BeNull("the task merged while the reviewer ran, however stale this scope's copy is");
        using var check = factory.Services.CreateScope();
        var (lane, status) = await ReadAsync(check, taskId);
        lane.Should().Be(Lane.Done);
        status.Should().Be(TaskStatus.Completed, "a late rejection must not reopen merged work");
    }

    [Fact]
    public async Task ALateQaVerdict_OnASettledTask_IsDiscarded()
    {
        var taskId = await SeedTaskAsync("race-qa-verdict");
        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        await SettleAsync(scope, taskId);

        // A QA run lasts minutes; an approve-and-merge can land underneath it exactly like it
        // does under a review.
        var transition = await api.ApplyQaVerdictAsync(taskId, passed: false, maxQaCycles: 3);

        transition.Should().BeNull();
        (await ReadAsync(scope, taskId)).Status.Should().Be(TaskStatus.Completed);
    }

    [Fact]
    public async Task AMergedTaskAHumanMovedBackToReview_AcceptsTheNextVerdict()
    {
        var taskId = await SeedTaskAsync("race-reopened");
        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        await SettleAsync(scope, taskId);

        // The user pulled it back out of Done. IsMerged is still true — nothing ever clears it —
        // and that is exactly why "settled" must mean "in Done", not "was ever merged".
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var entity = await db.Tasks.FirstAsync(t => t.Id == taskId);
        entity.CurrentLane = Lane.CodeReview;
        entity.Status = TaskStatus.Active;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Observed in production: with IsMerged treated as settled, this returned null, the task
        // was re-picked as resumable, reviewed again, returned null again — a full code review a
        // minute, forever, with no exit. The verdict has to apply.
        var transition = await api.ApplyCodeReviewVerdictAsync(taskId, approved: true, maxReviewCycles: 3);

        transition.Should().NotBeNull("a task the user deliberately reopened is not settled");
        var (lane, status) = await ReadAsync(scope, taskId);
        lane.Should().Be(Lane.Done);
        status.Should().Be(TaskStatus.Completed);
    }

    [Fact]
    public async Task ATaskDemotedOutOfDoneByAFailedMerge_IsNotTreatedAsSettled()
    {
        var taskId = await SeedTaskAsync("race-demoted");
        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var entity = await db.Tasks.FirstAsync(t => t.Id == taskId);
        // Reached Done once (CompletedAt set) but the merge failed and it was demoted back —
        // IsMerged is false, so review must still be able to park it for a human.
        entity.CurrentLane = Lane.CodeReview;
        entity.Status = TaskStatus.WaitingCodeReview;
        entity.CompletedAt = DateTimeOffset.UtcNow;
        entity.MergeFailureReason = "conflicts";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var wrote = await api.SetTaskWaitingCodeReviewAsync(taskId);

        wrote.Should().BeTrue("work that never landed is still reviewable");
    }
}
