namespace Orkka.IntegrationTests;

using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// A merge-failure notice demotes a finished task out of Done onto the merge page. When the
/// failure is over without a merge — the resolver fixed the conflict and pushed, but the
/// task's group is not ready to land — the notice must be cleared and the task put back in
/// Done, and while the notice stands the group must count the task as STUCK, not as unfinished
/// work to wait on. Prod, 2026-09-16: two tasks sat in Code Review as "failed merges" for an
/// hour because their own stale notice held their group's gate.
/// </summary>
[Collection("Integration")]
public sealed class MergeFailureClearTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(Ulid ApiTask, Ulid UiTask, Ulid GroupId)> SeedGroupedPairAsync()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Merge Failure Clear Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("api", "https://github.com/test/api.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var ids = new List<Ulid>();
        foreach (var title in new[] { "API half", "UI half" })
        {
            var taskResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/tasks",
                new CreateTaskRequest(repo!.Id, title, "desc", "Medium"));
            var task = await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
            ids.Add(task!.Id);
        }

        var groupId = Ulid.NewUlid();
        using var seed = factory.Services.CreateScope();
        var db = seed.ServiceProvider.GetRequiredService<TasksDbContext>();
        var tasks = await db.Tasks.Where(t => ids.Contains(t.Id)).ToListAsync();
        db.TaskGroups.Add(new TaskGroup
        {
            Id = groupId, OrgId = tasks[0].OrgId, ProjectId = tasks[0].ProjectId,
            Title = "Cross-repo feature", MergePolicy = TaskGroupMergePolicy.Gated,
        });
        foreach (var t in tasks) t.GroupId = groupId;

        // Both halves finished review: Done, with PRs awaiting the merge sweep.
        var n = 1;
        foreach (var t in tasks)
        {
            t.CurrentLane = Lane.Done;
            t.Status = TaskStatus.Completed;
            t.CompletedAt = DateTimeOffset.UtcNow;
            t.PrUrl = $"https://github.com/test/api/pull/{n++}";
        }
        await db.SaveChangesAsync();
        return (ids[0], ids[1], groupId);
    }

    [Fact]
    public async Task AClearedNotice_PutsTheTaskBackInDone_AndTheGroupStopsCountingItAsStuck()
    {
        var (apiTask, _, groupId) = await SeedGroupedPairAsync();

        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        // The notice demotes the finished task onto the merge page …
        await api.SetTaskMergeFailureAsync(apiTask, "Merge conflicts with the base branch — attempting automatic resolution");
        var parked = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == apiTask);
        parked.CurrentLane.Should().Be(Lane.CodeReview);
        parked.Status.Should().Be(TaskStatus.WaitingCodeReview);

        // … and while it stands the group neither waits on the task as unfinished work nor
        // calls it stuck: a conflict notice is the retry path, the sweep's to try again.
        var whileParked = (await api.GetGroupMergeStatesAsync([groupId])).Single();
        whileParked.Ready.Should().BeTrue();

        // The resolver fixed and pushed; the merge waits for the UI half. Clearing the notice
        // returns the task to Done, and the group now only waits on the UI half.
        (await api.ClearTaskMergeFailureAsync(apiTask)).Should().BeTrue();
        var restored = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == apiTask);
        restored.CurrentLane.Should().Be(Lane.Done);
        restored.Status.Should().Be(TaskStatus.Completed);
        restored.MergeFailureReason.Should().BeNull();
        restored.MergeFailureKind.Should().BeNull();

        var afterClear = (await api.GetGroupMergeStatesAsync([groupId])).Single();
        afterClear.Ready.Should().BeTrue("both halves are finished and neither carries a failure any more");

        // Nothing to clear a second time.
        (await api.ClearTaskMergeFailureAsync(apiTask)).Should().BeFalse();
    }

    /// <summary>The retry path is not stuck: a member whose last attempt hit "runner not
    /// connected" (EnvironmentBlocked) still leaves the group ready, so the next sweep tries
    /// the merge again instead of holding the whole group on a notice that is already stale.</summary>
    [Fact]
    public async Task ARetryPathNotice_DoesNotMakeTheGroupStuck()
    {
        var (apiTask, _, groupId) = await SeedGroupedPairAsync();

        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        await api.SetTaskMergeFailureAsync(apiTask,
            "This repository is a folder on your machine, and your runner is not connected.",
            MergeFailureKind.EnvironmentBlocked);

        var state = (await api.GetGroupMergeStatesAsync([groupId])).Single();
        state.Ready.Should().BeTrue("a retry-path failure is the sweep's to retry, not a reason to hold the group");

        await api.SetTaskMergeFailureAsync(apiTask, "Automatic resolution ran out of moves.", MergeFailureKind.ResolutionExhausted);
        var terminal = (await api.GetGroupMergeStatesAsync([groupId])).Single();
        terminal.Ready.Should().BeFalse();
        terminal.BlockedBy.Should().StartWith("Cannot merge");
    }

    /// <summary>A merged task has no merge to fail. The late loser of a merge race — the
    /// resolver's own push, refused after the sweep had landed the same PR (prod, 2026-09-16,
    /// StoreFront #19) — must not demote shipped work out of Done or stamp "Merge failed" on
    /// it. And landing clears the KIND with the reason: #18 merged still carrying the
    /// EnvironmentBlocked from its one runner-offline attempt.</summary>
    [Fact]
    public async Task AMergedTask_RefusesAFailureNotice_AndLandingClearedTheKindToo()
    {
        var (apiTask, _, _) = await SeedGroupedPairAsync();

        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await api.SetTaskMergeFailureAsync(apiTask, "Your runner is not connected.", MergeFailureKind.EnvironmentBlocked);
        await api.MarkTaskMergedAsync(apiTask, "abc123");

        var merged = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == apiTask);
        merged.IsMerged.Should().BeTrue();
        merged.CurrentLane.Should().Be(Lane.Done);
        merged.MergeFailureReason.Should().BeNull();
        merged.MergeFailureKind.Should().BeNull("the kind goes with the reason");

        var late = await api.SetTaskMergeFailureAsync(apiTask,
            "The merge could not be landed in your repository: NonFastForward");
        late!.Changed.Should().BeFalse();

        var after = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == apiTask);
        after.CurrentLane.Should().Be(Lane.Done);
        after.Status.Should().Be(TaskStatus.Completed);
        after.MergeFailureReason.Should().BeNull();
        after.MergeFailureKind.Should().BeNull();
        (await db.TaskMessages.CountAsync(m => m.TaskId == apiTask && m.Content.Contains("NonFastForward")))
            .Should().Be(0, "no thread message for a failure that did not happen");
    }

    [Fact]
    public async Task ClearingATaskWithoutANotice_ChangesNothing()
    {
        var (_, uiTask, _) = await SeedGroupedPairAsync();

        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        (await api.ClearTaskMergeFailureAsync(uiTask)).Should().BeFalse();
        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == uiTask);
        task.CurrentLane.Should().Be(Lane.Done);
        task.Status.Should().Be(TaskStatus.Completed);
    }
}
