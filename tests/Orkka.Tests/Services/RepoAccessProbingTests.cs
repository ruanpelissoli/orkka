namespace Orkka.Tests.Services;

using System.Diagnostics;
using FluentAssertions;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;

/// <summary>
/// The runner-side preflight, against real git.
///
/// Real git on purpose: the whole value of this probe is that it fails in exactly the way a
/// real clone would, and a fake would only prove that the fake agrees with itself. Local
/// paths keep it offline and deterministic — git treats a filesystem path as a remote, so
/// "reachable" and "not there" are both reproducible without a network or a credential.
/// </summary>
public sealed class RepoAccessProbingTests
{
    [Fact]
    public async Task ReachableRemote_ProbesOk()
    {
        using var remote = new TempBareRepo();

        var result = await RepoAccessProbing.ProbeAsync([new RepoAccessProbe("repo-1", remote.Path)]);

        var probed = result.Results.Should().ContainSingle().Subject;
        probed.RepoId.Should().Be("repo-1");
        probed.Ok.Should().BeTrue();
        probed.Error.Should().BeNull();
    }

    /// <summary>
    /// A brand-new repository has no refs, which is what makes `ls-remote --exit-code`
    /// the wrong tool: it reports 2 for "no matching refs" and the probe would call a
    /// perfectly reachable repo broken. Reachability is the question being asked.
    /// </summary>
    [Fact]
    public async Task EmptyButReachableRemote_IsNotReportedAsAFailure()
    {
        using var remote = new TempBareRepo();

        // TempBareRepo is `git init --bare` and nothing else — zero refs, by construction.
        var result = await RepoAccessProbing.ProbeAsync([new RepoAccessProbe("repo-1", remote.Path)]);

        result.Results.Single().Ok.Should().BeTrue("an empty repository is still readable");
    }

    [Fact]
    public async Task UnreachableRemote_ReportsGitsOwnError()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"orkka-not-a-repo-{Guid.NewGuid():N}");

        var result = await RepoAccessProbing.ProbeAsync([new RepoAccessProbe("repo-1", missing)]);

        var probed = result.Results.Should().ContainSingle().Subject;
        probed.Ok.Should().BeFalse();
        probed.Error.Should().NotBeNullOrWhiteSpace("the host classifies this text, so it must arrive");
    }

    /// <summary>Every repo gets an answer, and each answer is keyed back to the repo the
    /// host asked about — the host lines these up with its own rows by id.</summary>
    [Fact]
    public async Task EveryProbedRepoComesBackKeyedById()
    {
        using var reachable = new TempBareRepo();
        var missing = Path.Combine(Path.GetTempPath(), $"orkka-not-a-repo-{Guid.NewGuid():N}");

        var result = await RepoAccessProbing.ProbeAsync([
            new RepoAccessProbe("good", reachable.Path),
            new RepoAccessProbe("bad", missing),
        ]);

        result.Results.Select(r => r.RepoId).Should().Equal("good", "bad");
        result.Results.Single(r => r.RepoId == "good").Ok.Should().BeTrue();
        result.Results.Single(r => r.RepoId == "bad").Ok.Should().BeFalse();
    }

    /// <summary>A repo linked as a local folder has no remote and therefore no credential
    /// question. Calling it broken would invent a problem the user cannot fix.</summary>
    [Fact]
    public async Task RepoWithNoRemote_IsNotAFailure()
    {
        var result = await RepoAccessProbing.ProbeAsync([new RepoAccessProbe("local-folder", "")]);

        result.Results.Single().Ok.Should().BeTrue();
        result.Results.Single().Error.Should().BeNull();
    }

    /// <summary>One click must not become a minutes-long scan on a large project.</summary>
    [Fact]
    public async Task MoreReposThanTheCap_ProbesUpToTheCapOnly()
    {
        var probes = Enumerable.Range(0, RepoAccessProbing.MaxRepos + 5)
            .Select(i => new RepoAccessProbe($"repo-{i}", ""))
            .ToArray();

        var result = await RepoAccessProbing.ProbeAsync(probes);

        result.Results.Should().HaveCount(RepoAccessProbing.MaxRepos);
    }

    /// <summary>A remote is probed with the machine's own credentials — the ones every real
    /// run uses — so git really runs: 127.0.0.1:1 refuses the connection, and
    /// the answer is git's own, not a refusal invented before git ever ran.</summary>
    [Fact]
    public async Task HttpsRemote_IsProbedWithTheMachinesOwnCredentials()
    {
        var result = await RepoAccessProbing.ProbeAsync(
            [new RepoAccessProbe("repo-1", "https://127.0.0.1:1/alice/app.git")]);

        var probed = result.Results.Single();
        probed.Ok.Should().BeFalse();
        probed.Error.Should().NotStartWith(nameof(Orkka.Git.GitErrorCode.NoCredential));
    }

    /// <summary>
    /// The handoff that makes the preflight worth having: git's own text goes through the
    /// same classifier the failed-run path uses, so the advice a user gets BEFORE spending a
    /// run is the advice they would have got after — and that advice never sends anyone to
    /// configure git on their machine, because nothing there is consulted.
    /// </summary>
    [Fact]
    public void ProbeError_ClassifiesWithTheSameVocabularyAsAFailedRun()
    {
        const string refused =
            "fatal: could not read Username for 'https://github.com': terminal prompts disabled";

        RepoAccessDiagnostics.Classify(refused)
            .Should().Be(RepoAccessFailureReason.GitCredentials);
        var advice = RepoAccessDiagnostics.Explain(RepoAccessFailureReason.GitCredentials, onUserMachine: true);
        advice.Should().Contain("Sign git in on that machine",
            "the runner clones with the developer's own credentials, so the remedy is that machine's git");
    }

    /// <summary>A bare repo in a temp folder — a remote git can reach with no network,
    /// no credentials, and no commit identity configured.</summary>
    private sealed class TempBareRepo : IDisposable
    {
        public TempBareRepo()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"orkka-probe-{Guid.NewGuid():N}.git");
            Directory.CreateDirectory(Path);

            using var git = Process.Start(new ProcessStartInfo("git")
            {
                ArgumentList = { "init", "--bare", Path },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            git.WaitForExit(30_000);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* a leftover temp folder is not worth failing a test over */ }
        }
    }
}
