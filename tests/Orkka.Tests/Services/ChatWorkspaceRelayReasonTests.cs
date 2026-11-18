namespace Orkka.Tests.Services;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Runners.Contracts;
using Orkka.SharedKernel.Ai;

/// <summary>
/// The hosted route, which is the one that broke in production: the API pod asks a Worker
/// pod, the Worker asks the user's runner, and the clone fails on the user's machine. What
/// is pinned here is that the REASON survives all three hops — an id-only answer leaves the
/// API pod able to drop the repo but unable to tell the user why, which is the defect.
///
/// The two <c>RelayWorkspaceResponse</c> records are hand-duplicated on either side of the
/// wire (the Worker does not reference the Agents module), so their compatibility is a real
/// thing to test rather than a compiler guarantee.
/// </summary>
public sealed class ChatWorkspaceRelayReasonTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RelayedPreparation_CarriesTheReasonBackToTheApiPod()
    {
        var repo = Repo("esfer-api");
        var invoker = Invoker(WorkerAnswer(new Orkka.Runners.Server.Hosting.RelayWorkspaceResponse(
            "workspace-token", null,
            [repo.Id.ToString()],
            [new Orkka.Runners.Server.Hosting.RelayUnavailableRepo(
                repo.Id.ToString(), "esfer-api",
                nameof(RepoAccessFailureReason.GitCredentials),
                "fatal: could not read Username for 'https://github.com'")])));

        var prepared = await invoker.EnsureChatWorkspaceAsync(OrgId, Ulid.NewUlid(), [repo]);

        prepared.Token.Should().Be("workspace-token");
        var missing = prepared.Unavailable.Should().ContainSingle().Subject;
        missing.Id.Should().Be(repo.Id);
        missing.Name.Should().Be("esfer-api");
        missing.Reason.Should().Be(RepoAccessFailureReason.GitCredentials);

        // And the reason has to reach the user as advice, not as git's stderr — advice about
        // the repository's access in Orkka, never about signing in on their machine.
        RepoAccessDiagnostics.BuildNotice(prepared.Unavailable)
            .Should().Contain("esfer-api").And.Contain("refused your git credentials");
    }

    /// <summary>
    /// Rolling deploys: a Worker that predates the detailed field sends only ids. The repo
    /// must still be excluded and still be named — the name is recovered from the request,
    /// since the caller knows what it asked for.
    /// </summary>
    [Fact]
    public async Task OlderWorker_SendingOnlyIds_StillExcludesAndNamesTheRepo()
    {
        var repo = Repo("esfer-api");
        var invoker = Invoker(WorkerAnswer(new Orkka.Runners.Server.Hosting.RelayWorkspaceResponse(
            "workspace-token", null, [repo.Id.ToString()])));

        var prepared = await invoker.EnsureChatWorkspaceAsync(OrgId, Ulid.NewUlid(), [repo]);

        var missing = prepared.Unavailable.Should().ContainSingle().Subject;
        missing.Id.Should().Be(repo.Id);
        missing.Name.Should().Be("esfer-api");
        missing.Reason.Should().Be(RepoAccessFailureReason.Unknown, "the old wire form carried none");
    }

    /// <summary>A newer Worker naming a reason this pod has never heard of must degrade to
    /// Unknown, not throw — the two are deployed in either order.</summary>
    [Fact]
    public async Task UnknownReasonName_DegradesInsteadOfThrowing()
    {
        var repo = Repo("esfer-api");
        var invoker = Invoker(WorkerAnswer(new Orkka.Runners.Server.Hosting.RelayWorkspaceResponse(
            "workspace-token", null, [repo.Id.ToString()],
            [new Orkka.Runners.Server.Hosting.RelayUnavailableRepo(
                repo.Id.ToString(), "esfer-api", "SomeReasonFromTheFuture", null)])));

        var prepared = await invoker.EnsureChatWorkspaceAsync(OrgId, Ulid.NewUlid(), [repo]);

        prepared.Unavailable.Should().ContainSingle()
            .Which.Reason.Should().Be(RepoAccessFailureReason.Unknown);
    }

    [Fact]
    public async Task EveryRepoAvailable_ReportsNothingMissing()
    {
        var repo = Repo("esfer-api");
        var invoker = Invoker(WorkerAnswer(
            new Orkka.Runners.Server.Hosting.RelayWorkspaceResponse("workspace-token", null)));

        var prepared = await invoker.EnsureChatWorkspaceAsync(OrgId, Ulid.NewUlid(), [repo]);

        prepared.Unavailable.Should().BeEmpty();
        RepoAccessDiagnostics.BuildNotice(prepared.Unavailable).Should().BeNull();
    }

    // ------------------------------------------------------------------------ harness

    private static readonly Ulid OrgId = Ulid.NewUlid();
    private const string UserId = "user_1";

    private static ChatWorkspaceRepo Repo(string name)
        => new(Ulid.NewUlid(), name, "main", $"https://github.com/acme-dev/{name}.git");

    private static Func<HttpRequestMessage, HttpResponseMessage> WorkerAnswer(
        Orkka.Runners.Server.Hosting.RelayWorkspaceResponse response)
        => _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // Serialized from the WORKER's own record and read back through the API pod's
            // copy — the point of the test is that those two agree.
            Content = JsonContent.Create(response, options: Json),
        };

    private static HybridChatAgentInvoker Invoker(Func<HttpRequestMessage, HttpResponseMessage> worker)
    {
        ChatRequesterContext.Set(OrgId, UserId);

        var services = new ServiceCollection()
            .AddSingleton<IRunnerFleet>(new OnlineFleet())
            .BuildServiceProvider();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runners:RelayUrlTemplate"] = "http://{pod}.orkka-worker:8080",
            })
            .Build();

        return new HybridChatAgentInvoker(
            services.GetRequiredService<IServiceScopeFactory>(),
            new StubHttpClientFactory(worker),
            configuration,
            NullLogger<HybridChatAgentInvoker>.Instance);
    }

    /// <summary>A live runner on a known Worker pod, so the relay route resolves.</summary>
    private sealed class OnlineFleet : IRunnerFleet
    {
        public Task<RunnerPresenceSnapshot?> GetPresenceAsync(
            Ulid orgId, string userId, CancellationToken ct = default)
            => Task.FromResult<RunnerPresenceSnapshot?>(new RunnerPresenceSnapshot(
                orgId, userId, Online: true, "orkka-worker-node-0", "1.0.0",
                WorkerInstance: "orkka-worker-0",
                ConnectedAt: DateTimeOffset.UtcNow, LastSeenAt: DateTimeOffset.UtcNow,
                ProvidersJson: "[]", CapabilitiesJson: null));

        public Task<IReadOnlyList<RunnerPresenceSnapshot>> ListPresenceAsync(
            Ulid orgId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RunnerPresenceSnapshot>>([]);

        public Task<RunnerProviderCoverage?> GetProviderCoverageAsync(
            Ulid orgId, string userId, CancellationToken ct = default)
            => Task.FromResult<RunnerProviderCoverage?>(null);
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(handler));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
