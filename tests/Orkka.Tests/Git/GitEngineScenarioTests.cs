namespace Orkka.Tests.Git;

using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.Git;

/// <summary>
/// The engine's edges: what happens when a caller cancels, when a credential must never leak
/// into an error, when a host stamps a different topology, and the one ambient exception.
/// </summary>
public sealed class GitEngineScenarioTests
{
    private readonly GitProcess _git = new(NullLogger<GitProcess>.Instance);

    [Fact]
    public async Task ACancelledCaller_GetsOperationCanceled_NotAResult()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _git.RunAsync(new GitCommand(Path.GetTempPath(), "--version"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ATokenInTheRemoteUrl_NeverReachesTheReportedError()
    {
        // 127.0.0.1:1 refuses immediately; git echoes the URL in its failure, credential and all.
        var result = await _git.RunAsync(new GitCommand(Path.GetTempPath(),
            ["ls-remote", "https://x-access-token:ghs_leak@127.0.0.1:1/acme/app.git"],
            Timeout: TimeSpan.FromSeconds(30)), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().NotContain("ghs_leak");
        new GitCommand("x", "ls-remote", "https://x-access-token:ghs_leak@github.com/a/b.git")
            .Redacted().Should().NotContain("ghs_leak");
    }

    [Fact]
    public void AmbientCredentials_StayAvailable()
    {
        var ambient = new ProcessStartInfo("git");
        ambient.Environment.Remove("GIT_CONFIG_COUNT");
        GitProcess.ApplyEnvironment(ambient, new GitCommand("x", ["push", "origin", "main"]));
        ambient.Environment.TryGetValue("GIT_CONFIG_COUNT", out var count);
        // Windows adds core.longpaths to every command; what matters here is the helper.
        Enumerable.Range(0, int.Parse(count ?? "0"))
            .Select(i => ambient.Environment[$"GIT_CONFIG_KEY_{i}"])
            .Should().NotContain("credential.helper", "no helper is cleared and none is added");
    }

    [Fact]
    public void Describe_NamesTheCodeFirst_AndFallsBackToTheExitCode()
    {
        new GitCommandResult(128, "", "fatal: bad", GitErrorCode.RefNotFound).Describe()
            .Should().Be("RefNotFound: fatal: bad");
        new GitCommandResult(1, "", "", GitErrorCode.Unknown).Describe()
            .Should().Be("Unknown: git exited 1");
        new GitCommandResult(0, "out\n", "", GitErrorCode.None).Describe().Should().Be("ok");
    }

    [Fact]
    public void TopologyLedger_RecordsUnderTheHostsLedger_WithItsOwnTopology()
    {
        var inner = new RecordingLedger();
        var proxied = new TopologyLedger(inner, GitTopologies.UserRunner);

        using (var scope = GitOperationScope.Begin(proxied, GitOperationKind.Push, "org", "repo", "task"))
            scope.Complete();

        inner.Records.Single().Topology.Should().Be(GitTopologies.UserRunner);
        inner.Topology.Should().Be("test", "the inner ledger's own name is untouched");
    }

    [Fact]
    public void LedgerScope_KeepsAnEngineNamedCode_WhenTheErrorTravelledThroughAResult()
    {
        var ledger = new RecordingLedger();
        using (var scope = GitOperationScope.Begin(ledger, GitOperationKind.EnsureMirror))
            scope.Fail($"{GitErrorCode.NoCredential}: {GitErrorClassifier.Describe(GitErrorCode.NoCredential)}");

        ledger.Records.Single().Code.Should().Be(GitErrorCode.NoCredential);
    }

    [Fact]
    public void Classifier_TreatsRepositoryNotFound_AsACredentialProblemToo()
    {
        // GitHub answers 404 for a private repo the credential cannot see.
        GitErrorClassifier.IsCredentialProblem(GitErrorCode.RepositoryNotFound).Should().BeTrue();
        GitErrorClassifier.IsTransient(GitErrorCode.Authentication).Should().BeFalse();
    }

    private sealed class RecordingLedger : IGitOperationLedger
    {
        public List<GitOperationRecord> Records { get; } = [];
        public string Topology => "test";
        public void Record(GitOperationRecord record) => Records.Add(record);
    }
}
