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
/// Exercises PublishRepoToGitHub's runner-side validation — every path that must fail
/// BEFORE the gh CLI is invoked (tests never create anything on GitHub). Since protocol
/// v3 the op receives a RunnerTokens.UserRepo token, resolved runner-side through the
/// bare mirror's origin URL — the tests mirror that cache layout.
/// </summary>
[Collection(Orkka.Tests.GitEngineStaticsCollection.Name)]
public sealed class RunnerOperationsPublishTests : IDisposable
{
    private readonly string _basePath;
    private readonly string _cacheRoot;
    private readonly RunnerOperations _ops;

    public RunnerOperationsPublishTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"orkka-runner-publish-tests-{Ulid.NewUlid()}");
        _cacheRoot = Path.Combine(_basePath, "cache");
        Directory.CreateDirectory(_basePath);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = _cacheRoot,
            })
            .Build();

        // Token resolution goes through GetBareRepoPath — reproduce the real
        // {cache}/{org}/{repo}/bare layout so TokenFor-created mirrors are found.
        var git = new Mock<IGitWorkspaceManager>();
        git.Setup(g => g.GetBareRepoPath(It.IsAny<Ulid>(), It.IsAny<Ulid>()))
            .Returns((Ulid orgId, Ulid repoId) =>
                Path.Combine(_cacheRoot, orgId.ToString(), repoId.ToString(), "bare"));

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

    private async Task<string> CreateRepoAsync(string name, bool commit = true)
    {
        var path = Path.Combine(_basePath, name);
        Directory.CreateDirectory(path);
        await RunGit(path, "init -b main");
        if (commit)
        {
            await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "# Test");
            await RunGit(path, "add -A");
            await RunGit(path, "-c user.name=Orkka -c user.email=orkka@localhost commit -m \"init\"");
        }
        return path;
    }

    /// <summary>Registers a bare mirror whose origin points at the given working repo —
    /// exactly how the runner locates a user repo — and returns its wire token.</summary>
    private async Task<string> TokenFor(string userRepoPath)
    {
        var orgId = Ulid.NewUlid();
        var repoId = Ulid.NewUlid();
        var bare = Path.Combine(_cacheRoot, orgId.ToString(), repoId.ToString(), "bare");
        Directory.CreateDirectory(bare);
        await RunGit(bare, "init --bare");
        await RunGit(bare, $"remote add origin \"{userRepoPath}\"");
        return RunnerTokens.UserRepo(orgId.ToString(), repoId.ToString());
    }

    [Fact]
    public async Task Publish_MalformedToken_Fails()
    {
        // Raw filesystem paths no longer cross the wire — they must be rejected outright.
        var result = await _ops.PublishRepoToGitHubAsync(
            new PublishRepoToGitHubRequest(Path.Combine(_basePath, "some-repo"), null, Private: true));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Invalid repository token");
    }

    [Fact]
    public async Task Publish_TokenWithoutMirror_FailsActionably()
    {
        var token = RunnerTokens.UserRepo(Ulid.NewUlid().ToString(), Ulid.NewUlid().ToString());

        var result = await _ops.PublishRepoToGitHubAsync(
            new PublishRepoToGitHubRequest(token, null, Private: true));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("cache not prepared");
    }

    [Fact]
    public async Task Publish_NotAGitRepo_Fails()
    {
        var path = Path.Combine(_basePath, "plain-folder");
        Directory.CreateDirectory(path);

        var result = await _ops.PublishRepoToGitHubAsync(
            new PublishRepoToGitHubRequest(await TokenFor(path), null, Private: true));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Not a git repository");
    }

    [Fact]
    public async Task Publish_InvalidName_Fails()
    {
        var path = await CreateRepoAsync("valid-repo");

        var result = await _ops.PublishRepoToGitHubAsync(
            new PublishRepoToGitHubRequest(await TokenFor(path), "bad name!", Private: true));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Repository names");
    }

    [Fact]
    public async Task Publish_ExistingOrigin_Fails()
    {
        var path = await CreateRepoAsync("has-origin");
        await RunGit(path, "remote add origin https://example.com/some/repo.git");

        var result = await _ops.PublishRepoToGitHubAsync(
            new PublishRepoToGitHubRequest(await TokenFor(path), null, Private: true));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("already has an origin remote");
        result.Error.Should().Contain("https://example.com/some/repo.git");
    }

    [Fact]
    public async Task Publish_NoCommits_Fails()
    {
        var path = await CreateRepoAsync("empty-repo", commit: false);

        var result = await _ops.PublishRepoToGitHubAsync(
            new PublishRepoToGitHubRequest(await TokenFor(path), null, Private: true));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("no commits");
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
}
