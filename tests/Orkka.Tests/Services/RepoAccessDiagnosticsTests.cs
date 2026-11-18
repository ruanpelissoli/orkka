namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.Modules.Agents.Contracts;

/// <summary>
/// The words a user reads when their codebase is missing from a chat turn, and the
/// classification that picks them.
///
/// Pinned because the failure this exists for is invisible by construction: the repo is
/// correctly dropped from the prompt, the agent then truthfully says it cannot find the
/// code, and nothing tells the human that git on their own machine was never signed in.
/// A notice that says "some repos are unavailable", or that pastes git's stderr, closes
/// the log gap without closing the understanding gap.
/// </summary>
public sealed class RepoAccessDiagnosticsTests
{
    /// <summary>The exact error seen in production: the runner clones under the user's own
    /// git credentials, git there runs with prompts disabled, and a machine with no
    /// credential helper fails every HTTPS clone of a private repo this way.</summary>
    private const string ProductionCloneError =
        "Git command failed (exit 128): Cloning into bare repository 'bare'...\n"
        + "fatal: Cannot prompt because user interactivity has been disabled.\n"
        + "fatal: could not read Username for 'https://github.com': terminal prompts disabled";

    [Fact]
    public void Classify_TheProductionCloneFailure_IsACredentialProblem()
        => RepoAccessDiagnostics.Classify(ProductionCloneError)
            .Should().Be(RepoAccessFailureReason.GitCredentials);

    [Theory]
    [InlineData("fatal: Authentication failed for 'https://github.com/acme/api.git/'")]
    [InlineData("remote: Invalid username or password.")]
    [InlineData("git@github.com: Permission denied (publickey).")]
    [InlineData("remote: Support for password authentication was removed on August 13, 2021.")]
    [InlineData("remote: HTTP Basic: Access denied")]
    public void Classify_AuthFailures_AreCredentialProblems(string error)
        => RepoAccessDiagnostics.Classify(error)
            .Should().Be(RepoAccessFailureReason.GitCredentials);

    [Fact]
    public void Classify_RepositoryNotFound_IsNotFound()
        => RepoAccessDiagnostics.Classify("remote: Repository not found.")
            .Should().Be(RepoAccessFailureReason.RepoNotFound);

    /// <summary>
    /// GitHub answers a private repo the caller cannot see with "Repository not found" as
    /// well, and its own auth line rides in the same output. Reading that as "deleted"
    /// sends the user to check their repo URL when the fix is to sign in.
    /// </summary>
    [Fact]
    public void Classify_AuthAndNotFoundTogether_PrefersTheCredentialReading()
    {
        var error = "remote: Repository not found.\nfatal: Authentication failed for 'https://github.com/acme/api.git/'";

        RepoAccessDiagnostics.Classify(error)
            .Should().Be(RepoAccessFailureReason.GitCredentials);
    }

    [Fact]
    public void Classify_UnrecognisedError_IsUnknown()
        => RepoAccessDiagnostics.Classify("error: RPC failed; curl 92 HTTP/2 stream 5 was not closed cleanly")
            .Should().Be(RepoAccessFailureReason.Unknown);

    [Fact]
    public void Classify_NoError_IsUnknown()
        => RepoAccessDiagnostics.Classify(null).Should().Be(RepoAccessFailureReason.Unknown);

    /// <summary>
    /// Only the reasons the user can act on are worth suppressing retries for. An offline
    /// runner or an unclassified transport error clears by itself, and holding a repo out
    /// after it started working again would be a worse bug than the retry storm.
    /// </summary>
    [Theory]
    [InlineData(RepoAccessFailureReason.GitCredentials, true)]
    [InlineData(RepoAccessFailureReason.RepoNotFound, true)]
    [InlineData(RepoAccessFailureReason.RunnerUnavailable, false)]
    [InlineData(RepoAccessFailureReason.NotCloned, false)]
    [InlineData(RepoAccessFailureReason.Unknown, false)]
    public void IsPermanent_MarksOnlyWhatTheUserMustFix(RepoAccessFailureReason reason, bool expected)
        => RepoAccessDiagnostics.IsPermanent(reason).Should().Be(expected);

    // ---------------------------------------------------------------- whose git failed

    /// <summary>
    /// The advice inverts with the machine. A UserRunner task (and every runner-routed chat
    /// turn) clones under the customer's own git credentials, so they fix it locally; a Cloud
    /// task clones under Orkka's, where telling them to run `gh auth login` sends them to fix
    /// something that had nothing to do with the failure.
    /// </summary>
    [Fact]
    public void Explain_CredentialFailure_TellsTheRightPersonToFixIt()
    {
        var onTheirMachine = RepoAccessDiagnostics.Explain(
            RepoAccessFailureReason.GitCredentials, onUserMachine: true);
        var inTheCloud = RepoAccessDiagnostics.Explain(
            RepoAccessFailureReason.GitCredentials, onUserMachine: false);

        onTheirMachine.Should().Contain("refused your git credentials").And.Contain("gh auth login");
        inTheCloud.Should().NotContain("gh auth login", "nothing they sign into fixes a clone Orkka has no credential for");
        inTheCloud.Should().Contain("./orkka start");
    }

    /// <summary>Two repos that both failed to authenticate but on different machines need
    /// opposite advice, so they must not be collapsed into one bullet.</summary>
    [Fact]
    public void BuildNotice_SameReasonDifferentMachines_DoesNotShareAdvice()
    {
        var notice = RepoAccessDiagnostics.BuildNotice([
            new UnavailableChatRepo(
                Ulid.NewUlid(), "on-my-pc", RepoAccessFailureReason.GitCredentials, null, OnUserMachine: true),
            new UnavailableChatRepo(
                Ulid.NewUlid(), "in-the-cloud", RepoAccessFailureReason.GitCredentials, null, OnUserMachine: false),
        ]);

        notice.Should().NotBeNull();
        // Each name sits with the advice that applies to it.
        var mine = notice!.IndexOf("on-my-pc", StringComparison.Ordinal);
        var theirs = notice.IndexOf("in-the-cloud", StringComparison.Ordinal);
        var runner = notice.IndexOf("your runner", StringComparison.Ordinal);
        var cloud = notice.IndexOf("Orkka has no credential", StringComparison.Ordinal);

        mine.Should().BeLessThan(runner);
        runner.Should().BeLessThan(theirs, "the local repo's advice must end before the cloud repo starts");
        theirs.Should().BeLessThan(cloud);
    }

    // ---------------------------------------------------------------- task-run wording

    /// <summary>
    /// Task runs on a user runner drop the platform token for unmanaged repos exactly as chat
    /// does, so the same machine fails their tasks too — and the task thread used to show raw
    /// git stderr. One line, naming the repo, because it lands next to the phase that stalled.
    /// </summary>
    [Fact]
    public void DescribeCloneFailure_OnAUserRunner_ReadsAsCredentialAdviceNotGitStderr()
    {
        var described = RepoAccessDiagnostics.DescribeCloneFailure(
            "esfer-api", ProductionCloneError, onUserMachine: true);

        described.Should().StartWith("Could not open esfer-api:");
        described.Should().Contain("refused your git credentials");
        described.Should().NotContain("terminal prompts disabled");
        described.Should().NotContain("exit 128");
    }

    [Fact]
    public void DescribeCloneFailure_InTheCloud_DoesNotBlameTheUsersMachine()
    {
        var described = RepoAccessDiagnostics.DescribeCloneFailure(
            "esfer-api", "fatal: Authentication failed", onUserMachine: false);

        described.Should().Contain("./orkka start");
        described.Should().NotContain("gh auth login");
    }

    /// <summary>An unclassified failure has no advice to give, so the raw first line is the
    /// most useful thing available — dropping it would leave the reader with nothing.</summary>
    [Fact]
    public void DescribeCloneFailure_UnknownError_KeepsOneLineOfTheOriginal()
    {
        var described = RepoAccessDiagnostics.DescribeCloneFailure(
            "esfer-api", "error: RPC failed; curl 92\nhint: try again", onUserMachine: true);

        described.Should().Contain("error: RPC failed; curl 92");
        described.Should().NotContain("hint: try again");
    }

    [Fact]
    public void BuildNotice_NothingMissing_IsSilent()
        => RepoAccessDiagnostics.BuildNotice([]).Should().BeNull();

    /// <summary>
    /// The whole point of the notice: name the repos, name the fix. A user who is told
    /// "some repos could not be loaded" learns nothing they can act on.
    /// </summary>
    [Fact]
    public void BuildNotice_CredentialFailure_NamesTheReposAndTheFix()
    {
        var notice = RepoAccessDiagnostics.BuildNotice([
            new UnavailableChatRepo(
                Ulid.NewUlid(), "esfer-api", RepoAccessFailureReason.GitCredentials,
                ProductionCloneError, OnUserMachine: true),
            new UnavailableChatRepo(
                Ulid.NewUlid(), "esfer-web", RepoAccessFailureReason.GitCredentials,
                ProductionCloneError, OnUserMachine: true),
        ]);

        notice.Should().NotBeNull();
        notice.Should().Contain("esfer-api").And.Contain("esfer-web");
        notice.Should().Contain("refused your git credentials", "the user has to know it is a credential problem");
        notice.Should().Contain("Sign git in on that machine", "an actionable notice names a way to fix it");
    }

    /// <summary>
    /// Raw git stderr describes our subprocess flags ("terminal prompts disabled"), not the
    /// user's problem. It stays in the logs, where it belongs.
    /// </summary>
    [Fact]
    public void BuildNotice_CredentialFailure_DoesNotDumpGitStderr()
    {
        var notice = RepoAccessDiagnostics.BuildNotice([
            new UnavailableChatRepo(
                Ulid.NewUlid(), "esfer-api", RepoAccessFailureReason.GitCredentials,
                ProductionCloneError, OnUserMachine: true),
        ]);

        notice.Should().NotContain("terminal prompts disabled");
        notice.Should().NotContain("exit 128");
    }

    /// <summary>An error we could not classify has no better explanation to offer, so the
    /// first line of it is worth showing rather than nothing at all.</summary>
    [Fact]
    public void BuildNotice_UnknownFailure_ShowsOneLineOfTheRawError()
    {
        var notice = RepoAccessDiagnostics.BuildNotice([
            new UnavailableChatRepo(
                Ulid.NewUlid(), "esfer-api", RepoAccessFailureReason.Unknown,
                "error: RPC failed; curl 92\nhint: try again later"),
        ]);

        notice.Should().Contain("error: RPC failed; curl 92");
        notice.Should().NotContain("hint: try again later", "one line is the signal, the rest is a log dump");
    }

    /// <summary>Repos that failed for different reasons need different advice — grouping
    /// them under one explanation would make at least one of them wrong.</summary>
    [Fact]
    public void BuildNotice_MixedReasons_ExplainsEachGroup()
    {
        var notice = RepoAccessDiagnostics.BuildNotice([
            new UnavailableChatRepo(
                Ulid.NewUlid(), "esfer-api", RepoAccessFailureReason.GitCredentials, null, OnUserMachine: true),
            new UnavailableChatRepo(Ulid.NewUlid(), "old-repo", RepoAccessFailureReason.RepoNotFound, null),
        ]);

        notice.Should().Contain("esfer-api").And.Contain("refused your git credentials");
        notice.Should().Contain("old-repo").And.Contain("no such repository");
    }
}
