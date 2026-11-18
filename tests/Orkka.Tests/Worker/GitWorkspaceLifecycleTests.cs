namespace Orkka.Tests.Worker;

using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.Git;
using Orkka.Modules.Execution.Git;
using Orkka.Modules.SourceControl.Contracts;

/// <summary>
/// One task's whole git life against a real forge (a bare repo on disk), through the
/// workspace manager as the pipeline drives it: mirror → worktree → agent commit → platform
/// publish → base moved → sync (clean) → base moved into a conflict → sync (conflicted, left
/// for the agent) → resolved → verified → published → cleaned up. Pinned throughout: every
/// commit is the platform's, nothing about a credential reaches disk, and the ledger saw every
/// operation with the outcome it had.
///
/// This is the regression net the engine phases ship under. A step that starts failing here
/// fails before it reaches a user's project.
/// </summary>
[Collection(Orkka.Tests.GitEngineStaticsCollection.Name)]
public sealed class GitWorkspaceLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orkka-lifecycle-{Ulid.NewUlid()}");
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();
    private readonly Ulid _taskId = Ulid.NewUlid();
    private readonly RecordingLedger _ledger = new();
    private readonly GitProcess _git = new(NullLogger<GitProcess>.Instance);
    private string Forge => Path.Combine(_root, "forge.git");
    private string Author => Path.Combine(_root, "author");

    [Fact]
    public async Task ATask_FromMirrorToCleanup_WithTheBotAuthorAndNoCredentialOnDisk()
    {
        await SeedForgeAsync();
        var manager = new GitWorkspaceManager(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = Path.Combine(_root, "cache"),
                ["Git:CommitAuthorName"] = "admin-orkka",
                ["Git:CommitAuthorEmail"] = "304923340+admin-orkka@users.noreply.github.com",
            }).Build(),
            NullLogger<GitWorkspaceManager>.Instance, _git, _ledger);

        // 1. Mirror. A file forge takes no credential; a token given anyway must not land anywhere.
        var mirror = await manager.EnsureBareCloneAsync(_orgId, _repoId, Forge);
        mirror.IsSuccess.Should().BeTrue(mirror.Error);
        (await File.ReadAllTextAsync(Path.Combine(mirror.Value!, "config"))).Should().NotContain("ghs_never_on_disk");
        (await Out(mirror.Value!, "config user.name")).Should().Be("admin-orkka");

        // 2. Worktree from main.
        var worktree = await manager.CreateWorktreeAsync(_orgId, _repoId, _taskId, "orkka/LIFE-1", "main", "LIFE-1");
        worktree.IsSuccess.Should().BeTrue(worktree.Error);
        worktree.Value.Should().EndWith("LIFE-1");
        Directory.Exists(Path.Combine(worktree.Value!, ".git")).Should().BeTrue(
            "the workspace is a standalone clone with its own ref store, not a linked worktree of the mirror");
        (await Out(worktree.Value!, "remote get-url origin")).Should().Be(Forge);
        (await Out(worktree.Value!, "config user.name")).Should().Be("admin-orkka");
        (await Out(worktree.Value!, "rev-parse --abbrev-ref HEAD")).Should().Be("orkka/LIFE-1");

        // 3. The agent works and commits with plain git — no identity flags, like a coding agent.
        await File.WriteAllTextAsync(Path.Combine(worktree.Value!, "feature.txt"), "feature\n");
        await Run(worktree.Value!, "add -A");
        await Run(worktree.Value!, "commit -m \"feat: the feature\"");
        (await Out(worktree.Value!, "log -1 --format=%an")).Should().Be("admin-orkka",
            "the mirror's config makes the agent's own commit the bot's");

        // 4. The platform publishes. Leftovers the agent forgot are committed too.
        await File.WriteAllTextAsync(Path.Combine(worktree.Value!, "forgot.txt"), "forgot\n");
        var publish = await manager.CommitAndPushAsync(worktree.Value!, "Commit changes left uncommitted", "orkka/LIFE-1");
        publish.IsSuccess.Should().BeTrue(publish.Error);
        (await Out(Forge, "log -1 --format=%s orkka/LIFE-1")).Should().Be("Commit changes left uncommitted");
        (await manager.RemoteBranchExistsAsync(_orgId, _repoId, "orkka/LIFE-1")).Should().BeTrue();

        // 5. A sibling lands on main; the sync at the next phase boundary merges it in cleanly.
        await AuthorCommitAsync("sibling.txt", "sibling\n", "feat: sibling");
        var sync = await manager.SyncBaseBranchAsync(_orgId, _repoId, _taskId, "main",
            leaveConflictsInProgress: false, commitLeftovers: true);
        sync.IsSuccess.Should().BeTrue(sync.Error);
        sync.Value!.Status.Should().Be(BaseSyncStatus.Merged);
        sync.Value.CommitsBehind.Should().Be(1);
        (await Out(worktree.Value!, "log -1 --format=%an|%s")).Should().StartWith("admin-orkka|Merge");

        // 6. A second sibling edits the task's own file: the entry sync leaves the conflict
        //    staged for the agent, and a run that ended dirty is committed first, not refused.
        await File.WriteAllTextAsync(Path.Combine(worktree.Value!, "feature.txt"), "feature v2\n");
        await Run(worktree.Value!, "commit -am \"feat: v2\"");
        await File.WriteAllTextAsync(Path.Combine(worktree.Value!, "wip.txt"), "left behind by a crashed run\n");
        await AuthorCommitAsync("feature.txt", "feature by sibling\n", "feat: sibling edits feature");

        var conflicted = await manager.SyncBaseBranchAsync(_orgId, _repoId, _taskId, "main",
            leaveConflictsInProgress: true, commitLeftovers: true);
        conflicted.IsSuccess.Should().BeTrue(conflicted.Error);
        conflicted.Value!.Status.Should().Be(BaseSyncStatus.Conflicted);
        conflicted.Value.ConflictedFiles.Should().Equal("feature.txt");
        (await Out(worktree.Value!, "log --format=%s -3")).Should().Contain("Commit changes left uncommitted by the previous run");

        // 7. Not yet resolved: verification says so, and would never let it be pushed.
        var unresolved = await manager.VerifyMergeAsync(_orgId, _repoId, _taskId, "main");
        unresolved.IsSuccess.Should().BeTrue(unresolved.Error);
        unresolved.Value!.Completed.Should().BeFalse();
        unresolved.Value.UnmergedPaths.Should().Equal("feature.txt");

        // 8. The agent resolves and commits; verification passes; the platform publishes.
        await File.WriteAllTextAsync(Path.Combine(worktree.Value!, "feature.txt"), "feature v2 + sibling\n");
        await Run(worktree.Value!, "add feature.txt");
        await Run(worktree.Value!, "commit --no-edit");
        var resolved = await manager.VerifyMergeAsync(_orgId, _repoId, _taskId, "main");
        resolved.Value!.Completed.Should().BeTrue();
        var pushed = await manager.EnsurePushedAsync(_orgId, _repoId, _taskId, "orkka/LIFE-1");
        pushed.IsSuccess.Should().BeTrue(pushed.Error);
        (await Out(Forge, "show orkka/LIFE-1:feature.txt")).Should().Be("feature v2 + sibling");

        // 9. The forge-side mergeability check agrees the branch now merges cleanly.
        var check = await manager.GetMergeConflictsAsync(_orgId, _repoId, "orkka/LIFE-1", "main");
        check.IsSuccess.Should().BeTrue(check.Error);
        check.Value!.HasConflicts.Should().BeFalse();

        // 10. Cleanup frees the folder and the mirror survives for the next task.
        await manager.CleanupWorktreeAsync(_orgId, _repoId, _taskId);
        Directory.Exists(worktree.Value!).Should().BeFalse();
        Directory.Exists(mirror.Value!).Should().BeTrue();

        // Every commit on the forge is the bot's.
        (await Out(Forge, "log --format=%an|%ae orkka/LIFE-1")).Split('\n')
            .Where(l => !l.StartsWith("Seed|"))
            .Should().OnlyContain(l => l == "admin-orkka|304923340+admin-orkka@users.noreply.github.com");

        // The ledger saw the whole life, each operation with its outcome.
        _ledger.Records.Select(r => r.Kind).Should().Contain([
            GitOperationKind.EnsureMirror, GitOperationKind.CreateWorkspace, GitOperationKind.Publish,
            GitOperationKind.SyncBase, GitOperationKind.VerifyMerge, GitOperationKind.Push,
            GitOperationKind.MergeCheck, GitOperationKind.Cleanup]);
        _ledger.Records.Should().OnlyContain(r => r.Succeeded, "nothing in a clean life fails");
    }

    /// <summary>A hybrid project's local folder lives on the user's PC. Asked to mirror it on
    /// Orkka's compute, the manager names the precondition instead of running git — which, on
    /// Linux, reads <c>C:\Code\app</c> as the ssh host "C" and fails with "cannot run ssh".</summary>
    [Fact]
    public async Task ALocalFolderThatIsNotOnThisMachine_IsRefusedAsAPrecondition()
    {
        var manager = new GitWorkspaceManager(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = Path.Combine(_root, "cache"),
            }).Build(),
            NullLogger<GitWorkspaceManager>.Instance, _git, _ledger);
        var elsewhere = Path.Combine(_root, "not-here", "storefront-api");

        var result = await manager.EnsureBareCloneAsync(_orgId, _repoId, elsewhere);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().StartWith(nameof(GitErrorCode.Precondition));
        result.Error.Should().Contain("Orkka Local runner");
        _ledger.Records.Single().Code.Should().Be(GitErrorCode.Precondition);
        _ledger.Records.Single().CommandCount.Should().Be(0, "git was never asked to guess");
        Directory.Exists(manager.GetBareRepoPath(_orgId, _repoId)).Should().BeFalse();
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private async Task SeedForgeAsync()
    {
        Directory.CreateDirectory(_root);
        await Run(_root, "init --bare -b main forge.git");
        await Run(_root, $"clone \"{Forge}\" author");
        await Run(Author, "checkout -b main");
        await File.WriteAllTextAsync(Path.Combine(Author, "README.md"), "seed\n");
        await Run(Author, "add -A");
        await Run(Author, "-c user.name=Seed -c user.email=seed@example.test commit -m \"init\"");
        await Run(Author, "push -u origin main");
    }

    private async Task AuthorCommitAsync(string file, string content, string message)
    {
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
        try
        {
            if (!Directory.Exists(_root)) return;
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* temp */ }
    }

    private sealed class RecordingLedger : IGitOperationLedger
    {
        public List<GitOperationRecord> Records { get; } = [];
        public string Topology => "test";
        public void Record(GitOperationRecord record) => Records.Add(record);
    }
}
