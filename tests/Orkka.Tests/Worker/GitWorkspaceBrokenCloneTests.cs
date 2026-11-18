namespace Orkka.Tests.Worker;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Orkka.Modules.Execution.Git;
using FluentAssertions;

/// <summary>
/// A clone that fails must not leave a directory behind that later reads as a mirror.
///
/// This is the shape of a real production failure. `git clone --bare` creates the target and
/// copies its templates in BEFORE it contacts the remote, so a clone that fails on
/// authentication — a managed repository attempted with the user's own credentials, which is
/// the runner's deliberate ambient-first first try — leaves a directory that looks cloned and
/// is not. Every later call then took the "refresh an existing mirror" path, whose failures
/// are deliberately non-fatal ("continuing on the mirror we have"), so the clone never retried,
/// EnsureBareClone reported SUCCESS, and the task died much later and somewhere else with
/// `worktree add` → "fatal: not a git repository". Two tasks blocked on a brand-new project,
/// and nothing in the message pointed at the clone.
///
/// Real git against a temp origin; the manager is the same class the cloud worker and the
/// user's runner both run.
/// </summary>
public sealed class GitWorkspaceBrokenCloneTests : IDisposable
{
    private readonly string _basePath;
    private readonly string _cachePath;
    private readonly string _originPath;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();

    private const string Identity = "-c user.name=Seed -c user.email=seed@localhost";

    public GitWorkspaceBrokenCloneTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"orkka-broken-clone-tests-{Ulid.NewUlid()}");
        _cachePath = Path.Combine(_basePath, "cache");
        _originPath = Path.Combine(_basePath, "origin.git");
    }

    private GitWorkspaceManager CreateManager()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = _cachePath,
            })
            .Build();
        return new GitWorkspaceManager(config, Mock.Of<ILogger<GitWorkspaceManager>>());
    }

    private string BarePath => Path.Combine(
        _cachePath, _orgId.ToString(), _repoId.ToString(), "bare");

    private async Task SeedOriginAsync()
    {
        Directory.CreateDirectory(_originPath);
        await RunGit(_originPath, "init --bare --initial-branch=main");

        var authorPath = Path.Combine(_basePath, "author");
        Directory.CreateDirectory(_basePath);
        await RunGit(_basePath, $"clone \"{_originPath}\" author");
        await RunGit(authorPath, "checkout -b main");
        await File.WriteAllTextAsync(Path.Combine(authorPath, "README.md"), "seed\n");
        await RunGit(authorPath, "add -A");
        await RunGit(authorPath, $"{Identity} commit -m \"init\"");
        await RunGit(authorPath, "push -u origin main");
    }

    /// <summary>Exactly what a failed `git clone --bare` leaves behind: the directory, with
    /// the templates it copied before it ever reached the network.</summary>
    private async Task PoisonTheCacheAsync()
    {
        Directory.CreateDirectory(BarePath);
        await File.WriteAllTextAsync(Path.Combine(BarePath, "description"),
            "Unnamed repository; edit this file 'description' to name the repository.\n");
        Directory.CreateDirectory(Path.Combine(BarePath, "hooks"));
        Directory.CreateDirectory(Path.Combine(BarePath, "info"));
    }

    [Fact]
    public async Task AHalfWrittenCache_IsReplaced_NotMistakenForAMirror()
    {
        await SeedOriginAsync();
        await PoisonTheCacheAsync();

        var result = await CreateManager().EnsureBareCloneAsync(_orgId, _repoId, _originPath);

        result.IsSuccess.Should().BeTrue(result.Error);

        // The whole point: what comes back is a real repository, so the worktree that follows
        // has something to branch from.
        (await GitOutput(BarePath, "rev-parse --resolve-git-dir .")).Should().NotBeEmpty();
        (await GitOutput(BarePath, "rev-parse --verify refs/remotes/origin/main")).Should().NotBeEmpty();
    }

    [Fact]
    public async Task AHalfWrittenCache_LetsTheWorktreeSucceed()
    {
        // The failure as the user meets it: the task blocks on worktree creation, several
        // steps away from the clone that actually went wrong.
        await SeedOriginAsync();
        await PoisonTheCacheAsync();
        var manager = CreateManager();

        await manager.EnsureBareCloneAsync(_orgId, _repoId, _originPath);
        var worktree = await manager.CreateWorktreeAsync(
            _orgId, _repoId, Ulid.NewUlid(), "orkka/task-1", "main");

        worktree.IsSuccess.Should().BeTrue(worktree.Error);
    }

    [Fact]
    public async Task AFailedClone_LeavesNothingBehindForTheNextAttemptToTripOver()
    {
        // First attempt against a remote that does not exist — the shape of a managed repo
        // tried with credentials that cannot see it.
        var manager = CreateManager();
        var missingOrigin = Path.Combine(_basePath, "does-not-exist.git");

        var failed = await manager.EnsureBareCloneAsync(_orgId, _repoId, missingOrigin);
        failed.IsFailure.Should().BeTrue();
        Directory.Exists(BarePath).Should().BeFalse(
            "a directory left here is what makes the next attempt fail with 'File exists' "
            + "and every attempt after that with 'not a git repository'");

        // And the retry that would carry a working credential now succeeds.
        await SeedOriginAsync();
        var retried = await manager.EnsureBareCloneAsync(_orgId, _repoId, _originPath);
        retried.IsSuccess.Should().BeTrue(retried.Error);
    }

    [Fact]
    public async Task AHealthyCache_IsLeftAlone()
    {
        // The guard deletes things. It must never fire on a working mirror.
        await SeedOriginAsync();
        var manager = CreateManager();

        var first = await manager.EnsureBareCloneAsync(_orgId, _repoId, _originPath);
        first.IsSuccess.Should().BeTrue(first.Error);
        var headBefore = await GitOutput(BarePath, "rev-parse refs/remotes/origin/main");
        var markerPath = Path.Combine(BarePath, "orkka-cache-marker");
        await File.WriteAllTextAsync(markerPath, "kept");

        var second = await manager.EnsureBareCloneAsync(_orgId, _repoId, _originPath);

        second.IsSuccess.Should().BeTrue(second.Error);
        File.Exists(markerPath).Should().BeTrue("the existing mirror must be reused, not re-cloned");
        (await GitOutput(BarePath, "rev-parse refs/remotes/origin/main")).Should().Be(headBefore);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_basePath))
            return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_basePath, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_basePath, recursive: true);
        }
        catch (IOException)
        {
            // A git process may still hold a handle; the temp folder is disposable either way.
        }
    }

    private static async Task RunGit(string workingDirectory, string arguments)
    {
        using var process = StartGit(workingDirectory, arguments);
        await process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
    }

    private static async Task<string> GitOutput(string workingDirectory, string arguments)
    {
        using var process = StartGit(workingDirectory, arguments);
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output.Trim();
    }

    private static System.Diagnostics.Process StartGit(string workingDirectory, string arguments)
        => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
}
