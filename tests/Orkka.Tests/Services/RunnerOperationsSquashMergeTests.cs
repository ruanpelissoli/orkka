namespace Orkka.Tests.Services;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Git;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Execution.Agents;
using Orkka.Modules.SourceControl.Contracts;

/// <summary>
/// Landing a task in the user's OWN repository (Orkka Local, hybrid local folders) without
/// ever merging inside their working copy. The merge happens in a scratch clone Orkka owns
/// and reaches the user's repository as a push: a ref update when the base is not checked
/// out, a fast-forward of the working tree when it is checked out and clean, and a refusal
/// with the reason when the tree is dirty — their files are never touched, no branch is
/// switched under them. Every commit Orkka makes is the platform's.
/// </summary>
[Collection(Orkka.Tests.GitEngineStaticsCollection.Name)]
public sealed class RunnerOperationsSquashMergeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orkka-squash-{Ulid.NewUlid()}");
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();
    private readonly GitProcess _git = new(NullLogger<GitProcess>.Instance);
    private readonly RunnerOperations _ops;
    private string UserRepo => Path.Combine(_root, "user-repo");
    private string Bare => Path.Combine(_root, "cache", _orgId.ToString(), _repoId.ToString(), "bare");

    public RunnerOperationsSquashMergeTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = Path.Combine(_root, "cache"),
            })
            .Build();

        var git = new Mock<IGitWorkspaceManager>();
        git.Setup(g => g.GetBareRepoPath(_orgId, _repoId)).Returns(Bare);

        _ops = new RunnerOperations(
            git.Object,
            Mock.Of<IAiAgentInvoker>(),
            new ProviderDetection(Mock.Of<ILogger<ProviderDetection>>()),
            config,
            new RunnerHostLink(),
            Mock.Of<ILogger<RunnerOperations>>(),
            _git);
    }

    [Fact]
    public async Task CleanCheckedOutBase_LandsTheMerge_AndFastForwardsTheWorkingTree()
    {
        await SeedAsync();

        var result = await _ops.SquashMergeIntoUserRepoAsync(Request());

        result.Success.Should().BeTrue(result.Error);
        (await Out(UserRepo, "rev-parse --abbrev-ref HEAD")).Should().Be("main", "no branch is switched under the user");
        File.Exists(Path.Combine(UserRepo, "feature.txt")).Should().BeTrue("the working tree fast-forwarded");
        (await Out(UserRepo, "log -1 --format=%s|%an")).Should().Be($"Land the feature|{TestGitIdentity.Name}");
        (await Out(UserRepo, "status --porcelain")).Should().BeEmpty();
        Directory.Exists(Path.Combine(_root, "cache", "merge-scratch")).Should().BeTrue();
        Directory.EnumerateDirectories(Path.Combine(_root, "cache", "merge-scratch")).Should().BeEmpty(
            "the scratch clone is removed after the merge");
    }

    [Fact]
    public async Task DirtyWorkingTree_RefusesWithTheReason_AndTouchesNothing()
    {
        await SeedAsync();
        await File.WriteAllTextAsync(Path.Combine(UserRepo, "README.md"), "edited but not committed\n");

        var result = await _ops.SquashMergeIntoUserRepoAsync(Request());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("uncommitted changes");
        (await File.ReadAllTextAsync(Path.Combine(UserRepo, "README.md"))).Should().Be("edited but not committed\n");
        File.Exists(Path.Combine(UserRepo, "feature.txt")).Should().BeFalse();
        (await Out(UserRepo, "log -1 --format=%s")).Should().Be("init", "main did not move");
    }

    [Fact]
    public async Task BaseNotCheckedOut_UpdatesTheRef_WithoutTouchingTheWorkingTree()
    {
        await SeedAsync();
        await Run(UserRepo, "checkout -b scratch-work");
        await File.WriteAllTextAsync(Path.Combine(UserRepo, "wip.txt"), "the user's own work in progress\n");

        var result = await _ops.SquashMergeIntoUserRepoAsync(Request());

        result.Success.Should().BeTrue(result.Error);
        (await Out(UserRepo, "rev-parse --abbrev-ref HEAD")).Should().Be("scratch-work");
        File.Exists(Path.Combine(UserRepo, "wip.txt")).Should().BeTrue("the user's tree is untouched");
        (await Out(UserRepo, "log -1 --format=%s main")).Should().Be("Land the feature");
        (await Out(UserRepo, "show main:feature.txt")).Should().Be("feature");
    }

    [Fact]
    public async Task ConflictingBranch_Refuses_AndLeavesTheRepositoryAsItWas()
    {
        await SeedAsync();
        // main moves on the same file the task changed, in a way that conflicts.
        await File.WriteAllTextAsync(Path.Combine(UserRepo, "shared.txt"), "main's version\n");
        await Run(UserRepo, "commit -am \"main edits shared\"");

        var result = await _ops.SquashMergeIntoUserRepoAsync(Request());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Merge conflict");
        (await Out(UserRepo, "status --porcelain")).Should().BeEmpty("a failed merge leaves no half-applied state");
        (await Out(UserRepo, "log -1 --format=%s")).Should().Be("main edits shared");
    }

    /// <summary>Two tasks landing in the same folder at the same moment: each merge is cut in
    /// its own scratch clone, and the second push would be refused NonFastForward once the
    /// first had moved main (prod, 2026-09-16). Merges into one folder are serialized on the
    /// runner, so both land.</summary>
    [Fact]
    public async Task TwoMergesIntoTheSameRepository_AtOnce_BothLand()
    {
        await SeedAsync();
        await Run(UserRepo, "checkout -b orkka/T-2");
        await File.WriteAllTextAsync(Path.Combine(UserRepo, "second.txt"), "second");
        await Run(UserRepo, "add -A");
        await Run(UserRepo, "-c user.name=Agent -c user.email=agent@example.test commit -m second");
        await Run(UserRepo, "checkout main");

        var first = _ops.SquashMergeIntoUserRepoAsync(Request());
        var second = _ops.SquashMergeIntoUserRepoAsync(new SquashMergeRequest(
            RunnerTokens.UserRepo(_orgId.ToString(), _repoId.ToString()), "orkka/T-2", "main", "Land the second"));
        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(r => r.Success, string.Join(" | ", results.Select(r => r.Error)));
        File.Exists(Path.Combine(UserRepo, "feature.txt")).Should().BeTrue();
        File.Exists(Path.Combine(UserRepo, "second.txt")).Should().BeTrue();
        (await Out(UserRepo, "rev-list --count main")).Trim().Should().Be("3");
        (await Out(UserRepo, "status --porcelain")).Should().BeEmpty();
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private SquashMergeRequest Request() => new(
        RunnerTokens.UserRepo(_orgId.ToString(), _repoId.ToString()),
        "orkka/T-1", "main", "Land the feature");

    private async Task SeedAsync()
    {
        Directory.CreateDirectory(UserRepo);
        await Run(UserRepo, "init -b main");
        await File.WriteAllTextAsync(Path.Combine(UserRepo, "README.md"), "seed\n");
        await File.WriteAllTextAsync(Path.Combine(UserRepo, "shared.txt"), "original\n");
        await Run(UserRepo, "add -A");
        await Run(UserRepo, "-c user.name=User -c user.email=user@example.test commit -m init");

        // The task's branch, as an agent's push would have left it in the user's repository.
        await Run(UserRepo, "checkout -b orkka/T-1");
        await File.WriteAllTextAsync(Path.Combine(UserRepo, "feature.txt"), "feature");
        await File.WriteAllTextAsync(Path.Combine(UserRepo, "shared.txt"), "task's version\n");
        await Run(UserRepo, "add -A");
        await Run(UserRepo, "-c user.name=Agent -c user.email=agent@example.test commit -m feature");
        await Run(UserRepo, "checkout main");

        // The runner's cache: a bare mirror whose origin IS the user's folder.
        Directory.CreateDirectory(Path.GetDirectoryName(Bare)!);
        await Run(Path.GetDirectoryName(Bare)!, $"clone --bare \"{UserRepo}\" bare");
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
        try { Orkka.Modules.Execution.Git.GitWorkspaceManager.DeleteDirectoryTree(_root); }
        catch { /* temp */ }
    }
}
