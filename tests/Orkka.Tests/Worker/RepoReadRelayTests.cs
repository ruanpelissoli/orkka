namespace Orkka.Tests.Worker;

using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.Runners.Server;
using Orkka.Runners.Server.Hosting;

/// <summary>
/// The Worker end of the local-folder read relay: each op is the runner's own browse op, and
/// the runner's result record travels back verbatim as JSON so the API reads the same wire
/// type. A runner that is offline or too old is "not supported", never an error.
/// </summary>
public sealed class RepoReadRelayTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string OrgId = Ulid.NewUlid().ToString();
    private static readonly string RepoId = Ulid.NewUlid().ToString();

    [Fact]
    public async Task ATreeRead_AsksTheRunnerForThatLevel_AndReturnsItsEntriesAsJson()
    {
        object? seen = null;
        var link = new FakeLink(online: true, capable: true, (method, args) =>
        {
            method.Should().Be(RunnerProtocol.ListTree);
            seen = args[0];
            return new RunnerTreeResult(true, [new RunnerTreeEntry("src", "src", true)], null);
        });

        var answer = await RepoReadRelay.ExecuteAsync(link,
            new RelayRepoReadRequest(OrgId, "user_1", RepoId, RepoReadRelay.Ops.Tree, Branch: "dev", Path: "src"),
            CancellationToken.None);

        answer.Supported.Should().BeTrue();
        answer.Success.Should().BeTrue();
        seen.Should().BeEquivalentTo(new ListTreeRequest(OrgId, RepoId, "dev", "src"));
        var entries = JsonSerializer.Deserialize<RunnerTreeResult>(answer.Value!, Json)!;
        entries.Entries.Should().ContainSingle(e => e.Name == "src" && e.IsDirectory);
    }

    [Fact]
    public async Task AFileRead_WithoutAPath_IsRefusedBeforeTheRunnerIsAsked()
    {
        var asked = false;
        var link = new FakeLink(online: true, capable: true, (_, _) => { asked = true; return RunnerOpResult.Ok(); });

        var answer = await RepoReadRelay.ExecuteAsync(link,
            new RelayRepoReadRequest(OrgId, "user_1", RepoId, RepoReadRelay.Ops.File), CancellationToken.None);

        answer.Should().BeEquivalentTo(new { Supported = true, Success = false });
        asked.Should().BeFalse();
    }

    [Fact]
    public async Task ARunnerWithoutLocalFolders_IsNotSupported()
    {
        var link = new FakeLink(online: true, capable: false, (_, _) => RunnerOpResult.Ok());

        var answer = await RepoReadRelay.ExecuteAsync(link,
            new RelayRepoReadRequest(OrgId, "user_1", RepoId, RepoReadRelay.Ops.Branches), CancellationToken.None);

        answer.Supported.Should().BeFalse();
    }

    [Fact]
    public async Task ARunnerThatDropsMidCall_IsNotSupported_NotAnException()
    {
        var link = new FakeLink(online: true, capable: true,
            (_, _) => throw new RunnerUnavailableException("Runner disconnected mid-call."));

        var answer = await RepoReadRelay.ExecuteAsync(link,
            new RelayRepoReadRequest(OrgId, "user_1", RepoId, RepoReadRelay.Ops.Commits), CancellationToken.None);

        answer.Supported.Should().BeFalse();
        answer.Error.Should().Contain("disconnected");
    }

    [Fact]
    public async Task AnUnknownOp_IsRefused()
    {
        var link = new FakeLink(online: true, capable: true, (_, _) => RunnerOpResult.Ok());

        var answer = await RepoReadRelay.ExecuteAsync(link,
            new RelayRepoReadRequest(OrgId, "user_1", RepoId, "rm-rf"), CancellationToken.None);

        answer.Supported.Should().BeTrue();
        answer.Success.Should().BeFalse();
        answer.Error.Should().Contain("rm-rf");
    }

    private sealed class FakeLink(bool online, bool capable, Func<string, object?[], object> answer) : IRunnerLink
    {
        public bool IsConnected => online;
        public (bool Connected, RunnerHello? Hello) Status => (online, new RunnerHello(
            ProtocolVersion: 3, MachineName: "laptop", ClaudeCliFound: true, ClaudeAuthenticated: true,
            ActiveRunIds: [], Providers: [],
            Capabilities: capable ? [RunnerCapabilities.LocalFolders, RunnerCapabilities.DeveloperGit] : []));
        public string? GetProviderBlocker(AiProvider provider) => null;
        public Task<T> InvokeAsync<T>(string method, object?[] args, CancellationToken ct, TimeSpan? opTimeout = null)
            => Task.FromResult((T)answer(method, args));
        public Task SendAsync(string method, object? arg, CancellationToken ct) => Task.CompletedTask;
        public Channel<RunnerChunk> OpenRunChannel(string runId) => throw new NotSupportedException();
        public void CloseRunChannel(string runId) { }
        public bool TryGetRunChannel(string runId, out Channel<RunnerChunk> channel) => throw new NotSupportedException();
        public IAsyncEnumerable<RunnerChunk> ConsumeRunAsync(string runId, CancellationToken ct) => throw new NotSupportedException();
    }
}
