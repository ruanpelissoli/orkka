namespace Orkka.Tests.Features.Models;

using System.Threading.Channels;
using FluentAssertions;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.Runners.Server;
using Orkka.Runners.Server.Hosting;

/// <summary>
/// One auto-pick order for the three runner-backed readiness implementations (the Local
/// edition's, the hosted Worker's per-link one, and the utility link's). They used to each
/// carry a copy with a "keep in step" comment; this pins that they now read the same constant
/// and produce the same list from the same Hello.
/// </summary>
public sealed class ProviderPreferenceTests
{
    /// <summary>Minimal <see cref="IRunnerLink"/>: only <see cref="Status"/> matters to readiness.</summary>
    private sealed class FakeLink(bool connected, RunnerHello? hello) : IRunnerLink
    {
        public bool IsConnected => connected;
        public (bool Connected, RunnerHello? Hello) Status => (connected, hello);
        public string? GetProviderBlocker(AiProvider provider) => throw new NotSupportedException();
        public Task<T> InvokeAsync<T>(string method, object?[] args, CancellationToken ct, TimeSpan? opTimeout = null)
            => throw new NotSupportedException();
        public Task SendAsync(string method, object? arg, CancellationToken ct) => throw new NotSupportedException();
        public Channel<RunnerChunk> OpenRunChannel(string runId) => throw new NotSupportedException();
        public void CloseRunChannel(string runId) => throw new NotSupportedException();
        public bool TryGetRunChannel(string runId, out Channel<RunnerChunk> channel) => throw new NotSupportedException();
        public IAsyncEnumerable<RunnerChunk> ConsumeRunAsync(string runId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static RunnerHello Hello(params RunnerProviderStatus[] providers) => new(
        ProtocolVersion: 1, MachineName: "ruan-laptop",
        ClaudeCliFound: true, ClaudeAuthenticated: true,
        ActiveRunIds: [], Providers: providers);

    private static RunnerHello EveryProviderReady() => Hello(
        RunnerProviders.All.Select(p => new RunnerProviderStatus(p, CliFound: true, Authenticated: true)).ToArray());

    private static IReadOnlyList<IProviderReadiness> EveryReadiness(IRunnerLink link) =>
    [
        new HostedRunnerReadiness(link),
        new LinkProviderReadiness(link),
    ];

    // ── The constant itself ─────────────────────────────────────────────────

    [Fact]
    public void Order_CoversEveryChatProvider_ExactlyOnce()
    {
        var chatProviders = Enum.GetValues<AiProvider>().Where(p => p != AiProvider.PixelLab);

        ProviderPreference.Order.Should().OnlyHaveUniqueItems();
        ProviderPreference.Order.Should().BeEquivalentTo(chatProviders,
            "every provider an agent can run on needs a place in the auto-pick order, and the "
            + "image-only vendor must never be auto-picked for a run");
    }

    [Fact]
    public void Order_StartsWithAnthropic_AndEndsWithOllama()
    {
        ProviderPreference.Order[0].Should().Be(AiProvider.Anthropic);
        ProviderPreference.Order[^1].Should().Be(AiProvider.Ollama, "local models are the weakest auto-pick");
        ProviderPreference.Order[^2].Should().Be(AiProvider.OpenCode,
            "a harness auto-pick silently picks whichever vendor its default points at");
    }

    [Fact]
    public void Order_PutsMeteredCopilot_AfterTheSubscriptionClis()
    {
        var order = ProviderPreference.Order.ToList();
        var copilot = order.IndexOf(AiProvider.GitHubCopilot);

        foreach (var unmetered in new[] { AiProvider.Anthropic, AiProvider.OpenAI, AiProvider.Google, AiProvider.XAI })
            order.IndexOf(unmetered).Should().BeLessThan(copilot,
                $"{unmetered} is unmetered and must be auto-picked before a provider that spends the user's premium requests");
    }

    [Fact]
    public void EveryProviderInTheOrder_MapsToADistinctRunnerCliKey()
    {
        // The readiness lookups key the Hello by this mapping; two providers sharing a CLI key
        // would report each other's login state.
        var keys = ProviderPreference.Order.Select(p => RunnerProviders.FromAiProviderName(p.ToString())).ToList();

        keys.Should().OnlyHaveUniqueItems();
        keys.Should().BeEquivalentTo(RunnerProviders.All);
    }

    // ── The three readiness classes agree ───────────────────────────────────

    [Fact]
    public void EveryReadinessClass_ReturnConnectedProviders_InPreferenceOrder()
    {
        var link = new FakeLink(connected: true, EveryProviderReady());

        foreach (var readiness in EveryReadiness(link))
            readiness.Connected.Should().Equal(ProviderPreference.Order,
                $"{readiness.GetType().Name} must list every ready provider in the shared order");
    }

    [Fact]
    public void EveryReadinessClass_KeepTheOrder_WhenOnlySomeAreReady()
    {
        // Listed in the Hello in the "wrong" order on purpose: the order must come from the
        // constant, not from the runner's array.
        var link = new FakeLink(connected: true, Hello(
            new RunnerProviderStatus(RunnerProviders.Copilot, true, true),
            new RunnerProviderStatus(RunnerProviders.Claude, true, false),
            new RunnerProviderStatus(RunnerProviders.Codex, true, true),
            new RunnerProviderStatus(RunnerProviders.Ollama, false, false),
            new RunnerProviderStatus(RunnerProviders.Grok, true, true)));

        foreach (var readiness in EveryReadiness(link))
            readiness.Connected.Should().Equal(
                [AiProvider.OpenAI, AiProvider.XAI, AiProvider.GitHubCopilot],
                readiness.GetType().Name);
    }

    [Fact]
    public void EveryReadinessClass_ReportNothing_WhenTheRunnerIsOffline()
    {
        foreach (var readiness in EveryReadiness(new FakeLink(connected: false, EveryProviderReady())))
        {
            readiness.Connected.Should().BeEmpty(readiness.GetType().Name);
            readiness.GetAnyProviderBlocker().Should().NotBeNull(readiness.GetType().Name);
        }

        foreach (var readiness in EveryReadiness(new FakeLink(connected: true, hello: null)))
            readiness.Connected.Should().BeEmpty(readiness.GetType().Name);
    }

    [Fact]
    public void EveryReadinessClass_TreatInstalledButSignedOut_AsNotConnected()
    {
        var link = new FakeLink(connected: true, Hello(
            RunnerProviders.All.Select(p => new RunnerProviderStatus(p, CliFound: true, Authenticated: false)).ToArray()));

        foreach (var readiness in EveryReadiness(link))
        {
            readiness.Connected.Should().BeEmpty(readiness.GetType().Name);
            readiness.GetAnyProviderBlocker().Should().NotBeNull(readiness.GetType().Name);
        }
    }

    // ── ProviderDisplayNames ────────────────────────────────────────────────

    [Theory]
    [InlineData(AiProvider.Anthropic, "Claude")]
    [InlineData(AiProvider.OpenAI, "OpenAI Codex")]
    [InlineData(AiProvider.Google, "Gemini")]
    [InlineData(AiProvider.GitHubCopilot, "GitHub Copilot")]
    [InlineData(AiProvider.Ollama, "Ollama")]
    [InlineData(AiProvider.XAI, "Grok")]
    [InlineData(AiProvider.OpenCode, "OpenCode")]
    [InlineData(AiProvider.PixelLab, "PixelLab")]
    public void DisplayNames_AreTheUserFacingVendorNames(AiProvider provider, string expected)
        => ProviderDisplayNames.Of(provider).Should().Be(expected);

    [Fact]
    public void DisplayNames_AreDistinct()
        => Enum.GetValues<AiProvider>().Select(ProviderDisplayNames.Of).Should().OnlyHaveUniqueItems();
}
