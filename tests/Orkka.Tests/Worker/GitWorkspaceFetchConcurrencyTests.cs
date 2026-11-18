namespace Orkka.Tests.Worker;

using Orkka.Modules.Execution.Git;
using Orkka.Modules.SourceControl.Contracts;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Regression cover for the failure Ruan hit once two tasks ran against one repo:
///
///   cannot lock ref 'refs/remotes/origin/main': is at {a} but expected {b}
///   ! {sha} main -> origin/main (unable to update local ref)
///
/// Every worktree of a repo shares that repo's <c>refs/remotes/*</c>, so concurrent fetches are
/// concurrent compare-and-swaps on one ref and the loser fails. Adding a base sync to every phase
/// boundary multiplied the fetch traffic and turned a rare race into a routine one.
///
/// These run real git against real worktrees, because the bug lives entirely in how git behaves
/// when two processes touch one ref store — a mocked git would pass no matter what we did.
/// </summary>
public sealed class GitWorkspaceFetchConcurrencyTests : IDisposable
{
    private readonly string _basePath;
    private readonly string _originPath;
    private readonly string _authorPath;
    private readonly string _barePath;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();
    private readonly GitWorkspaceManager _manager;

    private const string Identity = "-c user.name=Orkka -c user.email=orkka@localhost";

    public GitWorkspaceFetchConcurrencyTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"orkka-fetch-race-tests-{Ulid.NewUlid()}");
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
    }

    /// <summary>origin/main with one commit, the manager's bare mirror, and one worktree per
    /// task id — the shape of a project running several tasks against a single repo.</summary>
    private async Task<List<Ulid>> SetupAsync(int taskCount)
    {
        Directory.CreateDirectory(_originPath);
        await RunGit(_originPath, "init --bare --initial-branch=main");

        await RunGit(_basePath, $"clone \"{_originPath}\" author");
        await RunGit(_authorPath, "checkout -b main");
        await File.WriteAllTextAsync(Path.Combine(_authorPath, "README.md"), "# app\n");
        await RunGit(_authorPath, "add -A");
        await RunGit(_authorPath, $"{Identity} commit -m \"init\"");
        await RunGit(_authorPath, "push -u origin main");

        Directory.CreateDirectory(Path.GetDirectoryName(_barePath)!);
        await RunGit(Path.GetDirectoryName(_barePath)!, $"clone --bare \"{_originPath}\" bare");
        await RunGit(_barePath, "config remote.origin.fetch \"+refs/heads/*:refs/remotes/origin/*\"");
        await RunGit(_barePath, "fetch origin");

        var taskIds = new List<Ulid>();
        for (var i = 0; i < taskCount; i++)
        {
            var taskId = Ulid.NewUlid();
            var worktreePath = _manager.GetWorktreePath(_orgId, _repoId, taskId);
            Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
            await RunGit(_barePath, $"worktree add \"{worktreePath}\" -b orkka/task-{i} origin/main");

            // Each task carries a commit of its own, in its own file — no conflicts, so any
            // failure here is the ref race and nothing else.
            await File.WriteAllTextAsync(Path.Combine(worktreePath, $"task-{i}.txt"), $"work {i}\n");
            await RunGit(worktreePath, "add -A");
            await RunGit(worktreePath, $"{Identity} commit -m \"task {i}\"");
            taskIds.Add(taskId);
        }

        return taskIds;
    }

    private async Task PushToBaseAsync(string file, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(_authorPath, file), content);
        await RunGit(_authorPath, "add -A");
        await RunGit(_authorPath, $"{Identity} commit -m \"{file}\"");
        await RunGit(_authorPath, "push origin main");
    }

    [Fact]
    public async Task SyncBaseBranch_TwoTasksAtOnce_NeitherLosesARefRace()
    {
        var taskIds = await SetupAsync(taskCount: 2);
        await PushToBaseAsync("landed.txt", "from another task\n");

        var results = await Task.WhenAll(taskIds.Select(id =>
            _manager.SyncBaseBranchAsync(_orgId, _repoId, id, "main", leaveConflictsInProgress: true)));

        foreach (var result in results)
        {
            result.IsSuccess.Should().BeTrue(result.Error);
            result.Value!.Status.Should().Be(BaseSyncStatus.Merged);
        }
    }

    [Fact]
    public async Task SyncBaseBranch_EightTasksAtOnce_AllSucceed()
    {
        // Well past MaxParallelTasks. The gate serializes per repo, so contention should only
        // ever cost wall-clock — never a failed sync.
        var taskIds = await SetupAsync(taskCount: 8);
        await PushToBaseAsync("landed.txt", "from another task\n");

        var results = await Task.WhenAll(taskIds.Select(id =>
            _manager.SyncBaseBranchAsync(_orgId, _repoId, id, "main", leaveConflictsInProgress: true)));

        results.Should().OnlyContain(r => r.IsSuccess);
        results.Select(r => r.Value!.CommitsBehind).Should().OnlyContain(c => c == 1);
    }

    [Fact]
    public async Task MixedFetchingOperations_Concurrently_DoNotCollide()
    {
        // The real pipeline interleaves these against one repo: a phase boundary syncing, the
        // merge sweep dry-running conflicts, a verification checking a resolved merge.
        var taskIds = await SetupAsync(taskCount: 3);
        await PushToBaseAsync("landed.txt", "from another task\n");

        var sync = _manager.SyncBaseBranchAsync(
            _orgId, _repoId, taskIds[0], "main", leaveConflictsInProgress: true);
        var verify = _manager.VerifyMergeAsync(_orgId, _repoId, taskIds[1], "main");
        var conflicts = _manager.GetMergeConflictsAsync(_orgId, _repoId, "orkka/task-2", "main");

        await Task.WhenAll(sync, verify, conflicts);

        (await sync).IsSuccess.Should().BeTrue((await sync).Error);
        (await verify).IsSuccess.Should().BeTrue((await verify).Error);
        (await conflicts).IsSuccess.Should().BeTrue((await conflicts).Error);
    }

    /// <summary>
    /// A mirror that cannot be refreshed is a failure with a code, never a task that proceeds on
    /// stale refs. Continuing "on the mirror it has" is how a milestone branch created on the
    /// forge never reached a worktree ("invalid reference origin/…") and how a dead credential
    /// surfaced only at push time, a whole run later.
    /// </summary>
    /// <summary>Two tasks entering a phase in the same second both refresh the same mirror.
    /// The fetch was gated; the config writes around it were not, and on Windows git's rename
    /// of <c>config</c> under a concurrent reader answers "Permission denied" (prod,
    /// 2026-09-16). Refreshes of one mirror serialize: every caller succeeds, the mirror is intact.</summary>
    [Fact]
    public async Task EnsureBareClone_ConcurrentRefreshesOfOneMirror_AllSucceed()
    {
        await SetupAsync(taskCount: 0);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => _manager.EnsureBareCloneAsync(_orgId, _repoId, _originPath)));

        results.Should().OnlyContain(r => r.IsSuccess,
            "refreshes of one mirror wait for each other instead of racing on its config");
        results.Select(r => r.Value).Should().OnlyContain(p => p == _barePath);
        (await File.ReadAllTextAsync(Path.Combine(_barePath, "config"))).Should().Contain("[remote \"origin\"]");
    }

    /// <summary>Two requests for one task's workspace at once — the Worker restarted while an
    /// interactive session was being prepared, and its replacement asked again while the
    /// runner's first clone was still writing (prod, 2026-09-17, StoreFront STO-23: "The
    /// process cannot access the file … because it is being used by another process").
    /// Requests for one worktree serialize: the second finds the first's clone and reuses it.</summary>
    [Fact]
    public async Task CreateWorktree_TwoRequestsForOneTask_ShareOneClone()
    {
        await SetupAsync(taskCount: 0);
        var taskId = Ulid.NewUlid();

        var results = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => _manager.CreateWorktreeAsync(_orgId, _repoId, taskId, "orkka/T-1", "main")));

        results.Should().OnlyContain(r => r.IsSuccess, string.Join(" | ", results.Select(r => r.Error)));
        var path = _manager.GetWorktreePath(_orgId, _repoId, taskId);
        results.Select(r => r.Value).Should().OnlyContain(p => p == path);
        (await File.ReadAllTextAsync(Path.Combine(path, ".git", "HEAD"))).Trim().Should().Be("ref: refs/heads/orkka/T-1");
    }

    [Fact]
    public async Task EnsureBareClone_RefreshFails_IsAFailure_NotAStaleMirror()
    {
        await SetupAsync(taskCount: 1);

        var result = await _manager.EnsureBareCloneAsync(
            _orgId, _repoId, Path.Combine(_basePath, "gone.git"));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("could not be refreshed");
        Directory.Exists(_barePath).Should().BeTrue("a refresh failure never destroys the mirror");
    }

    [Fact]
    public async Task EnsureBareClone_NoMirrorAndNoRemote_StillFails()
    {
        // The other half of the rule above: with nothing on disk there is nothing to fall back
        // to, so this must stay fatal rather than handing back an empty workspace.
        Directory.CreateDirectory(_basePath);

        var result = await _manager.EnsureBareCloneAsync(
            _orgId, _repoId, Path.Combine(_basePath, "does-not-exist.git"));

        result.IsFailure.Should().BeTrue();
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
}
