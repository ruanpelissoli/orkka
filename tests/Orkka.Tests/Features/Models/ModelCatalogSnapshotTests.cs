namespace Orkka.Tests.Features.Models;

using FluentAssertions;
using Orkka.Modules.Agents.Contracts;

/// <summary>
/// Lookup semantics of the snapshot every consumer reads through. The distinction that matters:
/// <c>Find</c>/<c>SupportsMcp</c> span DISABLED rows so agents already pinned to a retired model
/// keep working and keep billing, while <c>IsSelectable</c> gates only NEW picks.
/// </summary>
public sealed class ModelCatalogSnapshotTests
{
    private static ModelCatalogSnapshot Build(params AiModelEntry[] entries) =>
        new(entries, new Dictionary<(AiProvider, AiModelTier), string>());

    private static AiModelEntry Entry(
        string id, AiProvider provider = AiProvider.Anthropic,
        AiModelKind kind = AiModelKind.Chat, bool enabled = true, bool mcp = true,
        string? imageKey = null, decimal? input = null, decimal? output = null) =>
        new(id, id, provider, kind, "desc", mcp, enabled, false, imageKey, input, output, 0);

    [Fact]
    public void Find_ResolvesDisabledRows_CaseInsensitively()
    {
        var catalog = Build(Entry("claude-opus-4-1", enabled: false, input: 15m, output: 75m));

        catalog.Find("claude-opus-4-1").Should().NotBeNull();
        catalog.Find("CLAUDE-OPUS-4-1")!.InputRatePer1M.Should().Be(15m);
        catalog.Find("nope").Should().BeNull();
        catalog.Find("").Should().BeNull();
        catalog.Find(null).Should().BeNull();
    }

    [Fact]
    public void SupportsMcp_FailsClosed_ForUnknownIds_ButNotForDisabledOnes()
    {
        var catalog = Build(
            Entry("claude-sonnet-4-5", enabled: false, mcp: true),
            Entry("gpt-5.5", AiProvider.OpenAI, mcp: false));

        // A QA agent pinned to a retired Claude model must still be savable.
        catalog.SupportsMcp("claude-sonnet-4-5").Should().BeTrue();
        catalog.SupportsMcp("gpt-5.5").Should().BeFalse();
        catalog.SupportsMcp("who-knows").Should().BeFalse();
    }

    [Fact]
    public void IsSelectable_RequiresAnEnabledChatRow()
    {
        var catalog = Build(
            Entry("claude-opus-5"),
            Entry("claude-opus-4-1", enabled: false),
            Entry("gpt-image-1", AiProvider.OpenAI, AiModelKind.Image, imageKey: "openai"));

        catalog.IsSelectable("claude-opus-5").Should().BeTrue();
        catalog.IsSelectable("CLAUDE-OPUS-5").Should().BeTrue();
        catalog.IsSelectable("claude-opus-4-1").Should().BeFalse("retired models are not offered");
        catalog.IsSelectable("gpt-image-1").Should().BeFalse("image models are not chat models");
        catalog.IsSelectable("claude-opus-99").Should().BeFalse();
        catalog.IsSelectable("").Should().BeFalse();
    }

    [Fact]
    public void IsSelectable_AcceptsAnyOllamaNamespacedId()
    {
        // Locally-pulled tags are dynamic — the runner reports whatever is installed and the
        // catalog only anchors a few.
        var catalog = Build(Entry("ollama/gpt-oss:20b", AiProvider.Ollama));

        catalog.IsSelectable("ollama/qwen3-coder:14b-instruct").Should().BeTrue();
        catalog.IsSelectable("ollama/anything-at-all").Should().BeTrue();
        catalog.IsSelectable("qwen3-coder").Should().BeFalse("the namespace is what makes it safe");
    }

    [Fact]
    public void IsSelectable_AcceptsAnyOpenCodeNamespacedId()
    {
        // OpenCode ships no models at all: what is selectable depends entirely on which
        // providers the user ran `opencode auth login` for, which only the runner knows.
        var catalog = Build(Entry("opencode/anthropic/claude-sonnet-4-6", AiProvider.OpenCode));

        catalog.IsSelectable("opencode/deepseek/deepseek-v3").Should().BeTrue();
        catalog.IsSelectable("opencode/openrouter/anything").Should().BeTrue();
        // The bare vendor id is NOT selectable through this rule — it would route to that
        // vendor's own CLI, which is a different run entirely.
        catalog.IsSelectable("deepseek-v3").Should().BeFalse("the namespace is what makes it safe");
    }

    [Fact]
    public void TierModel_UsesTheDbDesignation_WhenPresent()
    {
        var catalog = new ModelCatalogSnapshot(
            [Entry("claude-opus-5")],
            new Dictionary<(AiProvider, AiModelTier), string>
            {
                [(AiProvider.Anthropic, AiModelTier.Fast)] = "claude-opus-5",
            });

        catalog.FastModel(AiProvider.Anthropic).Should().Be("claude-opus-5");
    }

    [Fact]
    public void TierModel_FallsBackToShippedDefaults_ForAnUndesignatedPair()
    {
        var catalog = Build(Entry("claude-opus-5"));

        foreach (var provider in Enum.GetValues<AiProvider>())
        {
            catalog.FastModel(provider).Should().Be(ProviderModelDefaults.FastModel(provider));
            catalog.UtilityModel(provider).Should().Be(ProviderModelDefaults.UtilityModel(provider));
            catalog.CapableModel(provider).Should().Be(ProviderModelDefaults.CapableModel(provider));
        }

        catalog.QaModel.Should().Be(ProviderModelDefaults.QaModel);
    }

    [Fact]
    public void QaModel_IsProviderIndependent()
    {
        // QA drives Playwright MCP through the Claude Agent SDK, so the single Anthropic QA
        // designation applies whatever provider the rest of the pod runs on.
        var catalog = new ModelCatalogSnapshot(
            [Entry("claude-sonnet-5")],
            new Dictionary<(AiProvider, AiModelTier), string>
            {
                [(AiProvider.Anthropic, AiModelTier.Qa)] = "claude-sonnet-5",
            });

        catalog.TierModel(AiProvider.Ollama, AiModelTier.Qa).Should().Be("claude-sonnet-5");
        catalog.QaModel.Should().Be("claude-sonnet-5");
    }

    [Fact]
    public void EnabledChat_ExcludesImageAndRetiredRows()
    {
        var catalog = Build(
            Entry("claude-opus-5"),
            Entry("claude-opus-4-1", enabled: false),
            Entry("gpt-image-1", AiProvider.OpenAI, AiModelKind.Image, imageKey: "openai"));

        catalog.EnabledChat.Select(m => m.ModelId).Should().BeEquivalentTo(["claude-opus-5"]);
        catalog.All.Should().HaveCount(3, "All spans every row regardless of state");
    }
}
