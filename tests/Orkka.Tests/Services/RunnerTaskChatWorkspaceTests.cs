namespace Orkka.Tests.Services;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Execution.Agents;
using Orkka.Modules.SourceControl.Contracts;

/// <summary>
/// A task's Discuss workspace on the runner (P8): its own folder, each repo detached at the task
/// branch when that branch exists on the remote (else the default branch), and never the
/// project chat's workspace or the task's running worktree. Real git, temp repos.
/// </summary>
[Collection(Orkka.Tests.GitEngineStaticsCollection.Name)]
public sealed class RunnerTaskChatWorkspaceTests : IDisposable
{
    private readonly string _basePath;
    private readonly string _remotePath;
    private readonly string _seedPath;
    private readonly string _barePath;
    private readonly string _taskWorktree;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _projectId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();
    private readonly Ulid _taskId = Ulid.NewUlid();
    private readonly RunnerOperations _ops;

    public RunnerTaskChatWorkspaceTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"orkka-task-chat-tests-{Ulid.NewUlid()}");
        _remotePath = Path.Combine(_basePath, "remote.git");
        _seedPath = Path.Combine(_basePath, "seed");
        _barePath = Path.Combine(_basePath, "cache", "bare");
        _taskWorktree = Path.Combine(_basePath, "cache", "worktrees", "task");

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

    /// <summary>A remote with main + a pushed task branch, the runner's mirror of it, and a task
    /// worktree (a clone on the task branch with uncommitted work).</summary>
    private async Task SetupAsync(bool pushTaskBranch = true)
    {
        Directory.CreateDirectory(_remotePath);
        await Git(_remotePath, "init --bare");

        Directory.CreateDirectory(_seedPath);
        await Git(_seedPath, "init");
        await Git(_seedPath, "checkout -b main");
        await File.WriteAllTextAsync(Path.Combine(_seedPath, "README.md"), "main");
        await Git(_seedPath, "add -A");
        await Git(_seedPath, "-c user.name=Orkka -c user.email=orkka@localhost commit -m init");
        await Git(_seedPath, $"remote add origin \"{_remotePath}\"");
        await Git(_seedPath, "push -u origin main");

        if (pushTaskBranch)
        {
            await Git(_seedPath, "checkout -b feature/SHOP-12-export");
            await File.WriteAllTextAsync(Path.Combine(_seedPath, "export.txt"), "task work");
            await Git(_seedPath, "add -A");
            await Git(_seedPath, "-c user.name=Orkka -c user.email=orkka@localhost commit -m task");
            await Git(_seedPath, "push origin feature/SHOP-12-export");
            await Git(_seedPath, "checkout main");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_barePath)!);
        await Git(Path.GetDirectoryName(_barePath)!, $"clone --bare \"{_remotePath}\" bare");
        await Git(_barePath, "config remote.origin.fetch \"+refs/heads/*:refs/remotes/origin/*\"");
        await Git(_barePath, "fetch origin");

        // The task's own running worktree: on the task branch with unsaved work in it.
        Directory.CreateDirectory(Path.GetDirectoryName(_taskWorktree)!);
        await Git(Path.GetDirectoryName(_taskWorktree)!, $"clone \"{_remotePath}\" task");
        await File.WriteAllTextAsync(Path.Combine(_taskWorktree, "wip.txt"), "uncommitted");
    }

    private EnsureChatWorkspaceRequest Request(string? branch, bool task = true) => new(
        _orgId.ToString(),
        [new ChatWorkspaceRepoDto(_repoId.ToString(), "shop-api", "main", branch)],
        _projectId.ToString(),
        task ? _taskId.ToString() : null);

    [Fact]
    public async Task TaskChat_ChecksOutTheTaskBranch_InItsOwnWorkspace()
    {
        await SetupAsync();

        var result = await _ops.EnsureChatWorkspaceAsync(Request("feature/SHOP-12-export"));

        result.Success.Should().BeTrue(result.Error);
        result.Value.Should().Be(RunnerTokens.ChatWorkspace(_orgId.ToString(), _projectId.ToString(), _taskId.ToString()));
        var path = _ops.ChatWorkspacePath(_orgId.ToString(), _projectId.ToString(), _taskId.ToString());
        File.ReadAllText(Path.Combine(path, "shop-api", "export.txt")).Should().Be("task work");

        // Not inside the project chat's workspace (that root is the project agent's Cwd).
        var projectWorkspace = _ops.ChatWorkspacePath(_orgId.ToString(), _projectId.ToString());
        Path.GetFullPath(path).Should().NotStartWith(Path.GetFullPath(projectWorkspace));
    }

    [Fact]
    public async Task TaskChat_FallsBackToTheDefaultBranch_WhenTheTaskBranchWasNeverPushed()
    {
        await SetupAsync(pushTaskBranch: false);

        var result = await _ops.EnsureChatWorkspaceAsync(Request("feature/SHOP-12-export"));

        result.Success.Should().BeTrue(result.Error);
        var path = _ops.ChatWorkspacePath(_orgId.ToString(), _projectId.ToString(), _taskId.ToString());
        File.Exists(Path.Combine(path, "shop-api", "README.md")).Should().BeTrue();
        File.Exists(Path.Combine(path, "shop-api", "export.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task ProjectChat_IgnoresTheBranch_AndStaysOnTheDefault()
    {
        await SetupAsync();

        var result = await _ops.EnsureChatWorkspaceAsync(Request("feature/SHOP-12-export", task: false));

        result.Success.Should().BeTrue(result.Error);
        result.Value.Should().Be(RunnerTokens.ChatWorkspace(_orgId.ToString(), _projectId.ToString()));
        var path = _ops.ChatWorkspacePath(_orgId.ToString(), _projectId.ToString());
        File.Exists(Path.Combine(path, "shop-api", "export.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task TaskChat_NeverTouchesTheTasksRunningWorktree()
    {
        await SetupAsync();
        var headBefore = await GitOutput(_taskWorktree, "rev-parse HEAD");

        await _ops.EnsureChatWorkspaceAsync(Request("feature/SHOP-12-export"));
        await _ops.EnsureChatWorkspaceAsync(Request("feature/SHOP-12-export")); // second turn: reset path

        File.ReadAllText(Path.Combine(_taskWorktree, "wip.txt")).Should().Be("uncommitted");
        (await GitOutput(_taskWorktree, "rev-parse HEAD")).Should().Be(headBefore);
    }

    [Fact]
    public async Task TaskChat_FollowsTheBranch_AsItMovesBetweenTurns()
    {
        await SetupAsync(pushTaskBranch: false);
        await _ops.EnsureChatWorkspaceAsync(Request("feature/SHOP-12-export"));

        // The dev run pushes the branch; the next turn reads it.
        await Git(_seedPath, "checkout -b feature/SHOP-12-export");
        await File.WriteAllTextAsync(Path.Combine(_seedPath, "export.txt"), "pushed later");
        await Git(_seedPath, "add -A");
        await Git(_seedPath, "-c user.name=Orkka -c user.email=orkka@localhost commit -m later");
        await Git(_seedPath, "push origin feature/SHOP-12-export");
        await Git(_barePath, "fetch origin");

        await _ops.EnsureChatWorkspaceAsync(Request("feature/SHOP-12-export"));

        var path = _ops.ChatWorkspacePath(_orgId.ToString(), _projectId.ToString(), _taskId.ToString());
        File.ReadAllText(Path.Combine(path, "shop-api", "export.txt")).Should().Be("pushed later");
    }

    [Fact]
    public async Task Cleanup_RemovesTheTasksDiscussWorkspace()
    {
        await SetupAsync();
        await _ops.EnsureChatWorkspaceAsync(Request("feature/SHOP-12-export"));
        var path = _ops.ChatWorkspacePath(_orgId.ToString(), _projectId.ToString(), _taskId.ToString());

        await _ops.RemoveTaskChatWorkspaceAsync(new CleanupWorktreeRequest(
            _orgId.ToString(), _repoId.ToString(), _taskId.ToString()));

        Directory.Exists(path).Should().BeFalse();
        (await GitOutput(_barePath, "worktree list")).Should().NotContain("task-chat-workspace");
    }

    [Theory]
    [InlineData("feature/SHOP-12-export", true)]
    [InlineData("orkka/OA-3", true)]
    [InlineData("-x", false)]
    [InlineData("a..b", false)]
    [InlineData("a b", false)]
    [InlineData("x\" && rm -rf /", false)]
    [InlineData("refs.lock", false)]
    public void BranchNames_AreVettedBeforeReachingGit(string branch, bool safe)
        => RunnerOperations.IsSafeBranchName(branch).Should().Be(safe);

    [Fact]
    public void Tokens_RoundTripTheTask()
    {
        var token = RunnerTokens.ChatWorkspace("org", "proj", "task");

        RunnerTokens.TryParseChatWorkspace(token, out var org, out var project, out var task).Should().BeTrue();
        (org, project, task).Should().Be(("org", "proj", "task"));
        // A task needs a project.
        RunnerTokens.ChatWorkspace("org", null, "task").Should().Be("runner-chat:org");
    }

    private static async Task Git(string cwd, string args)
    {
        var (code, _, err) = await RunGit(cwd, args);
        if (code != 0)
            throw new InvalidOperationException($"git {args} failed: {err}");
    }

    private static async Task<string> GitOutput(string cwd, string args) => (await RunGit(cwd, args)).Out.Trim();

    private static async Task<(int Code, string Out, string Err)> RunGit(string cwd, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }
}
