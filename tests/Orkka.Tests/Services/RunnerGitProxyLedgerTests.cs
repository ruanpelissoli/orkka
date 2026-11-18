namespace Orkka.Tests.Services;

using System.Threading.Channels;
using FluentAssertions;
using Orkka.Git;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.Runners.Server;

/// <summary>
/// A git operation proxied to a user's runner is recorded in the HOST's ledger, and the row
/// has to say which repository and task it was for — the first rows the dashboard showed for
/// user-runner ops read "repo=null task=null", which made a failing task impossible to find
/// from the git page. The ids come from the wire request itself.
/// </summary>
public sealed class RunnerGitProxyLedgerTests
{
    private readonly RecordingLedger _ledger = new();
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();
    private readonly Ulid _taskId = Ulid.NewUlid();

    [Fact]
    public async Task APushProxiedToTheRunner_IsRecordedWithItsRepoAndTask()
    {
        var link = new FakeLink((method, _) => method == RunnerProtocol.EnsurePushed
            ? RunnerOpResult.Ok()
            : RunnerOpResult.Fail("unexpected"));
        var proxy = new RunnerGitWorkspaceManager(link, ledger: _ledger);

        var result = await proxy.EnsurePushedAsync(_orgId, _repoId, _taskId, "orkka/T-1");

        result.IsSuccess.Should().BeTrue(result.Error);
        var row = _ledger.Records.Single();
        row.Kind.Should().Be(GitOperationKind.Push);
        row.Succeeded.Should().BeTrue();
        row.OrgId.Should().Be(_orgId.ToString());
        row.RepoId.Should().Be(_repoId.ToString());
        row.TaskId.Should().Be(_taskId.ToString());
    }

    [Fact]
    public async Task AFailedSyncOnTheRunner_IsRecordedWithItsIds_AndTheRunnersCode()
    {
        var link = new FakeLink((_, _) => RunnerOpResult.Fail(
            $"{GitErrorCode.DirtyWorkingTree}: The worktree has uncommitted changes."));
        var proxy = new RunnerGitWorkspaceManager(link, ledger: _ledger);

        var result = await proxy.SyncBaseBranchAsync(_orgId, _repoId, _taskId, "main", leaveConflictsInProgress: false);

        result.IsFailure.Should().BeTrue();
        var row = _ledger.Records.Single();
        row.Kind.Should().Be(GitOperationKind.SyncBase);
        row.Code.Should().Be(GitErrorCode.DirtyWorkingTree);
        row.RepoId.Should().Be(_repoId.ToString());
        row.TaskId.Should().Be(_taskId.ToString());
    }

    [Fact]
    public async Task ACommitAndPushByWorktreeToken_ResolvesTheIdsFromTheToken()
    {
        var link = new FakeLink((_, _) => RunnerOpResult.Ok());
        var proxy = new RunnerGitWorkspaceManager(link, ledger: _ledger);
        var token = RunnerTokens.Worktree(_orgId.ToString(), _repoId.ToString(), _taskId.ToString());

        await proxy.CommitAndPushAsync(token, "msg", "orkka/T-1");

        var row = _ledger.Records.Single();
        row.Kind.Should().Be(GitOperationKind.Publish);
        row.OrgId.Should().Be(_orgId.ToString());
        row.TaskId.Should().Be(_taskId.ToString());
    }

    [Fact]
    public async Task AMirrorOp_HasARepoButNoTask()
    {
        var link = new FakeLink((_, _) => RunnerOpResult.Ok("path"));
        var proxy = new RunnerGitWorkspaceManager(link, ledger: _ledger);

        await proxy.EnsureBareCloneAsync(_orgId, _repoId, "https://github.com/acme/app.git");

        var row = _ledger.Records.Single();
        row.Kind.Should().Be(GitOperationKind.EnsureMirror);
        row.RepoId.Should().Be(_repoId.ToString());
        row.TaskId.Should().BeNull();
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private sealed class RecordingLedger : IGitOperationLedger
    {
        public List<GitOperationRecord> Records { get; } = [];
        public string Topology => GitTopologies.UserRunner;
        public void Record(GitOperationRecord record) => Records.Add(record);
    }

    /// <summary>A connected, engine-capable runner that answers every op through one delegate.</summary>
    private sealed class FakeLink(Func<string, object?[], RunnerOpResult> answer) : IRunnerLink
    {
        public bool IsConnected => true;
        public (bool Connected, RunnerHello? Hello) Status => (true, new RunnerHello(
            ProtocolVersion: 3, MachineName: "laptop", ClaudeCliFound: true, ClaudeAuthenticated: true,
            ActiveRunIds: [], Providers: [], Capabilities: [RunnerCapabilities.DeveloperGit]));
        public string? GetProviderBlocker(AiProvider provider) => null;
        public Task<T> InvokeAsync<T>(string method, object?[] args, CancellationToken ct, TimeSpan? opTimeout = null)
            => Task.FromResult((T)(object)answer(method, args));
        public Task SendAsync(string method, object? arg, CancellationToken ct) => Task.CompletedTask;
        public Channel<RunnerChunk> OpenRunChannel(string runId) => throw new NotSupportedException();
        public void CloseRunChannel(string runId) { }
        public bool TryGetRunChannel(string runId, out Channel<RunnerChunk> channel) => throw new NotSupportedException();
        public IAsyncEnumerable<RunnerChunk> ConsumeRunAsync(string runId, CancellationToken ct) => throw new NotSupportedException();
    }
}
