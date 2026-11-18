namespace Orkka.Tests.Worker;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.Git;
using Orkka.Modules.Execution.Git;
using Orkka.Modules.SourceControl.Contracts;

/// <summary>
/// Every way a task's standalone clone is created, reused, rebuilt, continued, refreshed,
/// diffed and removed — against a real forge (a bare repository on disk). The lifecycle test
/// walks the happy path once; these pin each branch of the behaviour on its own.
/// </summary>
public sealed class GitWorkspaceCloneScenarioTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orkka-clone-scn-{Ulid.NewUlid()}");
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();
    private readonly GitProcess _git = new(NullLogger<GitProcess>.Instance);
    private readonly GitWorkspaceManager _manager;
    private string Forge => Path.Combine(_root, "forge.git");
    private string Author => Path.Combine(_root, "author");

    public GitWorkspaceCloneScenarioTests()
    {
        _manager = new GitWorkspaceManager(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = Path.Combine(_root, "cache"),
            }).Build(),
            NullLogger<GitWorkspaceManager>.Instance, _git, NullGitOperationLedger.Instance);
    }

    [Fact]
    public async Task AHealthyClone_IsReused_WithItsUncommittedWorkIntact()
    {
        var taskId = await SeedAndCloneAsync("orkka/T-1");
        var path = _manager.GetWorktreePath(_orgId, _repoId, taskId);
        await File.WriteAllTextAsync(Path.Combine(path, "wip.txt"), "in progress\n");

        var again = await _manager.CreateWorktreeAsync(_orgId, _repoId, taskId, "orkka/T-1", "main");

        again.IsSuccess.Should().BeTrue(again.Error);
        again.Value.Should().Be(path);
        File.Exists(Path.Combine(path, "wip.txt")).Should().BeTrue("re-entering a phase must not discard prior work");
    }

    /// <summary>
    /// The clone is FULL. A blobless clone fetches file contents lazily inside whatever command
    /// first needs them, with that command's credential — none, for the agent's own git, and
    /// none for a merge run without one. Seen live as "could not fetch … from promisor remote"
    /// in the middle of a phase-entry base merge.
    /// </summary>
    [Fact]
    public async Task ANewClone_IsFull_NeverAPromisorClone()
    {
        var taskId = await SeedAndCloneAsync("orkka/T-12");
        var path = _manager.GetWorktreePath(_orgId, _repoId, taskId);

        (await Out(path, "config --get remote.origin.promisor")).Should().BeEmpty();
        (await Out(path, "rev-list --objects --all --missing=print")).Should().NotContain("?",
            "every object reachable from every ref is present locally");
    }

    /// <summary>
    /// A clone an earlier version made partial is backfilled from the LOCAL mirror when it is
    /// reused — no network, no credential — and stops being a promisor clone. Proven by
    /// taking the forge away before reusing it: anything still missing would have to be
    /// fetched, and nothing is.
    /// </summary>
    [Fact]
    public async Task ALegacyPartialClone_IsBackfilledFromTheMirror_OnReuse()
    {
        await SeedAsync();
        await Run(Forge, "config uploadpack.allowfilter true");
        var taskId = Ulid.NewUlid();
        var path = _manager.GetWorktreePath(_orgId, _repoId, taskId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await Run(Path.GetDirectoryName(path)!, $"clone --filter=blob:none --no-checkout \"{Forge}\" \"{path}\"");
        await Run(path, "checkout -b orkka/T-13 origin/main");
        (await Out(path, "config --get remote.origin.promisor")).Should().Be("true", "the setup reproduces the old shape");

        // The forge becomes unreachable from this clone: a lazy fetch now has nowhere to go.
        await Run(path, $"remote set-url origin \"{Path.Combine(_root, "gone.git")}\"");

        var reused = await _manager.CreateWorktreeAsync(_orgId, _repoId, taskId, "orkka/T-13", "main");
        reused.IsSuccess.Should().BeTrue(reused.Error);

        (await Out(path, "config --get remote.origin.promisor")).Should().BeEmpty();
        (await Out(path, "rev-list --objects --all --missing=print")).Should().NotContain("?");
        // The command that failed in production, with no forge reachable at all.
        var show = await _git.RunAsync(new GitCommand(path, "show", "origin/main:README.md"), CancellationToken.None);
        show.Succeeded.Should().BeTrue(show.Error);
    }

    [Fact]
    public async Task ABrokenClone_IsRebuiltFromTheForge()
    {
        var taskId = await SeedAndCloneAsync("orkka/T-2");
        var path = _manager.GetWorktreePath(_orgId, _repoId, taskId);
        // Corrupt it the way a killed process does: the ref store is gone, git refuses to run.
        GitWorkspaceManager.DeleteDirectoryTree(Path.Combine(path, ".git"));
        File.WriteAllText(Path.Combine(path, ".git"), "gitdir: /nowhere\n");

        var rebuilt = await _manager.CreateWorktreeAsync(_orgId, _repoId, taskId, "orkka/T-2", "main");

        rebuilt.IsSuccess.Should().BeTrue(rebuilt.Error);
        Directory.Exists(Path.Combine(path, ".git")).Should().BeTrue();
        (await Out(path, "rev-parse --abbrev-ref HEAD")).Should().Be("orkka/T-2");
    }

    [Fact]
    public async Task ABranchAlreadyOnTheForge_IsContinued_NotRestartedFromTheBase()
    {
        var taskId = await SeedAndCloneAsync("orkka/T-3");
        var path = _manager.GetWorktreePath(_orgId, _repoId, taskId);
        await File.WriteAllTextAsync(Path.Combine(path, "first.txt"), "first run\n");
        await Run(path, "add -A");
        await Run(path, "commit -m \"first run\"");
        (await _manager.EnsurePushedAsync(_orgId, _repoId, taskId, "orkka/T-3")).IsSuccess.Should().BeTrue();
        await _manager.CleanupWorktreeAsync(_orgId, _repoId, taskId);
        Directory.Exists(path).Should().BeFalse();

        // A new machine, a GC'd folder, a re-claimed task: the branch on the forge is the truth.
        var resumed = await _manager.CreateWorktreeAsync(_orgId, _repoId, taskId, "orkka/T-3", "main");

        resumed.IsSuccess.Should().BeTrue(resumed.Error);
        File.Exists(Path.Combine(resumed.Value!, "first.txt")).Should().BeTrue();
        (await Out(resumed.Value!, "rev-parse --abbrev-ref --symbolic-full-name @{upstream}")).Should().Be("origin/orkka/T-3");
    }

    [Fact]
    public async Task ALegacyLinkedWorktree_IsReused_AndCleanedUp()
    {
        await SeedAsync();
        var taskId = Ulid.NewUlid();
        var path = _manager.GetWorktreePath(_orgId, _repoId, taskId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // What the previous version of the manager produced: a linked worktree of the mirror.
        await Run(_manager.GetBareRepoPath(_orgId, _repoId), $"worktree add \"{path}\" -b orkka/T-4 origin/main");
        File.Exists(Path.Combine(path, ".git")).Should().BeTrue("a linked worktree has a .git FILE");

        var reused = await _manager.CreateWorktreeAsync(_orgId, _repoId, taskId, "orkka/T-4", "main");
        reused.IsSuccess.Should().BeTrue(reused.Error);
        reused.Value.Should().Be(path);

        await _manager.CleanupWorktreeAsync(_orgId, _repoId, taskId);
        Directory.Exists(path).Should().BeFalse();
        (await Out(_manager.GetBareRepoPath(_orgId, _repoId), "worktree list")).Should().NotContain("T-4",
            "the mirror's registration is pruned with the folder");
    }

    [Fact]
    public async Task SyncBase_MidRun_RefusesADirtyTree_WithTheCode()
    {
        var taskId = await SeedAndCloneAsync("orkka/T-5");
        var path = _manager.GetWorktreePath(_orgId, _repoId, taskId);
        await File.WriteAllTextAsync(Path.Combine(path, "agent.txt"), "the agent is still typing\n");

        var sync = await _manager.SyncBaseBranchAsync(_orgId, _repoId, taskId, "main",
            leaveConflictsInProgress: true, commitLeftovers: false);

        sync.IsFailure.Should().BeTrue();
        sync.Error.Should().StartWith(nameof(GitErrorCode.DirtyWorkingTree));
        (await Out(path, "status --porcelain")).Should().Contain("agent.txt", "the agent's tree is untouched");
    }

    [Fact]
    public async Task SyncBase_AtAPhaseBoundary_NeverCommitsUnresolvedConflicts()
    {
        var taskId = await SeedAndCloneAsync("orkka/T-6");
        var path = _manager.GetWorktreePath(_orgId, _repoId, taskId);
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "task side\n");
        await Run(path, "commit -am \"task edits readme\"");
        await AuthorCommitAsync("README.md", "main side\n", "main edits readme");

        var first = await _manager.SyncBaseBranchAsync(_orgId, _repoId, taskId, "main",
            leaveConflictsInProgress: true, commitLeftovers: true);
        first.Value!.Status.Should().Be(BaseSyncStatus.Conflicted);

        // A crashed run left the conflict unresolved. The next boundary must not "commit
        // leftovers" over it — markers would land on the branch as code.
        var second = await _manager.SyncBaseBranchAsync(_orgId, _repoId, taskId, "main",
            leaveConflictsInProgress: true, commitLeftovers: true);

        second.IsSuccess.Should().BeTrue(second.Error);
        second.Value!.Status.Should().Be(BaseSyncStatus.Conflicted, "the stale merge is aborted and re-staged, not committed");
        (await Out(path, "log -1 --format=%s")).Should().Be("task edits readme");
    }

    [Fact]
    public async Task RemoteBranchExists_AnswersFromTheForge()
    {
        var taskId = await SeedAndCloneAsync("orkka/T-7");
        (await _manager.RemoteBranchExistsAsync(_orgId, _repoId, "orkka/T-7")).Should().BeFalse("nothing pushed yet");

        var path = _manager.GetWorktreePath(_orgId, _repoId, taskId);
        await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "a\n");
        await Run(path, "add -A");
        await Run(path, "commit -m a");
        (await _manager.EnsurePushedAsync(_orgId, _repoId, taskId, "orkka/T-7")).IsSuccess.Should().BeTrue();

        (await _manager.RemoteBranchExistsAsync(_orgId, _repoId, "orkka/T-7")).Should().BeTrue();
    }

    [Fact]
    public async Task GetDiff_FallsBackToTheMirror_WhenTheCloneIsGone()
    {
        var taskId = await SeedAndCloneAsync("orkka/T-8");
        var path = _manager.GetWorktreePath(_orgId, _repoId, taskId);
        await File.WriteAllTextAsync(Path.Combine(path, "new.txt"), "hello\n");
        await Run(path, "add -A");
        await Run(path, "commit -m \"add new\"");
        (await _manager.EnsurePushedAsync(_orgId, _repoId, taskId, "orkka/T-8")).IsSuccess.Should().BeTrue();
        await _manager.CleanupWorktreeAsync(_orgId, _repoId, taskId);

        var diff = await _manager.GetDiffAsync(_orgId, _repoId, taskId, "main", headBranch: "orkka/T-8");

        diff.IsSuccess.Should().BeTrue(diff.Error);
        diff.Value.Should().Contain("+hello");
    }

    [Fact]
    public async Task ApplyMergeStrategy_TakesTheChosenSide_AndCommitsAsTheBot()
    {
        var taskId = await SeedAndCloneAsync("orkka/T-10");
        var path = _manager.GetWorktreePath(_orgId, _repoId, taskId);
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "task side\n");
        await Run(path, "commit -am \"task edits readme\"");
        await AuthorCommitAsync("README.md", "main side\n", "main edits readme");

        var applied = await _manager.ApplyMergeStrategyAsync(_orgId, _repoId, taskId, "main", "current", null);

        applied.IsSuccess.Should().BeTrue(applied.Error);
        applied.Value!.Completed.Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(path, "README.md"))).TrimEnd().Should().Be("main side");
        (await Out(path, "log -1 --format=%an")).Should().Be(TestGitIdentity.Name);
    }

    [Fact]
    public async Task StaleClones_AreGarbageCollected_AndTheRepoWorkspaceCanBeRemovedWhole()
    {
        var taskId = await SeedAndCloneAsync("orkka/T-11");
        var path = _manager.GetWorktreePath(_orgId, _repoId, taskId);
        Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));

        var removed = await _manager.CleanupStaleWorktreesAsync(TimeSpan.FromDays(3));
        removed.Should().Be(1);
        Directory.Exists(path).Should().BeFalse();

        _manager.EnumerateRepoWorkspaces().Should().Contain((_orgId, _repoId));
        await _manager.RemoveRepoWorkspaceAsync(_orgId, _repoId);
        Directory.Exists(_manager.GetBareRepoPath(_orgId, _repoId)).Should().BeFalse();
        _manager.EnumerateRepoWorkspaces().Should().NotContain((_orgId, _repoId));
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private async Task<Ulid> SeedAndCloneAsync(string branch)
    {
        await SeedAsync();
        var taskId = Ulid.NewUlid();
        var created = await _manager.CreateWorktreeAsync(_orgId, _repoId, taskId, branch, "main");
        created.IsSuccess.Should().BeTrue(created.Error);
        return taskId;
    }

    private async Task SeedAsync()
    {
        if (Directory.Exists(Forge))
            return;
        Directory.CreateDirectory(_root);
        await Run(_root, "init --bare -b main forge.git");
        await Run(_root, $"clone \"{Forge}\" author");
        await Run(Author, "checkout -b main");
        await File.WriteAllTextAsync(Path.Combine(Author, "README.md"), "seed\n");
        await Run(Author, "add -A");
        await Run(Author, "-c user.name=Seed -c user.email=seed@example.test commit -m init");
        await Run(Author, "push -u origin main");
        var mirror = await _manager.EnsureBareCloneAsync(_orgId, _repoId, Forge);
        mirror.IsSuccess.Should().BeTrue(mirror.Error);
    }

    private async Task AuthorCommitAsync(string file, string content, string message)
    {
        await Run(Author, "checkout main");
        await File.WriteAllTextAsync(Path.Combine(Author, file), content);
        await Run(Author, "add -A");
        await Run(Author, $"-c user.name=Seed -c user.email=seed@example.test commit -m \"{message}\"");
        await Run(Author, "push origin main");
    }

    private async Task Run(string dir, string args)
    {
        var result = await _git.RunAsync(dir, args, CancellationToken.None);
        result.Succeeded.Should().BeTrue($"git {args}: {result.Error}");
    }

    private async Task<string> Out(string dir, string args)
        => (await _git.RunAsync(dir, args, CancellationToken.None)).Output;

    public void Dispose()
    {
        try { GitWorkspaceManager.DeleteDirectoryTree(_root); } catch { /* temp */ }
    }
}
