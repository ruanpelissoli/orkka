namespace Orkka.Tests.Worker;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.Git;
using Orkka.Modules.Execution.Git;

/// <summary>
/// A Story's shared branch on real git: the second member's clone continues from what the first
/// pushed, records that head as its review base, and its diff since that base is its own change
/// only — while the full branch diff still carries the whole story.
/// </summary>
public sealed class GitWorkspaceStoryTests : IDisposable
{
    private const string Branch = "feature/SHOP-10-checkout";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orkka-story-{Ulid.NewUlid()}");
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();
    private readonly GitProcess _git = new(NullLogger<GitProcess>.Instance);
    private readonly GitWorkspaceManager _manager;
    private string Forge => Path.Combine(_root, "forge.git");
    private string Author => Path.Combine(_root, "author");

    public GitWorkspaceStoryTests()
    {
        _manager = new GitWorkspaceManager(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = Path.Combine(_root, "cache"),
            }).Build(),
            NullLogger<GitWorkspaceManager>.Instance, _git, NullGitOperationLedger.Instance);
    }

    [Fact]
    public async Task TheNextMember_StartsFromThePreviousOnesPushedHead_AndItsDiffIsItsOwnChange()
    {
        await SeedAsync();
        var mainHead = (await Out(Author, "rev-parse HEAD")).Trim();

        var first = Ulid.NewUlid();
        (await _manager.CreateWorktreeAsync(_orgId, _repoId, first, Branch, "main")).IsSuccess.Should().BeTrue();
        var firstBase = await _manager.ResolveReviewBaseAsync(_orgId, _repoId, first, Branch, "main");
        firstBase.Should().Be(mainHead, "the first member starts where the branch was cut from the default branch");
        await CommitAsync(first, "cart.txt", "cart\n");
        (await _manager.EnsurePushedAsync(_orgId, _repoId, first, Branch)).IsSuccess.Should().BeTrue();
        var firstHead = (await Out(_manager.GetWorktreePath(_orgId, _repoId, first), "rev-parse HEAD")).Trim();

        var second = Ulid.NewUlid();
        (await _manager.CreateWorktreeAsync(_orgId, _repoId, second, Branch, "main")).IsSuccess.Should().BeTrue();
        File.Exists(Path.Combine(_manager.GetWorktreePath(_orgId, _repoId, second), "cart.txt"))
            .Should().BeTrue("the second member's clone continues the shared branch");
        var secondBase = await _manager.ResolveReviewBaseAsync(_orgId, _repoId, second, Branch, "main");
        secondBase.Should().Be(firstHead);
        await CommitAsync(second, "pay.txt", "pay\n");

        var own = await _manager.GetDiffSinceAsync(_orgId, _repoId, second, "main", secondBase!);
        own.IsSuccess.Should().BeTrue(own.Error);
        own.Value.Should().Contain("pay.txt").And.NotContain("cart.txt");

        var whole = await _manager.GetDiffAsync(_orgId, _repoId, second, "main");
        whole.Value.Should().Contain("pay.txt").And.Contain("cart.txt");
    }

    /// <summary>
    /// The base sync merges the default branch into the shared branch while a member is in
    /// flight. Before any commit of its own (the Developing-entry sync after planning) and after
    /// some (a re-entry after review), its review diff must still be its own change only — not
    /// the default branch's new commits, and not the earlier members' work.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMidMemberDefaultBranchSync_StaysOutOfTheMembersReviewDiff(bool commitsBeforeSync)
    {
        await SeedAsync();

        var first = Ulid.NewUlid();
        (await _manager.CreateWorktreeAsync(_orgId, _repoId, first, Branch, "main")).IsSuccess.Should().BeTrue();
        await CommitAsync(first, "cart.txt", "cart\n");
        (await _manager.EnsurePushedAsync(_orgId, _repoId, first, Branch)).IsSuccess.Should().BeTrue();

        var second = Ulid.NewUlid();
        (await _manager.CreateWorktreeAsync(_orgId, _repoId, second, Branch, "main")).IsSuccess.Should().BeTrue();
        var secondBase = await _manager.ResolveReviewBaseAsync(_orgId, _repoId, second, Branch, "main");
        if (commitsBeforeSync)
            await CommitAsync(second, "pay.txt", "pay\n");

        // Someone else lands on main meanwhile; the member's base sync merges it in.
        await File.WriteAllTextAsync(Path.Combine(Author, "hotfix.txt"), "hotfix\n");
        await Run(Author, "add -A");
        await Run(Author, "-c user.name=Other -c user.email=other@example.test commit -m hotfix");
        await Run(Author, "push origin main");
        var sync = await _manager.SyncBaseBranchAsync(_orgId, _repoId, second, "main", leaveConflictsInProgress: false);
        sync.IsSuccess.Should().BeTrue(sync.Error);
        var worktree = _manager.GetWorktreePath(_orgId, _repoId, second);
        File.Exists(Path.Combine(worktree, "hotfix.txt")).Should().BeTrue("the sync merged main in");

        if (!commitsBeforeSync)
            await CommitAsync(second, "pay.txt", "pay\n");

        // The naive range shows main's commit — the bug this guards against.
        (await Out(worktree, $"diff --name-only {secondBase} HEAD")).Should().Contain("hotfix.txt");

        var own = await _manager.GetDiffSinceAsync(_orgId, _repoId, second, "main", secondBase!);
        own.IsSuccess.Should().BeTrue(own.Error);
        own.Value.Should().Contain("pay.txt").And.NotContain("hotfix.txt").And.NotContain("cart.txt");

        // The bare-mirror path (worktree gone, branch pushed) answers the same.
        (await _manager.EnsurePushedAsync(_orgId, _repoId, second, Branch)).IsSuccess.Should().BeTrue();
        await _manager.CleanupWorktreeAsync(_orgId, _repoId, second);
        var fromMirror = await _manager.GetDiffSinceAsync(_orgId, _repoId, second, "main", secondBase!, headBranch: Branch);
        fromMirror.IsSuccess.Should().BeTrue(fromMirror.Error);
        fromMirror.Value.Should().Contain("pay.txt").And.NotContain("hotfix.txt").And.NotContain("cart.txt");
    }

    /// <summary>Why the base is a computed tree and not a moved pointer: with commits before the
    /// sync, neither parent of the sync merge is a correct base on real git.</summary>
    [Fact]
    public async Task WithCommitsBeforeTheSync_NeitherParentOfTheMergeIsACorrectBase()
    {
        await SeedAsync();
        var first = Ulid.NewUlid();
        (await _manager.CreateWorktreeAsync(_orgId, _repoId, first, Branch, "main")).IsSuccess.Should().BeTrue();
        await CommitAsync(first, "cart.txt", "cart\n");
        (await _manager.EnsurePushedAsync(_orgId, _repoId, first, Branch)).IsSuccess.Should().BeTrue();
        var second = Ulid.NewUlid();
        (await _manager.CreateWorktreeAsync(_orgId, _repoId, second, Branch, "main")).IsSuccess.Should().BeTrue();
        await CommitAsync(second, "pay.txt", "pay\n");
        await File.WriteAllTextAsync(Path.Combine(Author, "hotfix.txt"), "hotfix\n");
        await Run(Author, "add -A");
        await Run(Author, "-c user.name=Other -c user.email=other@example.test commit -m hotfix");
        await Run(Author, "push origin main");
        (await _manager.SyncBaseBranchAsync(_orgId, _repoId, second, "main", leaveConflictsInProgress: false))
            .IsSuccess.Should().BeTrue();
        var worktree = _manager.GetWorktreePath(_orgId, _repoId, second);

        // First parent: still before main's commit, so the member's own file is hidden behind it.
        (await Out(worktree, "diff --name-only HEAD^1 HEAD")).Should().Contain("hotfix.txt").And.NotContain("pay.txt");
        // Second parent (the synced main): the earlier member's work shows as if it were this one's.
        (await Out(worktree, "diff --name-only HEAD^2 HEAD")).Should().Contain("cart.txt");
    }

    private async Task SeedAsync()
    {
        Directory.CreateDirectory(_root);
        await Run(_root, "init --bare -b main forge.git");
        await Run(_root, $"clone \"{Forge}\" author");
        await Run(Author, "checkout -b main");
        await File.WriteAllTextAsync(Path.Combine(Author, "README.md"), "seed\n");
        await Run(Author, "add -A");
        await Run(Author, "-c user.name=Seed -c user.email=seed@example.test commit -m init");
        await Run(Author, "push -u origin main");
        (await _manager.EnsureBareCloneAsync(_orgId, _repoId, Forge)).IsSuccess.Should().BeTrue();
    }

    private async Task CommitAsync(Ulid taskId, string file, string content)
    {
        var path = _manager.GetWorktreePath(_orgId, _repoId, taskId);
        await File.WriteAllTextAsync(Path.Combine(path, file), content);
        await Run(path, "add -A");
        await Run(path, $"-c user.name=Dev -c user.email=dev@example.test commit -m \"add {file}\"");
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
