namespace Orkka.Tests.Worker;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Orkka.Modules.Execution.Git;
using FluentAssertions;

/// <summary>
/// Commits made in a task's repo — by the platform or by an agent inside a worktree — carry the
/// identity the runner workspace was configured with, and with none configured, the developer's
/// own git identity. Real git against a temp origin; the manager is the class the runner uses.
/// </summary>
public sealed class GitWorkspaceCommitIdentityTests : IDisposable
{
    private readonly string _basePath;
    private readonly string _originPath;
    private readonly string _authorPath;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();
    private readonly Ulid _taskId = Ulid.NewUlid();

    private const string Identity = "-c user.name=Seed -c user.email=seed@localhost";

    public GitWorkspaceCommitIdentityTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"orkka-commit-identity-tests-{Ulid.NewUlid()}");
        _originPath = Path.Combine(_basePath, "origin.git");
        _authorPath = Path.Combine(_basePath, "author");
    }

    private GitWorkspaceManager CreateManager(string? name, string? email)
    {
        var values = new Dictionary<string, string?>
        {
            ["Worker:RepoBasePath"] = Path.Combine(_basePath, "cache"),
        };
        if (name is not null) values["Git:CommitAuthorName"] = name;
        if (email is not null) values["Git:CommitAuthorEmail"] = email;

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new GitWorkspaceManager(config, Mock.Of<ILogger<GitWorkspaceManager>>());
    }

    private async Task SeedOriginAsync()
    {
        Directory.CreateDirectory(_originPath);
        await RunGit(_originPath, "init --bare --initial-branch=main");
        Directory.CreateDirectory(_authorPath);
        await RunGit(_basePath, $"clone \"{_originPath}\" author");
        await RunGit(_authorPath, "checkout -b main");
        await File.WriteAllTextAsync(Path.Combine(_authorPath, "README.md"), "seed\n");
        await RunGit(_authorPath, "add -A");
        await RunGit(_authorPath, $"{Identity} commit -m \"init\"");
        await RunGit(_authorPath, "push -u origin main");
    }

    [Fact]
    public async Task ConfiguredIdentity_IsWrittenToTheBareRepo_AndUsedByPlatformAndAgentCommits()
    {
        await SeedOriginAsync();
        var manager = CreateManager("orkka-ai[bot]", "12345+orkka-ai[bot]@users.noreply.github.com");

        var clone = await manager.EnsureBareCloneAsync(_orgId, _repoId, _originPath);
        clone.IsSuccess.Should().BeTrue(clone.Error);

        // The bare repo carries the identity — worktrees share its config.
        (await GitOutput(clone.Value!, "config user.name")).Should().Be("orkka-ai[bot]");
        (await GitOutput(clone.Value!, "config user.email")).Should().Be("12345+orkka-ai[bot]@users.noreply.github.com");

        var worktree = await manager.CreateWorktreeAsync(_orgId, _repoId, _taskId, "orkka/task", "main");
        worktree.IsSuccess.Should().BeTrue(worktree.Error);

        // An "agent" commit: plain git inside the worktree, no identity flags — exactly what a
        // coding agent runs. It must inherit the bot identity, not the machine's global user.
        await File.WriteAllTextAsync(Path.Combine(worktree.Value!, "agent.txt"), "agent work\n");
        await RunGit(worktree.Value!, "add -A");
        await RunGit(worktree.Value!, "commit -m \"agent commit\"");
        (await GitOutput(worktree.Value!, "log -1 --format=%an|%ae")).Should()
            .Be("orkka-ai[bot]|12345+orkka-ai[bot]@users.noreply.github.com");

        // A platform commit through the manager, pushed to origin.
        await File.WriteAllTextAsync(Path.Combine(worktree.Value!, "platform.txt"), "platform work\n");
        var push = await manager.CommitAndPushAsync(worktree.Value!, "platform commit", "orkka/task");
        push.IsSuccess.Should().BeTrue(push.Error);
        (await GitOutput(_originPath, "log -1 --format=%an|%ae orkka/task")).Should()
            .Be("orkka-ai[bot]|12345+orkka-ai[bot]@users.noreply.github.com");
    }

    /// <summary>
    /// A runner with no configured identity leaves the mirror alone: commits are the developer's,
    /// from their own git config (here the test process's stand-in, <see cref="TestGitIdentity"/>).
    /// </summary>
    [Fact]
    public async Task NoConfiguredIdentity_LeavesTheMirrorToTheDevelopersOwnGitConfig()
    {
        await SeedOriginAsync();
        var manager = CreateManager(name: null, email: null);

        var clone = await manager.EnsureBareCloneAsync(_orgId, _repoId, _originPath);
        clone.IsSuccess.Should().BeTrue(clone.Error);

        (await GitOutput(clone.Value!, "config --local user.name")).Should().BeEmpty();
        (await GitOutput(clone.Value!, "config --local user.email")).Should().BeEmpty();
    }

    /// <summary>
    /// Every commit the manager makes itself — the platform commit, the base-sync merge — is the
    /// developer's when no identity is configured: nothing forces a bot over their git config.
    /// </summary>
    [Fact]
    public async Task NoConfiguredIdentity_PlatformCommits_AreTheDevelopers()
    {
        await SeedOriginAsync();
        var manager = CreateManager(name: null, email: null);

        var clone = await manager.EnsureBareCloneAsync(_orgId, _repoId, _originPath);
        clone.IsSuccess.Should().BeTrue(clone.Error);

        var worktree = await manager.CreateWorktreeAsync(_orgId, _repoId, _taskId, "orkka/task", "main");
        worktree.IsSuccess.Should().BeTrue(worktree.Error);

        var developer = $"{TestGitIdentity.Name}|{TestGitIdentity.Email}";

        // A commit of the task's own, so the base sync below has to merge rather than fast-forward.
        await File.WriteAllTextAsync(Path.Combine(worktree.Value!, "task.txt"), "task work\n");
        var push = await manager.CommitAndPushAsync(worktree.Value!, "task work", "orkka/task");
        push.IsSuccess.Should().BeTrue(push.Error);
        (await GitOutput(worktree.Value!, "log -1 --format=%an|%ae")).Should().Be(developer);

        // Another task lands on main, and the base sync merges it in.
        await File.WriteAllTextAsync(Path.Combine(_authorPath, "from-main.txt"), "elsewhere\n");
        await RunGit(_authorPath, "add -A");
        await RunGit(_authorPath, $"{Identity} commit -m \"another task\"");
        await RunGit(_authorPath, "push origin main");

        var sync = await manager.SyncBaseBranchAsync(
            _orgId, _repoId, _taskId, "main", leaveConflictsInProgress: true);
        sync.IsSuccess.Should().BeTrue(sync.Error);

        (await GitOutput(worktree.Value!, "log -1 --format=%s")).Should().StartWith("Merge");
        (await GitOutput(worktree.Value!, "log -1 --format=%an|%ae|%cn|%ce")).Should().Be($"{developer}|{developer}");
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
        var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new Exception($"Git failed ({arguments}): {error}");
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
