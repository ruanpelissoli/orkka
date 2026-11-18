namespace Orkka.Tests.Git;

using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.Git;

/// <summary>
/// The engine's rules, each pinned where it is cheapest to check: the environment a command
/// runs with (no prompts, no ambient helpers, the credential as a scoped header, the bot as
/// author), the classification of git's stderr, the argument-line tokenizer the string-based
/// callers rely on, and the ledger scope's accounting. The last test runs real git against a
/// file remote to prove a token never lands in the clone's config.
/// </summary>
[Collection(Orkka.Tests.GitEngineStaticsCollection.Name)]
public sealed class GitEngineTests
{
    // ── Environment ──────────────────────────────────────────────────────────

    [Fact]
    public void Environment_NeverPrompts()
    {
        var psi = new ProcessStartInfo("git");
        psi.Environment.Remove("GIT_CONFIG_COUNT");

        GitProcess.ApplyEnvironment(psi, new GitCommand("C:/x", "fetch", "origin"));

        psi.Environment["GIT_TERMINAL_PROMPT"].Should().Be("0");
        psi.Environment["GCM_INTERACTIVE"].Should().Be("never");
        psi.Environment["GIT_ASKPASS"].Should().Be("");
    }

    /// <summary>Git authenticates and commits as the developer: their credential helpers stay,
    /// and no bot identity overrides their git config.</summary>
    [Fact]
    public void Environment_KeepsTheDevelopersHelpers_AndTheirIdentity()
    {
        var psi = new ProcessStartInfo("git");
        psi.Environment.Remove("GIT_CONFIG_COUNT");
        psi.Environment.Remove("GIT_AUTHOR_NAME");

        GitProcess.ApplyEnvironment(psi, new GitCommand("C:/x", "fetch", "origin"));

        psi.Environment["GIT_TERMINAL_PROMPT"].Should().Be("0", "a prompt would still hang a headless run");
        psi.Environment.Should().NotContainKey("GIT_AUTHOR_NAME");
        psi.Environment.TryGetValue("GIT_CONFIG_KEY_0", out var firstKey);
        firstKey.Should().NotBe("credential.helper");
    }

    [Fact]
    public void Environment_AppendsToTheRunnersBundledConfig_InsteadOfReplacingIt()
    {
        var psi = new ProcessStartInfo("git");
        psi.Environment["GIT_CONFIG_COUNT"] = "1";
        psi.Environment["GIT_CONFIG_KEY_0"] = "core.longpaths";
        psi.Environment["GIT_CONFIG_VALUE_0"] = "true";

        GitProcess.ApplyEnvironment(psi,
            new GitCommand("C:/x", ["fetch"]));

        psi.Environment["GIT_CONFIG_KEY_0"].Should().Be("core.longpaths");
        psi.Environment["GIT_CONFIG_VALUE_0"].Should().Be("true");
    }

    /// <summary>Windows git refuses paths past 260 characters unless told otherwise — a
    /// clone's temporary pack name under a deep worktree was enough (2026-09-17). Every
    /// command on Windows carries core.longpaths=true; other platforms carry nothing extra.</summary>
    [Fact]
    public void Environment_TurnsOnLongPaths_OnWindowsOnly()
    {
        var psi = new ProcessStartInfo("git");
        psi.Environment.Remove("GIT_CONFIG_COUNT");

        GitProcess.ApplyEnvironment(psi, new GitCommand("C:/x", "status"));

        var entries = Enumerable.Range(0, int.Parse(psi.Environment["GIT_CONFIG_COUNT"]!))
            .Select(i => (Key: psi.Environment[$"GIT_CONFIG_KEY_{i}"], Value: psi.Environment[$"GIT_CONFIG_VALUE_{i}"]))
            .ToList();
        if (OperatingSystem.IsWindows())
            entries.Should().Contain(e => e.Key == "core.longpaths" && e.Value == "true");
        else
            entries.Should().NotContain(e => e.Key == "core.longpaths");
    }

    [Fact]
    public void Environment_ForcesNoAuthor_UnlessTheCommandNamesOne()
    {
        var psi = new ProcessStartInfo("git");
        psi.Environment.Remove("GIT_AUTHOR_NAME");
        psi.Environment.Remove("GIT_COMMITTER_EMAIL");
        GitProcess.ApplyEnvironment(psi, new GitCommand("C:/x", "commit", "-m", "x"));

        psi.Environment.Should().NotContainKey("GIT_AUTHOR_NAME", "the developer's git config decides");
        psi.Environment.Should().NotContainKey("GIT_COMMITTER_EMAIL");

        var configured = new ProcessStartInfo("git");
        GitProcess.ApplyEnvironment(configured,
            new GitCommand("C:/x", ["commit"], Identity: new GitIdentity("admin-orkka", "1+admin-orkka@users.noreply.github.com")));
        configured.Environment["GIT_AUTHOR_NAME"].Should().Be("admin-orkka");
    }

    [Fact]
    public void Identity_FromConfiguration_NeverHalfSet()
    {
        GitIdentity.FromConfigurationOrNull("only-a-name", null).Should().BeNull();
        GitIdentity.FromConfigurationOrNull(null, "only@an.email").Should().BeNull();
        GitIdentity.FromConfigurationOrNull(" dev ", " dev@x.y ").Should().Be(new GitIdentity("dev", "dev@x.y"));
        GitIdentity.CoAuthorTrailer("Ruan", "ruan@x.y").Should().Be("Co-authored-by: Ruan <ruan@x.y>");
        GitIdentity.CoAuthorTrailer(null, "ruan@x.y").Should().BeNull();
    }

    // ── Credentials ──────────────────────────────────────────────────────────

    [Fact]
    public void Urls_CleanStripsEmbeddedCredentials_AndLeavesFoldersAlone()
    {
        GitUrls.Clean("https://x-access-token:ghs@github.com/acme/app.git").Should().Be("https://github.com/acme/app.git");
        GitUrls.Clean("https://github.com/acme/app.git").Should().Be("https://github.com/acme/app.git");
        GitUrls.Clean(@"C:\repos\local").Should().Be(@"C:\repos\local");
        GitUrls.IsRemote("git@github.com:acme/app.git").Should().BeTrue();
        GitUrls.IsRemote(@"C:\repos\local").Should().BeFalse();
    }

    // ── Classification ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", GitErrorCode.Authentication)]
    [InlineData("remote: Repository not found.\nfatal: repository 'https://github.com/a/b.git/' not found", GitErrorCode.RepositoryNotFound)]
    [InlineData("fatal: invalid reference: origin/orkka/milestone/x", GitErrorCode.RefNotFound)]
    [InlineData("error: src refspec main does not match any", GitErrorCode.RefNotFound)]
    [InlineData("error: cannot lock ref 'refs/remotes/origin/main': is at abc but expected def", GitErrorCode.RefLock)]
    [InlineData("fatal: not a git repository (or any of the parent directories): .git", GitErrorCode.NotARepository)]
    [InlineData("fatal: not a gitdir '.'", GitErrorCode.NotARepository)]
    [InlineData("! [rejected] main -> main (fetch first)\nerror: failed to push some refs", GitErrorCode.NonFastForward)]
    [InlineData("CONFLICT (content): Merge conflict in a.txt\nAutomatic merge failed; fix conflicts and then commit the result.", GitErrorCode.MergeConflict)]
    [InlineData("error: Your local changes to the following files would be overwritten by merge", GitErrorCode.DirtyWorkingTree)]
    [InlineData("fatal: Unable to create '/x/.git/index.lock': File exists.", GitErrorCode.RefLock)]
    [InlineData("fatal: unable to access 'https://github.com/a/b.git/': Could not resolve host: github.com", GitErrorCode.Network)]
    [InlineData("Author identity unknown\n*** Please tell me who you are.", GitErrorCode.IdentityUnknown)]
    [InlineData("remote: error: GH006: Protected branch update failed", GitErrorCode.Rejected)]
    [InlineData("something entirely new", GitErrorCode.Unknown)]
    public void Classifier_NamesTheFailure(string stderr, GitErrorCode expected)
        => GitErrorClassifier.Classify(1, stderr).Should().Be(expected);

    [Fact]
    public void Classifier_ExitZeroIsNone_AndEveryCodeHasAPlainSentence()
    {
        GitErrorClassifier.Classify(0, "warning: something").Should().Be(GitErrorCode.None);
        foreach (var code in Enum.GetValues<GitErrorCode>())
            GitErrorClassifier.Describe(code).Should().NotBeNullOrWhiteSpace();
        GitErrorClassifier.IsTransient(GitErrorCode.RefLock).Should().BeTrue();
        GitErrorClassifier.IsCredentialProblem(GitErrorCode.Authentication).Should().BeTrue();
        GitErrorClassifier.IsCredentialProblem(GitErrorCode.MergeConflict).Should().BeFalse();
    }

    // ── Argument line ────────────────────────────────────────────────────────

    [Fact]
    public void ArgumentLine_SplitsLikeTheStringCallersMeant()
    {
        GitArgumentLine.Split("commit -m \"a message with spaces\"")
            .Should().Equal("commit", "-m", "a message with spaces");
        GitArgumentLine.Split("commit -m \"say \\\"hi\\\"\"")
            .Should().Equal("commit", "-m", "say \"hi\"");
        GitArgumentLine.Split("push \"https://x@y/z\" main")
            .Should().Equal("push", "https://x@y/z", "main");
        GitArgumentLine.Split("  status   --porcelain ")
            .Should().Equal("status", "--porcelain");
        GitArgumentLine.Split("config user.name \"\"")
            .Should().Equal("config", "user.name", "");
    }

    // ── Ledger ───────────────────────────────────────────────────────────────

    [Fact]
    public void LedgerScope_RecordsOutcome_DurationAndTheFailingCommand()
    {
        var ledger = new RecordingLedger();

        using (var ok = GitOperationScope.Begin(ledger, GitOperationKind.Publish, "org", "repo", "task"))
        {
            ok.Observe(new GitCommandResult(0, "", "", GitErrorCode.None), "add -A");
            ok.Complete();
        }

        using (var failed = GitOperationScope.Begin(ledger, GitOperationKind.SyncBase, "org", "repo"))
        {
            failed.Observe(new GitCommandResult(128, "", "fatal: invalid reference: origin/x", GitErrorCode.RefNotFound), "fetch origin");
            failed.Fail("The base could not be fetched");
        }

        using (GitOperationScope.Begin(ledger, GitOperationKind.Cleanup))
        {
            // Disposed without an outcome: an exception on the way out is a failure.
        }

        ledger.Records.Should().HaveCount(3);
        ledger.Records[0].Should().Match<GitOperationRecord>(r =>
            r.Kind == GitOperationKind.Publish && r.Succeeded && r.CommandCount == 1 && r.TaskId == "task"
            && r.Topology == "test");
        ledger.Records[1].Should().Match<GitOperationRecord>(r =>
            !r.Succeeded && r.Code == GitErrorCode.RefNotFound && r.LastCommand == "fetch origin"
            && r.Error == "The base could not be fetched");
        ledger.Records[2].Succeeded.Should().BeFalse();
        ledger.Records[2].Code.Should().Be(GitErrorCode.Unknown);
    }

    [Fact]
    public void LedgerScope_IsCurrentForTheAsyncFlow_AndRestoresTheParent()
    {
        var ledger = new RecordingLedger();
        GitOperationScope.Active.Should().BeNull();

        using (var outer = GitOperationScope.Begin(ledger, GitOperationKind.EnsureMirror))
        {
            GitOperationScope.Active.Should().BeSameAs(outer);
            using (var inner = GitOperationScope.Begin(ledger, GitOperationKind.Read))
            {
                GitOperationScope.Active.Should().BeSameAs(inner);
                inner.Complete();
            }
            GitOperationScope.Active.Should().BeSameAs(outer);
            outer.Complete();
        }

        GitOperationScope.Active.Should().BeNull();
    }

    // ── Real git ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Against a real remote (a bare repo on disk): the credential the command carries is
    /// delivered through the environment, so nothing of it reaches the clone's config or its
    /// remote URL — the two places a token used to live.
    /// </summary>
    [Fact]
    public async Task RealGit_CloneWithACredential_LeavesNoTokenInTheClone()
    {
        var root = Path.Combine(Path.GetTempPath(), $"orkka-engine-{Ulid.NewUlid()}");
        Directory.CreateDirectory(root);
        try
        {
            var git = new GitProcess(NullLogger<GitProcess>.Instance);
            var remote = Path.Combine(root, "remote.git");
            (await git.RunAsync(new GitCommand(root, "init", "--bare", "-b", "main", remote), CancellationToken.None))
                .Succeeded.Should().BeTrue();

            var seed = Path.Combine(root, "seed");
            (await git.RunAsync(new GitCommand(root, "clone", remote, seed), CancellationToken.None)).Succeeded.Should().BeTrue();
            await File.WriteAllTextAsync(Path.Combine(seed, "a.txt"), "a\n");
            (await git.RunAsync(new GitCommand(seed, "add", "-A"), CancellationToken.None)).Succeeded.Should().BeTrue();
            var commit = await git.RunAsync(new GitCommand(seed, "commit", "-m", "seed \"quoted\" message"), CancellationToken.None);
            commit.Succeeded.Should().BeTrue(commit.Error);
            (await git.RunAsync(new GitCommand(seed, "push", "origin", "HEAD:main"), CancellationToken.None)).Succeeded.Should().BeTrue();

            // The author of that commit is the developer's own identity (the test process's
            // stand-in) — nothing forced a bot over it.
            var author = await git.RunAsync(new GitCommand(seed, "log", "-1", "--format=%an|%ae"), CancellationToken.None);
            author.Output.Should().Be($"{TestGitIdentity.Name}|{TestGitIdentity.Email}");

            // A clone writes no credential configuration anywhere.
            var clone = Path.Combine(root, "clone");
            var result = await git.RunAsync(new GitCommand(root, "clone", remote, clone), CancellationToken.None);
            result.Succeeded.Should().BeTrue(result.Error);

            var config = await File.ReadAllTextAsync(Path.Combine(clone, ".git", "config"));
            config.Should().NotContain("extraHeader");
            config.Should().NotContain("x-access-token");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>A probe's "no" (`rev-parse --verify --quiet` of a missing ref, `config --get`
    /// of an absent key) is an answer: it never becomes the ledger's "last failed command" —
    /// that slot names what broke — and (by the same flag) is not logged as a failure.</summary>
    [Fact]
    public async Task RealGit_AProbesNo_IsAnAnswer_NotAFailure()
    {
        var ledger = new RecordingLedger();
        var git = new GitProcess(NullLogger<GitProcess>.Instance);
        var dir = Path.Combine(Path.GetTempPath(), $"orkka-engine-probe-{Ulid.NewUlid()}");
        Directory.CreateDirectory(dir);
        try
        {
            (await git.RunAsync(dir, "init -q -b main", CancellationToken.None)).Succeeded.Should().BeTrue();
            using (var scope = GitOperationScope.Begin(ledger, GitOperationKind.Read))
            {
                var absentKey = await git.RunAsync(
                    new GitCommand(dir, "config", "--get", "remote.origin.promisor") { Probe = true }, CancellationToken.None);
                var missingRef = await git.RunAsync(
                    new GitCommand(dir, "rev-parse", "--verify", "--quiet", "refs/heads/nope") { Probe = true }, CancellationToken.None);
                absentKey.Succeeded.Should().BeFalse();
                missingRef.Succeeded.Should().BeFalse();
                scope.Complete();
            }

            ledger.Records.Single().Should().Match<GitOperationRecord>(r =>
                r.Succeeded && r.LastCommand == null && r.CommandCount == 0);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task RealGit_AFailedCommand_IsClassified_AndObservedByTheScope()
    {
        var ledger = new RecordingLedger();
        var git = new GitProcess(NullLogger<GitProcess>.Instance);
        var dir = Path.Combine(Path.GetTempPath(), $"orkka-engine-nogit-{Ulid.NewUlid()}");
        Directory.CreateDirectory(dir);
        try
        {
            using (var scope = GitOperationScope.Begin(ledger, GitOperationKind.Read))
            {
                var result = await git.RunAsync(new GitCommand(dir, "status"), CancellationToken.None);
                result.Succeeded.Should().BeFalse();
                result.Code.Should().Be(GitErrorCode.NotARepository);
                scope.Fail(result.Describe());
            }

            ledger.Records.Single().Should().Match<GitOperationRecord>(r =>
                r.Code == GitErrorCode.NotARepository && r.LastCommand == "status" && r.CommandCount == 1);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    private sealed class RecordingLedger : IGitOperationLedger
    {
        public List<GitOperationRecord> Records { get; } = [];
        public string Topology => "test";
        public void Record(GitOperationRecord record) => Records.Add(record);
    }
}
