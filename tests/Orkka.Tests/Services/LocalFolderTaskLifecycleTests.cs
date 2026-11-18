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
using Orkka.Modules.Execution.Git;

/// <summary>
/// The whole git life of a task on a "bring your own folder" project, end to end on the
/// runner — the scenario that failed in prod on 2026-09-15 with "Branch 'orkka/STO-2' is not
/// in your repository": the agent commits and never pushes (no machine credential is ever
/// used), so the PLATFORM has to push the task branch into the user's folder before the merge
/// can squash it onto their base branch. Mirror → worktree → commit → push → squash-merge,
/// with the user's working copy only ever touched by the final fast-forward.
/// </summary>
[Collection(Orkka.Tests.GitEngineStaticsCollection.Name)]
public sealed class LocalFolderTaskLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orkka-folder-life-{Ulid.NewUlid()}");
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();
    private readonly Ulid _taskId = Ulid.NewUlid();
    private readonly GitProcess _git = new(NullLogger<GitProcess>.Instance);
    private readonly GitWorkspaceManager _manager;
    private readonly RunnerOperations _ops;
    private string UserRepo => Path.Combine(_root, "storefront-api");

    public LocalFolderTaskLifecycleTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = Path.Combine(_root, "cache"),
            })
            .Build();
        _manager = new GitWorkspaceManager(config, NullLogger<GitWorkspaceManager>.Instance, _git, NullGitOperationLedger.Instance);
        _ops = new RunnerOperations(
            _manager,
            Mock.Of<IAiAgentInvoker>(),
            new ProviderDetection(Mock.Of<ILogger<ProviderDetection>>()),
            config,
            new RunnerHostLink(),
            Mock.Of<ILogger<RunnerOperations>>(),
            _git);
    }

    [Fact]
    public async Task AFolderThatWasNotARepo_IsInitialised_MirroredAndWorkedOn_ThenTheTaskLandsOnMain()
    {
        // "Bring your own folder": AI-generated code, no .git yet. Orkka initialises it.
        Directory.CreateDirectory(UserRepo);
        await File.WriteAllTextAsync(Path.Combine(UserRepo, "README.md"), "generated elsewhere\n");
        var init = await _ops.InitRepoInPlaceAsync(new InitRepoInPlaceRequest(UserRepo));
        init.Success.Should().BeTrue(init.Error);
        var baseBranch = (await Out(UserRepo, "rev-parse --abbrev-ref HEAD")).Trim();

        var org = _orgId.ToString();
        var repo = _repoId.ToString();
        var task = _taskId.ToString();

        // 1. The runner's mirror of the folder — tokenless, bot identity stamped.
        var mirror = await _ops.EnsureBareCloneAsync(new EnsureBareCloneRequest(org, repo, UserRepo));
        mirror.Success.Should().BeTrue(mirror.Error);

        // 2. The task's standalone clone, whose origin IS the user's folder.
        var worktree = await _ops.CreateWorktreeAsync(new CreateWorktreeRequest(org, repo, task, "orkka/STO-2", baseBranch));
        worktree.Success.Should().BeTrue(worktree.Error);
        var worktreePath = _manager.GetWorktreePath(_orgId, _repoId, _taskId);
        (await Out(worktreePath, "remote get-url origin")).Trim().Should().Be(UserRepo);

        // 3. The agent commits — and never pushes.
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "feature.txt"), "task work\n");
        await Run(worktreePath, "add -A");
        await Run(worktreePath, "-c user.name=Agent -c user.email=agent@example.test commit -m \"STO-2: feature\"");
        (await _git.RunAsync(UserRepo, "rev-parse --verify --quiet refs/heads/orkka/STO-2", CancellationToken.None))
            .Succeeded.Should().BeFalse("nothing has pushed yet — this is the state the prod merge found");

        // 4. The platform pushes after the run (EnsureBranchPushedAfterRunAsync → EnsurePushed),
        //    with no token: the origin is a folder.
        var push = await _ops.EnsurePushedAsync(new EnsurePushedRequest(org, repo, task, "orkka/STO-2"));
        push.Success.Should().BeTrue(push.Error);
        (await _git.RunAsync(UserRepo, "rev-parse --verify --quiet refs/heads/orkka/STO-2", CancellationToken.None))
            .Succeeded.Should().BeTrue("the task branch is now in the user's repository");
        (await Out(UserRepo, "rev-parse --abbrev-ref HEAD")).Trim().Should().Be(baseBranch, "the user's checkout never moved");
        (await Out(UserRepo, "status --porcelain")).Should().BeEmpty("the working tree was not touched by the push");

        // 5. The merge processor lands it: squash in a scratch clone, fast-forward the folder.
        var merge = await _ops.SquashMergeIntoUserRepoAsync(new SquashMergeRequest(
            RunnerTokens.UserRepo(org, repo), "orkka/STO-2", baseBranch, "STO-2: feature (#1)"));
        merge.Success.Should().BeTrue(merge.Error);
        File.Exists(Path.Combine(UserRepo, "feature.txt")).Should().BeTrue();
        (await Out(UserRepo, "log -1 --format=%s|%an")).Trim().Should().Be($"STO-2: feature (#1)|{TestGitIdentity.Name}");
    }

    [Fact]
    public async Task ASecondPushOfTheSameBranch_IsAFastForward_NotAnError()
    {
        await SeedRepoAsync();
        var org = _orgId.ToString();
        var repo = _repoId.ToString();
        var task = _taskId.ToString();
        (await _ops.EnsureBareCloneAsync(new EnsureBareCloneRequest(org, repo, UserRepo))).Success.Should().BeTrue();
        (await _ops.CreateWorktreeAsync(new CreateWorktreeRequest(org, repo, task, "orkka/STO-3", "main"))).Success.Should().BeTrue();
        var worktreePath = _manager.GetWorktreePath(_orgId, _repoId, _taskId);

        await File.WriteAllTextAsync(Path.Combine(worktreePath, "a.txt"), "1\n");
        await Run(worktreePath, "add -A");
        await Run(worktreePath, "-c user.name=Agent -c user.email=agent@example.test commit -m one");
        (await _ops.EnsurePushedAsync(new EnsurePushedRequest(org, repo, task, "orkka/STO-3"))).Success.Should().BeTrue();

        // Another dev pass (review feedback) commits more; the platform pushes again.
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "a.txt"), "2\n");
        await Run(worktreePath, "-c user.name=Agent -c user.email=agent@example.test commit -am two");
        var again = await _ops.EnsurePushedAsync(new EnsurePushedRequest(org, repo, task, "orkka/STO-3"));

        again.Success.Should().BeTrue(again.Error);
        (await Out(UserRepo, "log -1 --format=%s orkka/STO-3")).Trim().Should().Be("two");
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private async Task SeedRepoAsync()
    {
        Directory.CreateDirectory(UserRepo);
        await Run(UserRepo, "init -b main");
        await File.WriteAllTextAsync(Path.Combine(UserRepo, "README.md"), "seed\n");
        await Run(UserRepo, "add -A");
        await Run(UserRepo, "-c user.name=User -c user.email=user@example.test commit -m init");
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
        try { GitWorkspaceManager.DeleteDirectoryTree(_root); }
        catch { /* temp */ }
    }
}
