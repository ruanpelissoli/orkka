namespace Orkka.Tests.Services;

using Orkka.LocalRunner;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Execution.Agents;
using Orkka.Modules.SourceControl.Contracts;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Exercises the runner-side branch ops against the real Orkka Local topology:
/// a "remote" bare repo (stand-in for GitHub) ← the user's working repo ← the runner's
/// bare mirror (whose origin IS the user's repo).
/// </summary>
[Collection(Orkka.Tests.GitEngineStaticsCollection.Name)]
public sealed class RunnerOperationsBranchTests : IDisposable
{
    private readonly string _basePath;
    private readonly string _remotePath;
    private readonly string _userRepoPath;
    private readonly string _barePath;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();
    private readonly RunnerOperations _ops;

    public RunnerOperationsBranchTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"orkka-runner-branch-tests-{Ulid.NewUlid()}");
        _remotePath = Path.Combine(_basePath, "github.git");
        _userRepoPath = Path.Combine(_basePath, "user-repo");
        _barePath = Path.Combine(_basePath, "cache", "bare");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = Path.Combine(_basePath, "cache"),
            })
            .Build();

        var git = new Mock<IGitWorkspaceManager>();
        git.Setup(g => g.GetBareRepoPath(_orgId, _repoId)).Returns(_barePath);

        _ops = new RunnerOperations(
            git.Object,
            Mock.Of<IAiAgentInvoker>(),
            new ProviderDetection(Mock.Of<ILogger<ProviderDetection>>()),
            config,
            new Orkka.LocalRunner.RunnerHostLink(),
            Mock.Of<ILogger<RunnerOperations>>());
    }

    public void Dispose()
    {
        if (!Directory.Exists(_basePath))
            return;
        foreach (var file in Directory.EnumerateFiles(_basePath, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_basePath, recursive: true);
    }

    /// <summary>Remote bare repo + user repo (pushed to it) + runner mirror of the user
    /// repo, with "remote-only" existing solely as the user repo's remote-tracking ref.</summary>
    private async Task SetupTopologyAsync()
    {
        Directory.CreateDirectory(_remotePath);
        await RunGit(_remotePath, "init --bare");

        Directory.CreateDirectory(_userRepoPath);
        await RunGit(_userRepoPath, "init");
        await RunGit(_userRepoPath, "checkout -b main");
        await File.WriteAllTextAsync(Path.Combine(_userRepoPath, "README.md"), "# Test");
        await RunGit(_userRepoPath, "add -A");
        await RunGit(_userRepoPath, "-c user.name=Orkka -c user.email=orkka@localhost commit -m \"init\"");
        await RunGit(_userRepoPath, $"remote add origin \"{_remotePath}\"");
        await RunGit(_userRepoPath, "push -u origin main");

        // A branch that lives only on the remote: push it, then delete the local ref.
        await RunGit(_userRepoPath, "checkout -b remote-only");
        await File.WriteAllTextAsync(Path.Combine(_userRepoPath, "feature.txt"), "wip");
        await RunGit(_userRepoPath, "add -A");
        await RunGit(_userRepoPath, "-c user.name=Orkka -c user.email=orkka@localhost commit -m \"feature\"");
        await RunGit(_userRepoPath, "push origin remote-only");
        await RunGit(_userRepoPath, "checkout main");
        await RunGit(_userRepoPath, "branch -D remote-only");

        // The runner's mirror clones the USER repo (matching EnsureBareCloneAsync wiring).
        Directory.CreateDirectory(Path.GetDirectoryName(_barePath)!);
        await RunGit(Path.GetDirectoryName(_barePath)!, $"clone --bare \"{_userRepoPath}\" bare");
        await RunGit(_barePath, "config remote.origin.fetch \"+refs/heads/*:refs/remotes/origin/*\"");
        await RunGit(_barePath, $"remote set-url origin \"{_userRepoPath}\"");
        await RunGit(_barePath, "fetch origin");
    }

    [Fact]
    public async Task ListBranches_IncludesRemoteOnlyBranches_FlaggedIsRemote()
    {
        await SetupTopologyAsync();

        var result = await _ops.ListBranchesAsync(
            new ListBranchesRequest(_orgId.ToString(), _repoId.ToString()));

        result.Success.Should().BeTrue(result.Error);
        result.Branches.Should().Contain(b => b.Name == "main" && !b.IsRemote);
        result.Branches.Should().Contain(b => b.Name == "remote-only" && b.IsRemote);
    }

    [Fact]
    public async Task EnsureLocalBranch_MaterializesRemoteBranch_WithoutTouchingWorkingTree()
    {
        await SetupTopologyAsync();

        var result = await _ops.EnsureLocalBranchAsync(
            new EnsureLocalBranchRequest(_orgId.ToString(), _repoId.ToString(), "remote-only"));

        result.Success.Should().BeTrue(result.Error);

        // The local ref now exists, HEAD stayed on main, and the working tree is clean
        // (feature.txt from the remote-only branch was NOT checked out).
        (await GitOutput(_userRepoPath, "rev-parse --abbrev-ref HEAD")).Should().Be("main");
        (await GitOutput(_userRepoPath, "branch --list remote-only")).Should().Contain("remote-only");
        File.Exists(Path.Combine(_userRepoPath, "feature.txt")).Should().BeFalse();

        // And the branch is no longer remote-only in listings.
        var branches = await _ops.ListBranchesAsync(
            new ListBranchesRequest(_orgId.ToString(), _repoId.ToString()));
        branches.Branches.Should().Contain(b => b.Name == "remote-only" && !b.IsRemote);
    }

    [Fact]
    public async Task EnsureLocalBranch_AlreadyLocal_Succeeds()
    {
        await SetupTopologyAsync();

        var result = await _ops.EnsureLocalBranchAsync(
            new EnsureLocalBranchRequest(_orgId.ToString(), _repoId.ToString(), "main"));

        result.Success.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task EnsureLocalBranch_UnknownBranch_Fails()
    {
        await SetupTopologyAsync();

        var result = await _ops.EnsureLocalBranchAsync(
            new EnsureLocalBranchRequest(_orgId.ToString(), _repoId.ToString(), "nope"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("was not found");
    }

    [Fact]
    public async Task CreateRepoBranch_FromLocalBase_CreatesRefAndShowsInMirror()
    {
        await SetupTopologyAsync();

        var result = await _ops.CreateRepoBranchAsync(new CreateRepoBranchRequest(
            _orgId.ToString(), _repoId.ToString(), "feature/new", "main"));

        result.Success.Should().BeTrue(result.Error);
        (await GitOutput(_userRepoPath, "rev-parse --abbrev-ref HEAD")).Should().Be("main");

        var branches = await _ops.ListBranchesAsync(
            new ListBranchesRequest(_orgId.ToString(), _repoId.ToString()));
        branches.Branches.Should().Contain(b => b.Name == "feature/new" && !b.IsRemote);
    }

    [Fact]
    public async Task CreateRepoBranch_FromRemoteOnlyBase_Succeeds()
    {
        await SetupTopologyAsync();

        var result = await _ops.CreateRepoBranchAsync(new CreateRepoBranchRequest(
            _orgId.ToString(), _repoId.ToString(), "feature/from-remote", "remote-only"));

        result.Success.Should().BeTrue(result.Error);

        // Based on the remote-tracking tip, not main.
        var newSha = await GitOutput(_userRepoPath, "rev-parse feature/from-remote");
        var remoteSha = await GitOutput(_userRepoPath, "rev-parse refs/remotes/origin/remote-only");
        newSha.Should().Be(remoteSha);
    }

    [Fact]
    public async Task CreateRepoBranch_ExistingName_Fails()
    {
        await SetupTopologyAsync();

        var result = await _ops.CreateRepoBranchAsync(new CreateRepoBranchRequest(
            _orgId.ToString(), _repoId.ToString(), "main", "main"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("already exists");
    }

    [Fact]
    public async Task CreateRepoBranch_MissingBase_Fails()
    {
        await SetupTopologyAsync();

        var result = await _ops.CreateRepoBranchAsync(new CreateRepoBranchRequest(
            _orgId.ToString(), _repoId.ToString(), "feature/x", "ghost"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("was not found");
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
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new Exception($"Git failed ({arguments}): {error}");
        }
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
