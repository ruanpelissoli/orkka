namespace Orkka.Tests.Worker;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Orkka.Modules.Execution.Agents;
using Orkka.Modules.Execution.Git;

/// <summary>
/// The setup command is the only shell Orkka runs in a worktree that no agent asked for, so
/// the rules around when it re-runs and what it is allowed to do are the whole feature.
/// These drive the real GitWorkspaceManager against a temp directory.
/// </summary>
public sealed class SetupCommandTests : IDisposable
{
    private readonly string _basePath;
    private readonly GitWorkspaceManager _manager;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();
    private readonly Ulid _taskId = Ulid.NewUlid();

    public SetupCommandTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"orkka-setup-{Ulid.NewUlid()}");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = _basePath
            })
            .Build();

        _manager = new GitWorkspaceManager(config, Mock.Of<ILogger<GitWorkspaceManager>>());
        Directory.CreateDirectory(_manager.GetWorktreePath(_orgId, _repoId, _taskId));
    }

    public void Dispose()
    {
        try { Directory.Delete(_basePath, recursive: true); } catch { /* best effort */ }
    }

    private Task<Orkka.SharedKernel.Utilities.Result<
        Orkka.Modules.SourceControl.Contracts.SetupCommandOutcome>> RunAsync(
        string command)
        => _manager.RunSetupCommandAsync(_orgId, _repoId, _taskId, command);

    private static string Touch(string fileName) =>
        OperatingSystem.IsWindows() ? $"type nul > {fileName}" : $"touch {fileName}";

    private static string Fail() =>
        OperatingSystem.IsWindows() ? "exit /b 3" : "exit 3";

    private string MarkerPath =>
        Path.Combine(_manager.GetWorktreePath(_orgId, _repoId, _taskId), ".orkka", "setup");

    [Fact]
    public async Task Blank_command_is_a_skip_not_a_failure()
    {
        // A repo that needs no preparation has not failed to prepare.
        var result = await RunAsync("   ");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Skipped.Should().BeTrue();
        result.Value.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Successful_command_runs_and_records_a_marker()
    {
        var result = await RunAsync(Touch("prepared.txt"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Skipped.Should().BeFalse();
        result.Value.ExitCode.Should().Be(0);
        File.Exists(MarkerPath).Should().BeTrue();
    }

    [Fact]
    public async Task Second_run_of_the_same_command_is_skipped()
    {
        // A worktree is reused across planning → developing → testing; re-installing
        // dependencies once per phase would be pure waste.
        await RunAsync(Touch("first.txt"));
        var second = await RunAsync(Touch("first.txt"));

        second.Value!.Skipped.Should().BeTrue();
    }

    [Fact]
    public async Task Editing_the_command_makes_it_run_again()
    {
        await RunAsync(Touch("one.txt"));
        var afterEdit = await RunAsync(Touch("two.txt"));

        afterEdit.Value!.Skipped.Should().BeFalse();
        afterEdit.Value.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task A_failure_is_never_cached_as_done()
    {
        // Caching "it didn't work" would strand the worktree for the rest of the task.
        var first = await RunAsync(Fail());
        first.Value!.ExitCode.Should().Be(3);
        first.Value.Succeeded.Should().BeFalse();

        var second = await RunAsync(Fail());
        second.Value!.Skipped.Should().BeFalse();
    }

    [Fact]
    public async Task Failure_output_is_captured_for_diagnosis()
    {
        var result = await RunAsync("echo boom-detail && " + Fail());

        result.IsSuccess.Should().BeTrue();
        result.Value!.ExitCode.Should().Be(3);
        result.Value.Output.Should().Contain("boom-detail");
    }

    [Fact]
    public async Task The_users_command_runs_even_where_the_agent_guard_would_block_it()
    {
        // The user's own machine: blocking `cp ../.env .` there would defeat the feature.
        AgentBashGuard.IsBlockedCommand("cp ../.env .", null).Should().BeTrue();

        var result = await RunAsync("cp ../.env .");

        // It may well fail (no such file) — the point is that it RAN rather than being refused.
        result.IsSuccess.Should().BeTrue();
        result.Value!.Skipped.Should().BeFalse();
    }

    [Fact]
    public async Task Marker_lives_under_the_orkka_folder_that_commits_already_exclude()
    {
        await RunAsync(Touch("x.txt"));

        var worktree = _manager.GetWorktreePath(_orgId, _repoId, _taskId);
        Path.GetRelativePath(worktree, MarkerPath).Replace('\\', '/')
            .Should().Be(".orkka/setup");
    }

    [Fact]
    public async Task Missing_worktree_is_a_failure_not_a_silent_skip()
    {
        var result = await _manager.RunSetupCommandAsync(
            _orgId, _repoId, Ulid.NewUlid(), Touch("x.txt"));

        result.IsFailure.Should().BeTrue();
    }
}
