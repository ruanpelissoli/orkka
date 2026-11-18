namespace Orkka.Tests.Services;

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
/// Chat runs only on the requester's own runner (user decision 2026-10-26): there is no
/// in-process turn on Orkka's key, whatever the member's old execution preference says.
/// </summary>
public sealed class ChatRunnerOnlyTests
{
    private static readonly Ulid OrgId = Ulid.NewUlid();

    [Fact]
    public async Task AnOfflineRunner_FailsTheTurn_AndNothingRunsInProcess()
    {
        ChatRequesterContext.Set(OrgId, "user_offline");
        var workerCalls = 0;
        var invoker = Invoker(online: false, () => workerCalls++);

        var chunks = await Collect(invoker);

        chunks.Should().HaveCount(2);
        chunks[0].Type.Should().Be(ChatChunkType.Error);
        chunks[0].Content.Should().Contain("./orkka start").And.NotContain("Cloud");
        chunks[1].Type.Should().Be(ChatChunkType.Done);
        workerCalls.Should().Be(0);
    }

    [Fact]
    public async Task ATurnWithNoRequester_HasNoRunnerToRunOn_AndFails()
    {
        ChatRequesterContext.Clear();
        var invoker = Invoker(online: true, () => { });

        var chunks = await Collect(invoker);

        chunks[0].Type.Should().Be(ChatChunkType.Error);
    }

    [Fact]
    public async Task PreparingAWorkspace_WithTheRunnerOffline_Throws_InsteadOfCloningInProcess()
    {
        ChatRequesterContext.Set(OrgId, "user_offline");
        var invoker = Invoker(online: false, () => { });

        var act = () => invoker.EnsureChatWorkspaceAsync(OrgId, Ulid.NewUlid(), []);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*./orkka start*");
    }

    /// <summary>The task Discuss chat's fail-fast check: the same reason, before anything is
    /// saved or cloned, naming the command that fixes it.</summary>
    [Fact]
    public async Task RouteBlocker_NamesOrkkaLocalStart_WhenOffline_AndIsNullWhenOnline()
    {
        ChatRequesterContext.Set(OrgId, "user_offline");
        (await Invoker(online: false, () => { }).GetRouteBlockerAsync())
            .Should().Contain("Your runner is offline").And.Contain("./orkka start");

        ChatRequesterContext.Set(OrgId, "user_online");
        (await Invoker(online: true, () => { }).GetRouteBlockerAsync()).Should().BeNull();
    }

    private static async Task<List<ChatChunk>> Collect(HybridChatAgentInvoker invoker)
    {
        var chunks = new List<ChatChunk>();
        await foreach (var c in invoker.InvokeAgentAsync("ws", "system", "hi", "claude-sonnet-5"))
            chunks.Add(c);
        return chunks;
    }

    private static HybridChatAgentInvoker Invoker(bool online, Action onWorkerCall)
    {
        var services = new ServiceCollection()
            .AddSingleton<IRunnerFleet>(new Fleet(online))
            .BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runners:RelayUrlTemplate"] = "http://{pod}.orkka-worker:8080",
            })
            .Build();

        return new HybridChatAgentInvoker(
            services.GetRequiredService<IServiceScopeFactory>(),
            new Factory(onWorkerCall),
            configuration,
            NullLogger<HybridChatAgentInvoker>.Instance);
    }

    private sealed class Fleet(bool online) : IRunnerFleet
    {
        public Task<RunnerPresenceSnapshot?> GetPresenceAsync(Ulid orgId, string userId, CancellationToken ct = default)
            => Task.FromResult<RunnerPresenceSnapshot?>(new RunnerPresenceSnapshot(
                orgId, userId, Online: online, "laptop", "1.0.0",
                WorkerInstance: "orkka-worker-0",
                ConnectedAt: DateTimeOffset.UtcNow, LastSeenAt: DateTimeOffset.UtcNow,
                ProvidersJson: "[]", CapabilitiesJson: null));

        public Task<IReadOnlyList<RunnerPresenceSnapshot>> ListPresenceAsync(Ulid orgId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RunnerPresenceSnapshot>>([]);

        public Task<RunnerProviderCoverage?> GetProviderCoverageAsync(Ulid orgId, string userId, CancellationToken ct = default)
            => Task.FromResult<RunnerProviderCoverage?>(null);
    }

    private sealed class Factory(Action onCall) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(onCall));
    }

    private sealed class Handler(Action onCall) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            onCall();
            throw new InvalidOperationException("the Worker must not be called");
        }
    }
}
