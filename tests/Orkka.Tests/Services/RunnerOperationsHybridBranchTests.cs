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
/// Branch creation in the HYBRID topology: the user's runner, executing against a repo that
/// lives on a forge. There is no working copy on this machine — the cache's origin is a remote
/// URL, not a folder — so the branch has to be created on the forge itself.
///
/// This is the shape that broke milestone branching in production. Every attempt returned
/// "Could not locate the working repository behind this cache.", the orchestrator degraded to
/// the repo's default branch, and each task merged straight to main — one deploy per task,
/// which is exactly what the milestone branch exists to prevent. The failure was invisible
/// because the fallback is deliberately silent (a task that cannot start is worse than a task
/// that costs a deploy), so it showed up only as "the feature does nothing".
/// </summary>
[Collection(Orkka.Tests.GitEngineStaticsCollection.Name)]
public sealed class RunnerOperationsHybridBranchTests : IDisposable
{
    private readonly string _basePath;
    private readonly string _forgePath;
    private readonly string _barePath;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();
    private readonly RunnerOperations _ops;

    public RunnerOperationsHybridBranchTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"orkka-runner-hybrid-{Ulid.NewUlid()}");
        _forgePath = Path.Combine(_basePath, "github.git");
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
            new RunnerHostLink(),
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

    /// <summary>
    /// Forge bare repo ← the runner's cache, with origin as a `file://` URL.
    ///
    /// The URL is what makes this the hybrid case rather than the Orkka Local one: the runner
    /// resolves "is there a working copy behind this cache" by asking whether origin is an
    /// existing directory, and a URL is not. A plain path here would silently exercise the
    /// Orkka Local path instead and prove nothing.
    /// </summary>
    private async Task SetupHybridTopologyAsync()
    {
        Directory.CreateDirectory(_forgePath);
        await RunGit(_forgePath, "init --bare");

        var seed = Path.Combine(_basePath, "seed");
        Directory.CreateDirectory(seed);
        await RunGit(seed, "init");
        await RunGit(seed, "checkout -b main");
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "# Test");
        await RunGit(seed, "add -A");
        await RunGit(seed, "-c user.name=Orkka -c user.email=orkka@localhost commit -m \"init\"");
        await RunGit(seed, $"remote add origin \"{ForgeUrl}\"");
        await RunGit(seed, "push -u origin main");

        Directory.CreateDirectory(Path.GetDirectoryName(_barePath)!);
        await RunGit(Path.GetDirectoryName(_barePath)!, $"clone --bare \"{ForgeUrl}\" bare");
        await RunGit(_barePath, "config remote.origin.fetch \"+refs/heads/*:refs/remotes/origin/*\"");
        await RunGit(_barePath, $"remote set-url origin \"{ForgeUrl}\"");
        await RunGit(_barePath, "fetch origin");
    }

    private string ForgeUrl => "file:///" + _forgePath.Replace('\\', '/').TrimStart('/');

    private CreateRepoBranchRequest Request(string newBranch, string baseBranch = "main")
        => new(_orgId.ToString(), _repoId.ToString(), newBranch, baseBranch);

    /// <summary>
    /// The forge is the only place a branch on a forge repository is created — through the
    /// API, by the host. A ref push from this machine's cache was the second of four
    /// implementations of "create a branch", each able to half-succeed on its own; the runner
    /// now refuses so the host never mistakes a runner-side push for the forge's answer.
    /// </summary>
    [Fact]
    public async Task CreateRepoBranch_WithNoWorkingCopy_RefusesAndPointsAtTheForgeApi()
    {
        await SetupHybridTopologyAsync();

        var result = await _ops.CreateRepoBranchAsync(
            Request("orkka/milestone/checkout-flow-a1b2c3d4"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("forge API");
        (await GitOutput(_forgePath, "branch --list orkka/milestone/checkout-flow-a1b2c3d4"))
            .Should().BeEmpty("nothing on this machine pushes refs to a forge");
    }

    [Fact]
    public async Task CreateRepoBranch_RejectsUnsafeRefs_BeforeTouchingGit()
    {
        await SetupHybridTopologyAsync();

        var result = await _ops.CreateRepoBranchAsync(Request("evil\" --upload-pack=touch"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Invalid branch name");
    }

    [Fact]
    public async Task EnsureLocalBranch_WithNoWorkingCopy_SucceedsFromTheCache()
    {
        await SetupHybridTopologyAsync();

        var result = await _ops.EnsureLocalBranchAsync(
            new EnsureLocalBranchRequest(_orgId.ToString(), _repoId.ToString(), "main"));

        // Nothing to materialize in hybrid — the cache already mirrors the forge. Failing here
        // broke the repo page's base-branch picker with a message about a working repository
        // the user does not have.
        result.Success.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task EnsureLocalBranch_WithNoWorkingCopy_StillRejectsAnUnknownBranch()
    {
        await SetupHybridTopologyAsync();

        var result = await _ops.EnsureLocalBranchAsync(
            new EnsureLocalBranchRequest(_orgId.ToString(), _repoId.ToString(), "no-such-branch"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("was not found");
    }

    [Fact]
    public async Task CreateRepoBranch_WithNoCachePrepared_SaysSo()
    {
        // No topology: the repo was never cloned on this runner.
        var result = await _ops.CreateRepoBranchAsync(Request("orkka/milestone/nothing-55556666"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("cache not prepared");
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
