namespace Orkka.IntegrationTests;

using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Features.CodeChanges;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The permanent code-change history: a snapshot row per dev cycle, deduped by diff hash so a
/// re-entered phase that changed nothing cannot pad the history, plus the long-empty
/// TaskExecution.FilesChanged finally being written.
/// </summary>
[Collection("Integration")]
public sealed class CodeChangeSnapshotTests(OrkkaApiFactory factory)
{
    private const string SampleDiff = """
        diff --git a/src/app.cs b/src/app.cs
        index 1234567..89abcde 100644
        --- a/src/app.cs
        +++ b/src/app.cs
        @@ -1,2 +1,3 @@
         using System;
        +var added = 1;
        diff --git a/new.txt b/new.txt
        new file mode 100644
        index 0000000..e69de29
        --- /dev/null
        +++ b/new.txt
        @@ -0,0 +1,1 @@
        +hello
        """;

    private readonly HttpClient _client = factory.CreateClient();

    private IServiceScope CreateScope() => factory.Services.CreateScope();

    private async Task<(Ulid TaskId, Ulid RepoId)> SeedTaskAsync()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Snapshot Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("cs-repo", "https://github.com/test/cs.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/tasks",
            new CreateTaskRequest(repo!.Id, "Snapshot task", "desc", "High"));
        var task = await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);

        return (task!.Id, repo.Id);
    }

    [Fact]
    public async Task SaveSnapshot_ParsesStatsAndFillsExecutionFilesChanged()
    {
        var (taskId, repoId) = await SeedTaskAsync();

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        var start = await api.StartExecutionAsync(
            taskId, Lane.Developing, agentId: null, moveTaskToLane: true, setStartedAt: false);

        await api.SaveCodeChangeSnapshotAsync(
            taskId, start!.ExecutionId, repoId, Lane.Developing,
            "orkka/CS-1", "main", SampleDiff, isTruncated: false);

        var snapshot = await db.CodeChangeSnapshots.AsNoTracking().SingleAsync(s => s.TaskId == taskId);
        snapshot.FileCount.Should().Be(2);
        snapshot.Additions.Should().Be(2);
        snapshot.Deletions.Should().Be(0);
        snapshot.Lane.Should().Be(Lane.Developing);
        snapshot.BranchName.Should().Be("orkka/CS-1");
        snapshot.DiffHash.Should().NotBeNullOrEmpty();
        snapshot.FilesJson.Should().Contain("new.txt").And.Contain("added");

        var execution = await db.TaskExecutions.AsNoTracking().SingleAsync(e => e.Id == start.ExecutionId);
        execution.FilesChanged.Should().NotBeNull();
        execution.FilesChanged.Should().Contain("src/app.cs").And.Contain("\"action\"");
    }

    [Fact]
    public async Task SaveSnapshot_IdenticalDiff_DoesNotDuplicate()
    {
        var (taskId, repoId) = await SeedTaskAsync();

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await api.SaveCodeChangeSnapshotAsync(
            taskId, null, repoId, Lane.Developing, "orkka/CS-1", "main", SampleDiff, false);
        await api.SaveCodeChangeSnapshotAsync(
            taskId, null, repoId, Lane.Developing, "orkka/CS-1", "main", SampleDiff, false);

        (await db.CodeChangeSnapshots.CountAsync(s => s.TaskId == taskId)).Should().Be(1,
            "a re-entered phase that changed nothing must not pad the history");
    }

    [Fact]
    public async Task SaveSnapshot_OnlyIfMissing_SkipsWhenBranchAlreadyCaptured()
    {
        var (taskId, repoId) = await SeedTaskAsync();

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await api.SaveCodeChangeSnapshotAsync(
            taskId, null, repoId, Lane.Developing, "orkka/CS-1", "main", SampleDiff, false);

        // The CodeReview backfill re-reads at a different cap, so its bytes differ — the
        // branch guard, not the hash, is what keeps it a no-op.
        await api.SaveCodeChangeSnapshotAsync(
            taskId, null, repoId, Lane.CodeReview, "orkka/CS-1", "main",
            SampleDiff + "\n", isTruncated: false, onlyIfMissing: true);

        var snapshot = await db.CodeChangeSnapshots.AsNoTracking().SingleAsync(s => s.TaskId == taskId);
        snapshot.Lane.Should().Be(Lane.Developing, "the original capture must win over the backfill");
    }

    [Fact]
    public async Task SaveSnapshot_DifferentDiff_AccumulatesHistory()
    {
        var (taskId, repoId) = await SeedTaskAsync();

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await api.SaveCodeChangeSnapshotAsync(
            taskId, null, repoId, Lane.Developing, "orkka/CS-1", "main", SampleDiff, false);
        await api.SaveCodeChangeSnapshotAsync(
            taskId, null, repoId, Lane.Developing, "orkka/CS-1", "main",
            SampleDiff + "\n+more", false);

        (await db.CodeChangeSnapshots.CountAsync(s => s.TaskId == taskId)).Should().Be(2,
            "a task bounced back through Developing accumulates one snapshot per cycle");
    }

    // ---- Live snapshot: the Code View's picture of a run in progress ---------------------

    [Fact]
    public async Task LiveSnapshot_IsOneRowPerTask_OverwrittenWithRisingRevision()
    {
        var (taskId, repoId) = await SeedTaskAsync();

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        var first = await api.UpsertLiveCodeChangeSnapshotAsync(
            taskId, null, repoId, Lane.Developing, "orkka/CS-1", "main", SampleDiff, false);
        first.Should().NotBeNull();
        first!.Revision.Should().Be(1);
        first.FileCount.Should().Be(2);
        first.Files.Select(f => f.Path).Should().BeEquivalentTo("src/app.cs", "new.txt");

        // Same bytes again → nothing to say.
        (await api.UpsertLiveCodeChangeSnapshotAsync(
            taskId, null, repoId, Lane.Developing, "orkka/CS-1", "main", SampleDiff, false))
            .Should().BeNull();

        var second = await api.UpsertLiveCodeChangeSnapshotAsync(
            taskId, null, repoId, Lane.Developing, "orkka/CS-1", "main", SampleDiff + "\n+more", false);
        second.Should().NotBeNull();
        second!.Revision.Should().Be(2);
        second.SnapshotId.Should().Be(first.SnapshotId, "the live row is overwritten, not duplicated");

        var rows = await db.CodeChangeSnapshots.AsNoTracking().Where(s => s.TaskId == taskId).ToListAsync();
        rows.Should().ContainSingle();
        rows[0].IsLive.Should().BeTrue();
        rows[0].Revision.Should().Be(2);
    }

    [Fact]
    public async Task LiveSnapshot_ListsFirst_AndThePermanentSnapshotReplacesIt()
    {
        var (taskId, repoId) = await SeedTaskAsync();

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        // An older permanent snapshot from a previous cycle, then a live one from this run.
        await api.SaveCodeChangeSnapshotAsync(
            taskId, null, repoId, Lane.Developing, "orkka/CS-1", "main", SampleDiff, false);
        await api.UpsertLiveCodeChangeSnapshotAsync(
            taskId, null, repoId, Lane.Developing, "orkka/CS-1", "main", SampleDiff + "\n+live", false);

        var listed = await _client.GetFromJsonAsync<List<CodeChangeSnapshotSummaryResponse>>(
            $"/api/tasks/{taskId}/code-changes", TestJsonOptions.Default);
        listed.Should().HaveCount(2);
        listed![0].IsLive.Should().BeTrue("a run in progress is the newest truth");
        listed[0].Revision.Should().Be(1);
        listed[1].IsLive.Should().BeFalse();

        var diff = await _client.GetFromJsonAsync<CodeChangeSnapshotDiffResponse>(
            $"/api/tasks/{taskId}/code-changes/{listed[0].Id}", TestJsonOptions.Default);
        diff!.IsLive.Should().BeTrue();
        // Orkka keeps no copy of the code: the diff is read from the owner's runner, and this
        // host has none connected — so it says so instead of serving a stored copy.
        diff.Diff.Should().BeEmpty();
        diff.Unavailable.Should().Contain("keeps no copy");

        // The run ends: its permanent record supersedes the live mirror — even when that
        // record is byte-identical to the live diff (dedup must not eat the only copy).
        await api.SaveCodeChangeSnapshotAsync(
            taskId, null, repoId, Lane.Developing, "orkka/CS-1", "main", SampleDiff + "\n+live", false);

        var after = await _client.GetFromJsonAsync<List<CodeChangeSnapshotSummaryResponse>>(
            $"/api/tasks/{taskId}/code-changes", TestJsonOptions.Default);
        after.Should().HaveCount(2);
        after!.Should().OnlyContain(s => !s.IsLive);
    }
}
