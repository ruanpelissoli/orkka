namespace Orkka.Tests.Worker;

using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Orkka.Modules.Execution.Git;

/// <summary>
/// What "does this branch exist on the remote" answers when the remote cannot be asked.
///
/// RemoteBranchExistsAsync is an ls-remote, run with whatever credential the mirror's origin
/// URL carries. On a user's runner that is their own credential, which cannot read a managed
/// repo in Orkka's org — so ls-remote failed, the answer was "no", and the orchestrator
/// "ensured" (on the forge, successfully) a branch the mirror could then never fetch with
/// the same credential. The fallback answers from the mirror's own remote-tracking refs,
/// which the token-authenticated refresh keeps current.
///
/// Real git against a temp origin, like the other workspace tests.
/// </summary>
public sealed class GitWorkspaceRemoteBranchProbeTests : IDisposable
{
    private readonly string _basePath =
        Path.Combine(Path.GetTempPath(), $"orkka-remote-probe-tests-{Ulid.NewUlid()}");
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();

    private string CachePath => Path.Combine(_basePath, "cache");
    private string OriginPath => Path.Combine(_basePath, "origin.git");
    private string BarePath => Path.Combine(CachePath, _orgId.ToString(), _repoId.ToString(), "bare");

    private const string Identity = "-c user.name=Seed -c user.email=seed@localhost";

    public void Dispose()
    {
        if (Directory.Exists(_basePath))
        {
            try { Directory.Delete(_basePath, recursive: true); } catch { /* temp — best effort */ }
        }
    }

    private GitWorkspaceManager CreateManager()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:RepoBasePath"] = CachePath })
            .Build();
        return new GitWorkspaceManager(config, Mock.Of<ILogger<GitWorkspaceManager>>());
    }

    private async Task SeedOriginAsync()
    {
        Directory.CreateDirectory(OriginPath);
        await RunGit(OriginPath, "init --bare --initial-branch=main");

        var authorPath = Path.Combine(_basePath, "author");
        await RunGit(_basePath, $"clone \"{OriginPath}\" author");
        await RunGit(authorPath, "checkout -b main");
        await File.WriteAllTextAsync(Path.Combine(authorPath, "README.md"), "seed\n");
        await RunGit(authorPath, "add -A");
        await RunGit(authorPath, $"{Identity} commit -m \"init\"");
        await RunGit(authorPath, "push -u origin main");
    }

    [Fact]
    public async Task WhenTheRemoteCanBeAsked_TheRemoteAnswers()
    {
        await SeedOriginAsync();
        var manager = CreateManager();
        (await manager.EnsureBareCloneAsync(_orgId, _repoId, OriginPath)).IsSuccess.Should().BeTrue();

        (await manager.RemoteBranchExistsAsync(_orgId, _repoId, "main")).Should().BeTrue();
        (await manager.RemoteBranchExistsAsync(_orgId, _repoId, "orkka/milestone/nope")).Should().BeFalse();
    }

    [Fact]
    public async Task WhenTheRemoteCannotBeAsked_TheMirrorsRefsAnswer()
    {
        await SeedOriginAsync();
        var manager = CreateManager();
        (await manager.EnsureBareCloneAsync(_orgId, _repoId, OriginPath)).IsSuccess.Should().BeTrue();

        // The mirror has origin/main; now make the remote unreachable, as an unauthorised
        // credential does on a managed repo.
        await RunGit(BarePath, $"remote set-url origin \"{Path.Combine(_basePath, "does-not-exist.git")}\"");

        // A failure to ask is not "the branch does not exist".
        (await manager.RemoteBranchExistsAsync(_orgId, _repoId, "main")).Should().BeTrue(
            "the mirror fetched origin/main before the remote became unreachable");

        // …but a branch the mirror never fetched is still absent, which is what makes the
        // caller go and create or fetch it.
        (await manager.RemoteBranchExistsAsync(_orgId, _repoId, "orkka/milestone/never-fetched")).Should().BeFalse();
    }

    private static async Task RunGit(string workingDirectory, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        await process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
    }
}
