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
/// The runner as the host drives it, through the real engine and the real workspace manager,
/// on a developer's machine: a remote is attempted with the developer's own credentials
/// rather than refused for want of a platform token; commits carry the developer's identity
/// (the one the runner was configured with, else their own git config); a token given to a
/// push rides in the environment; the sync status names the remote a push would use.
/// </summary>
[Collection(Orkka.Tests.GitEngineStaticsCollection.Name)]
public sealed class RunnerOperationsEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orkka-runner-eng-{Ulid.NewUlid()}");
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();
    private readonly GitProcess _git = new(NullLogger<GitProcess>.Instance);
    private readonly GitWorkspaceManager _manager;
    private readonly RunnerOperations _ops;
    private string UserRepo => Path.Combine(_root, "user-repo");
    private string Remote => Path.Combine(_root, "remote.git");

    public RunnerOperationsEngineTests()
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
    public async Task ARemote_IsNotRefusedForWantOfAToken_OnADevelopersMachine()
    {
        var result = await _ops.EnsureBareCloneAsync(new EnsureBareCloneRequest(
            _orgId.ToString(), _repoId.ToString(), "https://127.0.0.1:1/acme/app.git"));

        // Nothing listens there, so the clone fails — but git ran and the network answered.
        result.Success.Should().BeFalse();
        result.Error.Should().NotStartWith(nameof(GitErrorCode.NoCredential));
    }

    [Fact]
    public async Task ALocalFolder_IsCloned_WithoutForcingAnIdentity()
    {
        await SeedUserRepoAsync();

        var result = await _ops.EnsureBareCloneAsync(new EnsureBareCloneRequest(
            _orgId.ToString(), _repoId.ToString(), UserRepo));

        result.Success.Should().BeTrue(result.Error);
        (await Out(_manager.GetBareRepoPath(_orgId, _repoId), "config --local user.name"))
            .Should().BeEmpty("the developer's own git config decides");
    }

    [Fact]
    public async Task AConfiguredIdentity_IsWrittenOnTheMirror()
    {
        await SeedUserRepoAsync();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = Path.Combine(_root, "cache-identity"),
                ["Git:CommitAuthorName"] = "Dana Dev",
                ["Git:CommitAuthorEmail"] = "dana@acme.example",
            })
            .Build();
        var manager = new GitWorkspaceManager(config, NullLogger<GitWorkspaceManager>.Instance, _git, NullGitOperationLedger.Instance);

        var result = await manager.EnsureBareCloneAsync(_orgId, _repoId, UserRepo);

        result.IsSuccess.Should().BeTrue(result.Error);
        (await Out(manager.GetBareRepoPath(_orgId, _repoId), "config user.name")).Should().Be("Dana Dev");
    }

    [Fact]
    public async Task SyncStatus_NamesTheRemote_AndPushWithATokenReachesIt()
    {
        await SeedUserRepoAsync();
        await Run(_root, "init --bare -b main remote.git");
        await Run(UserRepo, $"remote add origin \"{Remote}\"");
        (await _ops.EnsureBareCloneAsync(new EnsureBareCloneRequest(
            _orgId.ToString(), _repoId.ToString(), UserRepo))).Success.Should().BeTrue();
        var repoToken = RunnerTokens.UserRepo(_orgId.ToString(), _repoId.ToString());

        var before = await _ops.GetRemoteSyncStatusAsync(new RemoteSyncStatusRequest(repoToken));
        before.HasRemote.Should().BeTrue();
        before.HasUpstream.Should().BeFalse();
        before.RemoteUrl.Should().Be(Remote);

        // The push uses the machine's own git credentials, like any other.
        var push = await _ops.PushToRemoteAsync(new PushToRemoteRequest(repoToken, "main"));
        push.Success.Should().BeTrue(push.Error);

        var after = await _ops.GetRemoteSyncStatusAsync(new RemoteSyncStatusRequest(repoToken));
        after.HasUpstream.Should().BeTrue();
        after.Ahead.Should().Be(0);
        (await Out(Remote, "log -1 --format=%s main")).Should().Be("init");
    }

    [Fact]
    public async Task TheProbe_RunsGitOnARemote_AndReportsAFolderAsReachable()
    {
        await SeedUserRepoAsync();

        var result = await RepoAccessProbing.ProbeAsync([
            new RepoAccessProbe("folder", UserRepo),
            new RepoAccessProbe("forge", "https://127.0.0.1:1/acme/app.git"),
        ], timeout: TimeSpan.FromSeconds(30));

        result.Results.Single(r => r.RepoId == "folder").Ok.Should().BeTrue();
        var forge = result.Results.Single(r => r.RepoId == "forge");
        forge.Ok.Should().BeFalse();
        forge.Error.Should().NotStartWith(nameof(GitErrorCode.NoCredential), "git ran; the network answered");
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private async Task SeedUserRepoAsync()
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
        try { GitWorkspaceManager.DeleteDirectoryTree(_root); } catch { /* temp */ }
    }
}
