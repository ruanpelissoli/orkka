namespace Orkka.Tests.Features.Models;

using FluentAssertions;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Execution.Orchestration;
using Orkka.SharedKernel.Ai;

/// <summary>Configurable fake for provider-selection logic tests.</summary>
file sealed class FakeReadiness(params AiProvider[] connected) : IProviderReadiness
{
    public string? GetBlocker(AiProvider provider) =>
        connected.Contains(provider) ? null : $"{provider} not connected";

    public IReadOnlyList<AiProvider> Connected { get; } = connected;

    public string? GetAnyProviderBlocker() =>
        connected.Length > 0 ? null : "no provider connected";
}

public sealed class ProviderReadinessTests
{
    /// <summary>The shipped catalog — what these selection paths resolve against when the
    /// database has no admin overrides (a fresh install, and every host with no DB).</summary>
    private static readonly ModelCatalogSnapshot Catalog = ModelCatalogSnapshot.Static;

    // ── Host default: no machine in view ────────────────────────────────────

    [Fact]
    public void AlwaysReady_NeverBlocks_AndDefaultsToClaude()
    {
        // The developer's runner decides at run time which CLIs it can use; the host never
        // blocks, and a seeded agent defaults to Claude whatever keys Orkka itself holds.
        var readiness = new AlwaysReadyProviderReadiness();

        foreach (var provider in Enum.GetValues<AiProvider>())
            readiness.GetBlocker(provider).Should().BeNull();

        readiness.GetAnyProviderBlocker().Should().BeNull();
        readiness.Connected.Should().Equal(AiProvider.Anthropic);
        readiness.PreferredProvider().Should().Be(AiProvider.Anthropic);
    }

    // ── PreferredProvider fallback order ────────────────────────────────────

    [Fact]
    public void PreferredProvider_UsesAnthropic_WhenConnected()
        => new FakeReadiness(AiProvider.Anthropic, AiProvider.OpenAI)
            .PreferredProvider().Should().Be(AiProvider.Anthropic);

    [Fact]
    public void PreferredProvider_FallsBackToFirstConnected_WhenClaudeBlocked()
        => new FakeReadiness(AiProvider.OpenAI, AiProvider.Google)
            .PreferredProvider().Should().Be(AiProvider.OpenAI);

    [Fact]
    public void PreferredProvider_PassesThroughAnthropic_WhenNothingConnected()
        // Callers' own pre-flights surface the blocker; the passthrough keeps
        // today's error paths intact instead of inventing a new failure mode.
        => new FakeReadiness().PreferredProvider().Should().Be(AiProvider.Anthropic);

    // ── ProviderModelDefaults ↔ ModelCatalog consistency ────────────────────

    [Fact]
    public void ProviderModelDefaults_AllIdsExistInCatalog_WithMatchingProvider()
    {
        // PixelLab is image-only — it has no chat models and no tier defaults.
        foreach (var provider in Enum.GetValues<AiProvider>().Where(p => p != AiProvider.PixelLab))
        {
            foreach (var id in new[]
            {
                ProviderModelDefaults.FastModel(provider),
                ProviderModelDefaults.UtilityModel(provider),
                ProviderModelDefaults.CapableModel(provider),
            })
            {
                var entry = ModelCatalog.Models.FirstOrDefault(m => m.Id == id);
                entry.Should().NotBeNull($"'{id}' ({provider}) must exist in ModelCatalog");
                entry!.Provider.Should().Be(provider, $"'{id}' must belong to {provider}");
            }
        }
    }

    // ── Merge-resolver selection ────────────────────────────────────────────

    [Fact]
    public void SelectMergeResolver_ClaudeConnected_UsesAnthropicCapableModel()
        => TaskOrchestrator.SelectMergeResolver(new FakeReadiness(AiProvider.Anthropic), Catalog)
            .Should().Be((AiProvider.Anthropic, ProviderModelDefaults.CapableModel(AiProvider.Anthropic)));

    [Fact]
    public void SelectMergeResolver_CodexOnly_UsesOpenAiCodexModel()
        => TaskOrchestrator.SelectMergeResolver(new FakeReadiness(AiProvider.OpenAI), Catalog)
            .Should().Be((AiProvider.OpenAI, "gpt-5.6-sol"));

    [Fact]
    public void SelectMergeResolver_GeminiOnly_UsesGeminiPro()
        => TaskOrchestrator.SelectMergeResolver(new FakeReadiness(AiProvider.Google), Catalog)
            .Should().Be((AiProvider.Google, "gemini-3.1-pro-preview"));

    [Fact]
    public void SelectMergeResolver_CopilotOnly_UsesCopilotCapableModel()
        => TaskOrchestrator.SelectMergeResolver(new FakeReadiness(AiProvider.GitHubCopilot), Catalog)
            .Should().Be((AiProvider.GitHubCopilot, "copilot/auto"));

    [Fact]
    public void SelectMergeResolver_OllamaOnly_UsesOllamaCapableModel()
        => TaskOrchestrator.SelectMergeResolver(new FakeReadiness(AiProvider.Ollama), Catalog)
            .Should().Be((AiProvider.Ollama, "ollama/gpt-oss:20b"));

    [Fact]
    public void SelectMergeResolver_GrokOnly_UsesGrokCapableModel()
        => TaskOrchestrator.SelectMergeResolver(new FakeReadiness(AiProvider.XAI), Catalog)
            .Should().Be((AiProvider.XAI, "grok-4.6"));

    [Fact]
    public void SelectMergeResolver_OpenCodeOnly_UsesTheHarnessAnchor()
        // OpenCode ships no models, so every tier resolves to the same anchor — which vendor
        // is actually reachable is a property of the user's `opencode auth login` state and
        // only the runner knows it.
        => TaskOrchestrator.SelectMergeResolver(new FakeReadiness(AiProvider.OpenCode), Catalog)
            .Should().Be((AiProvider.OpenCode, "opencode/anthropic/claude-sonnet-4-6"));

    [Fact]
    public void SelectMergeResolver_NothingConnected_PassesThroughAnthropic()
        => TaskOrchestrator.SelectMergeResolver(new FakeReadiness(), Catalog)
            .Should().Be((AiProvider.Anthropic, ProviderModelDefaults.CapableModel(AiProvider.Anthropic)));

    // ── Agent seeding ───────────────────────────────────────────────────────

    [Fact]
    public void CreateDefaultPod_DefaultProvider_YieldsExactlyTodaysModels()
    {
        // Hosted byte-identical guard: the parameterless behavior must never drift.
        var pod = AgentSeeder.CreateDefaultPod(Ulid.NewUlid(), Ulid.NewUlid(), Catalog);

        pod.Single(a => a.Role == AgentRole.PM).Model.Should().Be("claude-haiku-4-5");
        // QA stays on the provider-independent QaModel (Playwright MCP needs Claude, and the
        // browser lane does not need the strongest coder); everyone else is the Capable tier.
        pod.Single(a => a.Role == AgentRole.QA).Model.Should().Be(ProviderModelDefaults.QaModel);
        pod.Where(a => a.Role is not AgentRole.PM and not AgentRole.QA)
            .Should().AllSatisfy(a => a.Model.Should().Be(ProviderModelDefaults.CapableModel(AiProvider.Anthropic)));
    }

    [Theory]
    [InlineData(AiProvider.OpenAI, "gpt-5.6-sol", "gpt-5.6-sol")]
    [InlineData(AiProvider.Google, "gemini-3.6-flash", "gemini-3.1-pro-preview")]
    [InlineData(AiProvider.GitHubCopilot, "copilot/auto", "copilot/auto")]
    [InlineData(AiProvider.Ollama, "ollama/gpt-oss:20b", "ollama/gpt-oss:20b")]
    [InlineData(AiProvider.XAI, "grok-4.6", "grok-4.6")]
    [InlineData(AiProvider.OpenCode,
        "opencode/anthropic/claude-sonnet-4-6", "opencode/anthropic/claude-sonnet-4-6")]
    public void CreateDefaultPod_NonAnthropicProvider_SeedsTierModels_ExceptQa(
        AiProvider provider, string fast, string capable)
    {
        var pod = AgentSeeder.CreateDefaultPod(
            Ulid.NewUlid(), Ulid.NewUlid(), Catalog, provider);

        pod.Single(a => a.Role == AgentRole.PM).Model.Should().Be(fast);
        // QA always stays on a Claude model — only MCP-capable models drive Playwright.
        pod.Single(a => a.Role == AgentRole.QA).Model.Should().Be(ProviderModelDefaults.QaModel);
        pod.Where(a => a.Role is not AgentRole.PM and not AgentRole.QA)
            .Should().AllSatisfy(a => a.Model.Should().Be(capable));
    }
}
