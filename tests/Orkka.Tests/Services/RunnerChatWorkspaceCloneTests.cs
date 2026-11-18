namespace Orkka.Tests.Services;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.Runners.Server;

/// <summary>
/// Preparing a chat workspace on the user's own machine, when a repo will not clone.
///
/// Observed in production: a runner with no git credential helper failed all three of a
/// project's repos, and seven chat turns over seven minutes fired twenty-one identical
/// clone attempts — each one guaranteed to fail for the same permanent reason. Two things
/// are pinned here: the failure is remembered for a window instead of retried per turn,
/// and it comes back carrying a reason, so the turn can tell the human what to fix.
/// </summary>
public sealed class RunnerChatWorkspaceCloneTests
{
    private const string CredentialFailure =
        "Git command failed (exit 128): fatal: could not read Username for "
        + "'https://github.com': terminal prompts disabled";

    [Fact]
    public async Task CloneFailure_ExcludesTheRepoAndReportsWhy()
    {
        var repo = Repo("esfer-api");
        var link = Link(cloneResult: RunnerOpResult.Fail(CredentialFailure));

        var prepared = await Invoker(link).EnsureChatWorkspaceAsync(Ulid.NewUlid(), Ulid.NewUlid(), [repo]);

        prepared.Unavailable.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                repo.Id,
                Name = "esfer-api",
                Reason = RepoAccessFailureReason.GitCredentials,
                Detail = CredentialFailure,
                // This proxy IS the user's machine, so the notice must offer them the fix
                // they can actually apply on that machine.
                OnUserMachine = true,
            });
        // The name has to survive: a notice that cannot say which repo is missing is not
        // one the user can act on.
        prepared.UnavailableRepoIds.Should().Equal(repo.Id);
    }

    /// <summary>The excluded repo must not reach the runner's workspace request either —
    /// the prompt describes what is in that folder, and it will not be in it.</summary>
    [Fact]
    public async Task CloneFailure_LeavesTheRepoOutOfTheWorkspaceRequest()
    {
        var good = Repo("esfer-web");
        var bad = Repo("esfer-api");
        EnsureChatWorkspaceRequest? sent = null;
        var link = Link(
            cloneResult: RunnerOpResult.Fail(CredentialFailure),
            cloneSucceedsFor: good.Id,
            captureWorkspace: r => sent = r);

        await Invoker(link).EnsureChatWorkspaceAsync(Ulid.NewUlid(), Ulid.NewUlid(), [good, bad]);

        sent.Should().NotBeNull();
        sent!.Repos.Select(r => r.Name).Should().Equal("esfer-web");
    }

    /// <summary>
    /// The retry storm. Nothing about a missing credential helper can change between two
    /// turns a minute apart, so the second turn must replay the remembered failure rather
    /// than pay for an identical attempt.
    /// </summary>
    [Fact]
    public async Task PermanentFailure_IsAttemptedOncePerWindow_NotOncePerTurn()
    {
        var repo = Repo("esfer-api");
        var orgId = Ulid.NewUlid();
        var projectId = Ulid.NewUlid();
        var clock = new TestClock();
        var cache = new BareCloneFailureCache(
            permanentWindow: TimeSpan.FromMinutes(5), timeProvider: clock);
        var link = Link(cloneResult: RunnerOpResult.Fail(CredentialFailure));
        var invoker = Invoker(link, cache);

        // Seven turns, the same seven minutes of chat that produced the production log.
        for (var turn = 0; turn < 7; turn++)
        {
            var prepared = await invoker.EnsureChatWorkspaceAsync(orgId, projectId, [repo]);

            // Still excluded, and still with the reason — the user is told every turn, not
            // just on the turn that happened to attempt the clone.
            prepared.Unavailable.Should().ContainSingle()
                .Which.Reason.Should().Be(RepoAccessFailureReason.GitCredentials);

            clock.Advance(TimeSpan.FromMinutes(1));
        }

        VerifyCloneAttempts(link, times: 2, "one attempt in the first 5-minute window, one in the second");
    }

    /// <summary>
    /// Not cached forever: fixing git credentials happens on the user's own machine with no
    /// way to tell us about it, so the next message after the window has to try for real.
    /// </summary>
    [Fact]
    public async Task PermanentFailure_IsRetriedOnceTheWindowExpires()
    {
        var repo = Repo("esfer-api");
        var orgId = Ulid.NewUlid();
        var clock = new TestClock();
        var cache = new BareCloneFailureCache(
            permanentWindow: TimeSpan.FromMinutes(5), timeProvider: clock);
        var link = Link(cloneResult: RunnerOpResult.Fail(CredentialFailure));
        var invoker = Invoker(link, cache);

        await invoker.EnsureChatWorkspaceAsync(orgId, Ulid.NewUlid(), [repo]);
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await invoker.EnsureChatWorkspaceAsync(orgId, Ulid.NewUlid(), [repo]);

        VerifyCloneAttempts(link, times: 2, "the user may have signed in during the window");
    }

    /// <summary>
    /// A runner that was merely unreachable must not be treated like a broken credential.
    /// It comes back on its own, and holding the whole codebase out of the conversation for
    /// five minutes after it did would be worse than the retry it saves.
    /// </summary>
    [Fact]
    public async Task TransientFailure_UsesTheShortWindow()
    {
        var repo = Repo("esfer-api");
        var orgId = Ulid.NewUlid();
        var clock = new TestClock();
        var cache = new BareCloneFailureCache(
            permanentWindow: TimeSpan.FromMinutes(5),
            transientWindow: TimeSpan.FromSeconds(30),
            timeProvider: clock);
        var link = Link(cloneResult: RunnerOpResult.Fail("error: RPC failed; curl 92"));
        var invoker = Invoker(link, cache);

        await invoker.EnsureChatWorkspaceAsync(orgId, Ulid.NewUlid(), [repo]);
        clock.Advance(TimeSpan.FromMinutes(1));
        await invoker.EnsureChatWorkspaceAsync(orgId, Ulid.NewUlid(), [repo]);

        VerifyCloneAttempts(link, times: 2, "a minute is well past the transient window");
    }

    /// <summary>
    /// A repo that starts working again leaves no trace of having failed. Belt-and-braces
    /// next to the expiry (a read past the window already drops the entry) — but a stale
    /// verdict replayed over a repo that now clones fine is the worst failure this cache
    /// could have, so the success path clears explicitly rather than relying on that.
    /// </summary>
    [Fact]
    public async Task SuccessfulClone_ForgetsAnEarlierFailure()
    {
        var repo = Repo("esfer-api");
        var orgId = Ulid.NewUlid();
        var clock = new TestClock();
        var cache = new BareCloneFailureCache(
            permanentWindow: TimeSpan.FromMinutes(5), timeProvider: clock);

        var failing = Link(cloneResult: RunnerOpResult.Fail(CredentialFailure));
        await Invoker(failing, cache).EnsureChatWorkspaceAsync(orgId, Ulid.NewUlid(), [repo]);

        // Past the window, so the retry happens for real — and succeeds.
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var working = Link(cloneResult: RunnerOpResult.Ok());
        var prepared = await Invoker(working, cache).EnsureChatWorkspaceAsync(orgId, Ulid.NewUlid(), [repo]);
        prepared.Unavailable.Should().BeEmpty();

        // A second later — well inside a window, had one survived the success — the next
        // failure still costs a real attempt instead of replaying the old verdict.
        clock.Advance(TimeSpan.FromSeconds(1));
        var failingAgain = Link(cloneResult: RunnerOpResult.Fail(CredentialFailure));
        await Invoker(failingAgain, cache).EnsureChatWorkspaceAsync(orgId, Ulid.NewUlid(), [repo]);

        VerifyCloneAttempts(failingAgain, times: 1, "the success left nothing behind");
    }

    /// <summary>The cache is keyed per (org, repo) — one org's broken credentials must not
    /// suppress another org's clone of a repo that happens to share the id space.</summary>
    [Fact]
    public async Task Failures_AreRememberedPerOrgAndRepo()
    {
        var repo = Repo("esfer-api");
        var cache = new BareCloneFailureCache(timeProvider: new TestClock());
        var link = Link(cloneResult: RunnerOpResult.Fail(CredentialFailure));
        var invoker = Invoker(link, cache);

        await invoker.EnsureChatWorkspaceAsync(Ulid.NewUlid(), Ulid.NewUlid(), [repo]);
        await invoker.EnsureChatWorkspaceAsync(Ulid.NewUlid(), Ulid.NewUlid(), [repo]);

        VerifyCloneAttempts(link, times: 2, "different orgs, different machines, different credentials");
    }

    /// <summary>A first clone of a big repo is minutes, not seconds: the op must travel with
    /// the clone budget, not the link's five-minute default that killed the 2026-09-07 turn.</summary>
    [Fact]
    public async Task CloneOps_CarryTheCloneBudget()
    {
        var link = Link(cloneResult: RunnerOpResult.Ok());

        await new RunnerChatInvoker(
                link.Object, NullLogger<RunnerChatInvoker>.Instance,
                ensureBareClones: true,
                cloneFailures: new BareCloneFailureCache(timeProvider: new TestClock()))
            .EnsureChatWorkspaceAsync(Ulid.NewUlid(), Ulid.NewUlid(), [Repo("esfer-api")]);

        link.Verify(l => l.InvokeAsync<RunnerOpResult>(
                RunnerProtocol.EnsureBareClone, It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>(), RunnerOpBudgets.Clone),
            Times.Once, "the clone budget is what lets a large first clone finish");
        RunnerOpBudgets.Clone.Should().BeGreaterThan(TimeSpan.FromMinutes(5));
    }

    /// <summary>A clone that still outruns its budget is ONE repo the agent goes without,
    /// named in the notice — never an exception that turns the whole turn into a 500.</summary>
    [Fact]
    public async Task CloneOpThatOutrunsItsBudget_ReportsTheRepoUnavailable_NotAnException()
    {
        var link = Link(cloneResult: RunnerOpResult.Ok());
        link.Setup(l => l.InvokeAsync<RunnerOpResult>(
                RunnerProtocol.EnsureBareClone, It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .ThrowsAsync(new InvalidOperationException("Invocation canceled by the server."));

        var prepared = await new RunnerChatInvoker(
                link.Object, NullLogger<RunnerChatInvoker>.Instance,
                ensureBareClones: true,
                cloneFailures: new BareCloneFailureCache(timeProvider: new TestClock()))
            .EnsureChatWorkspaceAsync(Ulid.NewUlid(), Ulid.NewUlid(), [Repo("esfer-api")]);

        prepared.Token.Should().Be("workspace-token", "the turn still runs on what is available");
        prepared.Unavailable.Should().ContainSingle()
            .Which.Detail.Should().Contain("time budget");
    }

    /// <summary>Orkka Local leaves ensureBareClones off — the host's own git reader is
    /// already a runner proxy, so cloning here would cost a second fetch per turn. Nothing
    /// about the cache may change that.</summary>
    [Fact]
    public async Task WithoutEnsureBareClones_NoCloneIsAttemptedAndNothingIsExcluded()
    {
        var link = Link(cloneResult: RunnerOpResult.Fail(CredentialFailure));

        var prepared = await new RunnerChatInvoker(
                link.Object, NullLogger<RunnerChatInvoker>.Instance,
                cloneFailures: new BareCloneFailureCache(timeProvider: new TestClock()))
            .EnsureChatWorkspaceAsync(Ulid.NewUlid(), Ulid.NewUlid(), [Repo("esfer-api")]);

        prepared.Unavailable.Should().BeEmpty();
        VerifyCloneAttempts(link, times: 0, "the local edition's clone already happened");
    }

    /// <summary>
    /// The defaults are load-bearing in a way a constant usually is not: the notice tells
    /// the user their fix will be picked up "in the next few minutes", and only a short
    /// window makes that true. A transient failure gets far less, so a runner that
    /// reconnects is not held out of its own conversation.
    /// </summary>
    [Fact]
    public void DefaultWindows_StayShortEnoughToKeepTheNoticeHonest()
    {
        BareCloneFailureCache.DefaultPermanentWindow
            .Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(5))
            .And.BeGreaterThan(TimeSpan.FromMinutes(1), "a window this side of a minute saves nothing");

        BareCloneFailureCache.DefaultTransientWindow
            .Should().BeLessThan(BareCloneFailureCache.DefaultPermanentWindow);
    }

    // ------------------------------------------------------------------------ harness

    private static ChatWorkspaceRepo Repo(string name)
        => new(Ulid.NewUlid(), name, "main", $"https://github.com/acme-dev/{name}.git");

    private static RunnerChatInvoker Invoker(
        Mock<IRunnerLink> link, BareCloneFailureCache? cache = null)
        => new(
            link.Object,
            NullLogger<RunnerChatInvoker>.Instance,
            ensureBareClones: true,
            cloneFailures: cache ?? new BareCloneFailureCache(timeProvider: new TestClock()));

    /// <summary>A connected runner that answers EnsureBareClone with <paramref name="cloneResult"/>
    /// (except for <paramref name="cloneSucceedsFor"/>) and always accepts the workspace op.</summary>
    private static Mock<IRunnerLink> Link(
        RunnerOpResult cloneResult,
        Ulid? cloneSucceedsFor = null,
        Action<EnsureChatWorkspaceRequest>? captureWorkspace = null)
    {
        var link = new Mock<IRunnerLink>();
        link.SetupGet(l => l.IsConnected).Returns(true);
        link.SetupGet(l => l.Status).Returns((true, (RunnerHello?)null));

        link.Setup(l => l.InvokeAsync<RunnerOpResult>(
                RunnerProtocol.EnsureBareClone, It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .ReturnsAsync((string _, object?[] args, CancellationToken _, TimeSpan? _) =>
            {
                var request = (EnsureBareCloneRequest)args[0]!;
                return cloneSucceedsFor?.ToString() == request.RepoId
                    ? RunnerOpResult.Ok()
                    : cloneResult;
            });

        link.Setup(l => l.InvokeAsync<RunnerOpResult>(
                RunnerProtocol.EnsureChatWorkspace, It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .ReturnsAsync((string _, object?[] args, CancellationToken _, TimeSpan? _) =>
            {
                captureWorkspace?.Invoke((EnsureChatWorkspaceRequest)args[0]!);
                return RunnerOpResult.Ok("workspace-token");
            });

        return link;
    }

    private static void VerifyCloneAttempts(Mock<IRunnerLink> link, int times, string because)
        => link.Verify(l => l.InvokeAsync<RunnerOpResult>(
                RunnerProtocol.EnsureBareClone, It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()),
            Times.Exactly(times), because);

    /// <summary>Controllable clock — the backoff window is the whole point of these tests
    /// and real time cannot exercise it.</summary>
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 8, 25, 1, 16, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
