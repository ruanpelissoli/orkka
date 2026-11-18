namespace Orkka.Tests.Worker;

using Orkka.Modules.Execution.Git;
using Orkka.Modules.SourceControl.Contracts;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Real-git coverage for the base-branch sync — the mechanism that stops a task developing
/// against the commit it forked from. Every assertion here is about git's actual behavior
/// (what a merge leaves in the index, what --abort restores), so it runs against real repos
/// rather than a mock: the whole value of the feature is that the worktree ends up in a
/// specific state, and a stubbed git cannot tell us whether it does.
///
/// Topology mirrors production: an "origin" bare repo, the manager's bare mirror of it, and a
/// linked worktree on a task branch.
/// </summary>
public sealed class GitWorkspaceBaseSyncTests : IDisposable
{
    private readonly string _basePath;
    private readonly string _originPath;
    private readonly string _authorPath;
    private readonly string _barePath;
    private readonly string _worktreePath;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();
    private readonly Ulid _taskId = Ulid.NewUlid();
    private readonly GitWorkspaceManager _manager;

    private const string Identity = "-c user.name=Orkka -c user.email=orkka@localhost";

    public GitWorkspaceBaseSyncTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"orkka-base-sync-tests-{Ulid.NewUlid()}");
        _originPath = Path.Combine(_basePath, "origin.git");
        _authorPath = Path.Combine(_basePath, "author");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = Path.Combine(_basePath, "cache"),
            })
            .Build();

        _manager = new GitWorkspaceManager(config, Mock.Of<ILogger<GitWorkspaceManager>>());
        _barePath = _manager.GetBareRepoPath(_orgId, _repoId);
        _worktreePath = _manager.GetWorktreePath(_orgId, _repoId, _taskId);
    }

    /// <summary>
    /// origin/main with one commit, a second clone the "other task" pushes from, the manager's
    /// bare mirror, and a task worktree branched from origin/main carrying one commit of its own.
    /// </summary>
    private async Task SetupAsync()
    {
        Directory.CreateDirectory(_originPath);
        await RunGit(_originPath, "init --bare --initial-branch=main");

        Directory.CreateDirectory(_authorPath);
        await RunGit(_basePath, $"clone \"{_originPath}\" author");
        await RunGit(_authorPath, "checkout -b main");
        await File.WriteAllTextAsync(Path.Combine(_authorPath, "shared.txt"), "line one\n");
        await File.WriteAllTextAsync(Path.Combine(_authorPath, "untouched.txt"), "stable\n");
        await RunGit(_authorPath, "add -A");
        await RunGit(_authorPath, $"{Identity} commit -m \"init\"");
        await RunGit(_authorPath, "push -u origin main");

        Directory.CreateDirectory(Path.GetDirectoryName(_barePath)!);
        await RunGit(Path.GetDirectoryName(_barePath)!, $"clone --bare \"{_originPath}\" bare");
        await RunGit(_barePath, "config remote.origin.fetch \"+refs/heads/*:refs/remotes/origin/*\"");
        await RunGit(_barePath, "fetch origin");

        Directory.CreateDirectory(Path.GetDirectoryName(_worktreePath)!);
        await RunGit(_barePath, $"worktree add \"{_worktreePath}\" -b orkka/task origin/main");

        // The task's own commit, in a file the base branch may or may not also touch.
        await File.WriteAllTextAsync(Path.Combine(_worktreePath, "task-only.txt"), "task work\n");
        await RunGit(_worktreePath, "add -A");
        await RunGit(_worktreePath, $"{Identity} commit -m \"task work\"");
    }

    /// <summary>Another task lands a commit on origin/main.</summary>
    private async Task PushToBaseAsync(string file, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_authorPath, file), content);
        await RunGit(_authorPath, "add -A");
        await RunGit(_authorPath, $"{Identity} commit -m \"{message}\"");
        await RunGit(_authorPath, "push origin main");
    }

    [Fact]
    public async Task SyncBaseBranch_BaseHasNotMoved_ReportsAlreadyCurrent()
    {
        await SetupAsync();

        var result = await _manager.SyncBaseBranchAsync(
            _orgId, _repoId, _taskId, "main", leaveConflictsInProgress: true);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Status.Should().Be(BaseSyncStatus.AlreadyCurrent);
        result.Value.CommitsBehind.Should().Be(0);
        result.Value.ConflictedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task SyncBaseBranch_NonConflictingBaseCommit_MergesAndBringsTheFileIn()
    {
        await SetupAsync();
        await PushToBaseAsync("from-main.txt", "landed elsewhere\n", "another task");

        var result = await _manager.SyncBaseBranchAsync(
            _orgId, _repoId, _taskId, "main", leaveConflictsInProgress: true);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Status.Should().Be(BaseSyncStatus.Merged);
        result.Value.CommitsBehind.Should().Be(1);

        // The point of the whole exercise: the other task's work is now in this worktree,
        // where the agent can still build against it.
        File.Exists(Path.Combine(_worktreePath, "from-main.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_worktreePath, "task-only.txt")).Should().BeTrue();
        (await GitOutput(_worktreePath, "status --porcelain")).Should().BeEmpty();
    }

    [Fact]
    public async Task SyncBaseBranch_Conflict_LeavesTheMergeStagedForTheAgent()
    {
        await SetupAsync();

        // Both sides edit shared.txt — the task first, then the base.
        await File.WriteAllTextAsync(Path.Combine(_worktreePath, "shared.txt"), "task's line\n");
        await RunGit(_worktreePath, "add -A");
        await RunGit(_worktreePath, $"{Identity} commit -m \"task edits shared\"");
        await PushToBaseAsync("shared.txt", "main's line\n", "main edits shared");

        var result = await _manager.SyncBaseBranchAsync(
            _orgId, _repoId, _taskId, "main", leaveConflictsInProgress: true);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Status.Should().Be(BaseSyncStatus.Conflicted);
        result.Value.ConflictedFiles.Should().ContainSingle().Which.Should().Be("shared.txt");

        // Still mid-merge, with markers on disk: this is what the dev agent is handed.
        (await GitOutput(_worktreePath, "diff --name-only --diff-filter=U")).Should().Be("shared.txt");
        (await File.ReadAllTextAsync(Path.Combine(_worktreePath, "shared.txt")))
            .Should().Contain("<<<<<<<").And.Contain("main's line");
    }

    [Fact]
    public async Task SyncBaseBranch_ConflictWithRollback_RestoresTheWorktree()
    {
        await SetupAsync();

        await File.WriteAllTextAsync(Path.Combine(_worktreePath, "shared.txt"), "task's line\n");
        await RunGit(_worktreePath, "add -A");
        await RunGit(_worktreePath, $"{Identity} commit -m \"task edits shared\"");
        await PushToBaseAsync("shared.txt", "main's line\n", "main edits shared");

        var result = await _manager.SyncBaseBranchAsync(
            _orgId, _repoId, _taskId, "main", leaveConflictsInProgress: false);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Status.Should().Be(BaseSyncStatus.Aborted);
        result.Value.ConflictedFiles.Should().ContainSingle().Which.Should().Be("shared.txt");

        // A reviewer or QA run must never find conflict markers, so the rollback has to be
        // total. Compared trimmed: git's checkout applies the platform's line endings.
        (await GitOutput(_worktreePath, "status --porcelain")).Should().BeEmpty();
        (await File.ReadAllTextAsync(Path.Combine(_worktreePath, "shared.txt")))
            .Trim().Should().Be("task's line");
    }

    [Fact]
    public async Task SyncBaseBranch_UncommittedChanges_FailsWithoutTouchingThem()
    {
        await SetupAsync();
        await PushToBaseAsync("from-main.txt", "landed elsewhere\n", "another task");
        await File.WriteAllTextAsync(Path.Combine(_worktreePath, "task-only.txt"), "in progress\n");

        var result = await _manager.SyncBaseBranchAsync(
            _orgId, _repoId, _taskId, "main", leaveConflictsInProgress: true);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("uncommitted");

        // Refusing is the whole point — the agent's unsaved work is still exactly as it left it.
        (await File.ReadAllTextAsync(Path.Combine(_worktreePath, "task-only.txt")))
            .Should().Be("in progress\n");
        File.Exists(Path.Combine(_worktreePath, "from-main.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task SyncBaseBranch_AfterAnAbandonedMerge_StartsFromACleanTree()
    {
        await SetupAsync();

        await File.WriteAllTextAsync(Path.Combine(_worktreePath, "shared.txt"), "task's line\n");
        await RunGit(_worktreePath, "add -A");
        await RunGit(_worktreePath, $"{Identity} commit -m \"task edits shared\"");
        await PushToBaseAsync("shared.txt", "main's line\n", "main edits shared");

        // A previous run left a conflicted merge sitting in the worktree.
        await _manager.SyncBaseBranchAsync(
            _orgId, _repoId, _taskId, "main", leaveConflictsInProgress: true);

        // The next call must not read that leftover state as "the worktree is dirty" and refuse.
        var result = await _manager.SyncBaseBranchAsync(
            _orgId, _repoId, _taskId, "main", leaveConflictsInProgress: true);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Status.Should().Be(BaseSyncStatus.Conflicted);
    }

    [Fact]
    public async Task VerifyMerge_CleanMerge_Completed()
    {
        await SetupAsync();
        await PushToBaseAsync("from-main.txt", "landed elsewhere\n", "another task");
        await _manager.SyncBaseBranchAsync(
            _orgId, _repoId, _taskId, "main", leaveConflictsInProgress: true);

        var result = await _manager.VerifyMergeAsync(_orgId, _repoId, _taskId, "main");

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Completed.Should().BeTrue();
        result.Value.BaseMerged.Should().BeTrue();
        result.Value.UnmergedPaths.Should().BeEmpty();
        result.Value.FilesWithMarkers.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyMerge_MergeStillInProgress_ReportsUnmergedPaths()
    {
        await SetupAsync();
        await File.WriteAllTextAsync(Path.Combine(_worktreePath, "shared.txt"), "task's line\n");
        await RunGit(_worktreePath, "add -A");
        await RunGit(_worktreePath, $"{Identity} commit -m \"task edits shared\"");
        await PushToBaseAsync("shared.txt", "main's line\n", "main edits shared");

        await _manager.SyncBaseBranchAsync(
            _orgId, _repoId, _taskId, "main", leaveConflictsInProgress: true);

        var result = await _manager.VerifyMergeAsync(_orgId, _repoId, _taskId, "main");

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Completed.Should().BeFalse();
        result.Value.UnmergedPaths.Should().ContainSingle().Which.Should().Be("shared.txt");
    }

    /// <summary>
    /// The case the whole verification step exists for: the resolver "finished", the tree is
    /// clean, git considers the merge done — and the file contains conflict markers. Nothing
    /// downstream would have caught this before it reached the base branch.
    /// </summary>
    [Fact]
    public async Task VerifyMerge_MarkersCommitted_IsNotCompleted()
    {
        await SetupAsync();
        await File.WriteAllTextAsync(Path.Combine(_worktreePath, "shared.txt"), "task's line\n");
        await RunGit(_worktreePath, "add -A");
        await RunGit(_worktreePath, $"{Identity} commit -m \"task edits shared\"");
        await PushToBaseAsync("shared.txt", "main's line\n", "main edits shared");

        await _manager.SyncBaseBranchAsync(
            _orgId, _repoId, _taskId, "main", leaveConflictsInProgress: true);

        // Exactly what a careless resolver does: stage the markers and commit the merge.
        await RunGit(_worktreePath, "add -A");
        await RunGit(_worktreePath, $"{Identity} commit --no-edit");

        var result = await _manager.VerifyMergeAsync(_orgId, _repoId, _taskId, "main");

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Completed.Should().BeFalse();
        result.Value.BaseMerged.Should().BeTrue();      // the merge IS committed…
        result.Value.UnmergedPaths.Should().BeEmpty();  // …and git is perfectly happy…
        result.Value.FilesWithMarkers.Should().ContainSingle().Which.Should().Be("shared.txt");
    }

    [Fact]
    public async Task VerifyMerge_MergeAbandoned_ReportsBaseNotMerged()
    {
        await SetupAsync();
        await PushToBaseAsync("from-main.txt", "landed elsewhere\n", "another task");

        // The resolver aborted instead of resolving: spotless worktree, nothing merged.
        var result = await _manager.VerifyMergeAsync(_orgId, _repoId, _taskId, "main");

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Completed.Should().BeFalse();
        result.Value.BaseMerged.Should().BeFalse();
        result.Value.UnmergedPaths.Should().BeEmpty();
        result.Value.FilesWithMarkers.Should().BeEmpty();
    }

    [Fact]
    public async Task RunWorktreeCommand_SuccessAndFailure_ReportExitCodeAndOutput()
    {
        await SetupAsync();

        var ok = await _manager.RunWorktreeCommandAsync(
            _orgId, _repoId, _taskId, "echo build-ok", RepoCommandKind.Build);

        ok.IsSuccess.Should().BeTrue(ok.Error);
        ok.Value!.Succeeded.Should().BeTrue();
        ok.Value.Output.Should().Contain("build-ok");

        // A non-zero exit is a RESULT, not a Result.Failure — the caller needs the compiler's
        // output, and a failed build is exactly the case it exists to read.
        var failed = await _manager.RunWorktreeCommandAsync(
            _orgId, _repoId, _taskId, "echo broken-symbol && exit 7", RepoCommandKind.Build);

        failed.IsSuccess.Should().BeTrue(failed.Error);
        failed.Value!.Succeeded.Should().BeFalse();
        failed.Value.ExitCode.Should().Be(7);
        failed.Value.Output.Should().Contain("broken-symbol");
    }

    [Fact]
    public async Task RunWorktreeCommand_IsNeverCached()
    {
        await SetupAsync();
        var marker = Path.Combine(_worktreePath, "ran.txt");

        await _manager.RunWorktreeCommandAsync(
            _orgId, _repoId, _taskId, "echo first > ran.txt", RepoCommandKind.Build);
        File.Delete(marker);

        // The setup-command path would skip an identical command that already succeeded here.
        // A build check that answers from a marker is answering about code that is no longer
        // in the worktree, which is the entire failure mode it is meant to catch.
        var second = await _manager.RunWorktreeCommandAsync(
            _orgId, _repoId, _taskId, "echo first > ran.txt", RepoCommandKind.Build);

        second.IsSuccess.Should().BeTrue(second.Error);
        File.Exists(marker).Should().BeTrue();
    }

    [Fact]
    public async Task RunWorktreeCommand_NoWorktree_Fails()
    {
        var result = await _manager.RunWorktreeCommandAsync(
            _orgId, _repoId, _taskId, "echo hi", RepoCommandKind.Build);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Worktree does not exist");
    }

    [Fact]
    public async Task SyncBaseBranch_NoWorktree_Fails()
    {
        var result = await _manager.SyncBaseBranchAsync(
            _orgId, _repoId, _taskId, "main", leaveConflictsInProgress: true);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Worktree does not exist");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_basePath))
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(_basePath, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_basePath, recursive: true);
        }
        catch (IOException)
        {
            // A git process may still hold a handle; the temp folder is disposable either way.
        }
    }

    private static async Task RunGit(string workingDirectory, string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = System.Diagnostics.Process.Start(psi)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new Exception($"Git failed ({arguments}): {error}");
    }

    private static async Task<string> GitOutput(string workingDirectory, string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output.Trim();
    }
}
