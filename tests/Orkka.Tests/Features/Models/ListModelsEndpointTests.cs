namespace Orkka.Tests.Features.Models;

using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Ai;
using FluentAssertions;

/// <summary>
/// Invariants of the SHIPPED catalog (ModelCatalogSeed / ModelCatalogSnapshot.Static) — what the
/// seeder inserts into an empty database and what serves reads when the table is empty or the
/// database is unreachable. Admin edits are free to diverge from these at run time; the write
/// endpoints enforce the ones that must always hold.
/// </summary>
public sealed class ListModelsEndpointTests
{
    private static readonly ModelCatalogSnapshot Catalog = ModelCatalogSnapshot.Static;

    [Fact]
    public void Catalog_CoversAllProviders()
    {
        Catalog.All.Should().NotBeEmpty();

        // Every chat-capable provider must offer at least one selectable model — a new enum
        // value with no catalog entries would leave that provider unpickable everywhere.
        // PixelLab is the retired image-only vendor and has no chat models.
        Catalog.EnabledChat.Select(m => m.Provider).Distinct().Should()
            .BeEquivalentTo(Enum.GetValues<AiProvider>().Where(p => p != AiProvider.PixelLab));
    }

    [Fact]
    public void Catalog_Provider_AgreesWithModelIdPrefixDerivation()
    {
        // Provider routing derives from the model id — an entry whose tagged provider
        // disagrees with the prefix rule would route to the wrong CLI. Holds for retired and
        // image rows too, which is what the admin create/update validator enforces.
        Catalog.All.Should().AllSatisfy(m =>
            ModelProviders.FromModelId(m.ModelId).Should().Be(m.Provider));
    }

    [Fact]
    public void NonAnthropicModels_DoNotSupportMcp()
    {
        // QA drives Playwright MCP through the Claude Agent SDK; SupportsMcp=false keeps
        // QA agents on Claude models (enforced by Create/UpdateAgentEndpoint and the UI).
        Catalog.All.Where(m => m.Provider != AiProvider.Anthropic)
            .Should().OnlyContain(m => !m.SupportsMcp);
    }

    [Fact]
    public void AllModels_HaveNonEmptyFields()
    {
        Catalog.All.Should().AllSatisfy(m =>
        {
            m.ModelId.Should().NotBeNullOrWhiteSpace();
            m.DisplayName.Should().NotBeNullOrWhiteSpace();
            m.Description.Should().NotBeNullOrWhiteSpace();
        });
    }

    [Fact]
    public void AllModels_HaveUniqueIds()
    {
        Catalog.All.Select(m => m.ModelId).Should().OnlyHaveUniqueItems();
        Catalog.All.Select(m => m.SortOrder).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void SupportsMcp_MatchesCatalog_AndFailsClosedForUnknownModels()
    {
        foreach (var model in Catalog.All)
            Catalog.SupportsMcp(model.ModelId).Should().Be(model.SupportsMcp);

        Catalog.SupportsMcp("llama-3-local").Should().BeFalse();
        Catalog.SupportsMcp("").Should().BeFalse();
        Catalog.SupportsMcp(null).Should().BeFalse();
    }

    // ── Seed composition ────────────────────────────────────────────────────

    [Fact]
    public void EveryModelUsersCanPick_IsTheStaticModelCatalog()
    {
        // The offered set is unchanged by moving the catalog into the database.
        Catalog.EnabledChat.Select(m => m.ModelId)
            .Should().BeEquivalentTo(ModelCatalog.Models.Select(m => m.Id));
    }

    [Fact]
    public void EveryPricedModel_HasARow()
    {
        // A priced id with no row would be invisible to the admin UI, so a reprice could
        // never reach it. Driven off CostCalculator so a rate added in code can't be orphaned.
        var ids = Catalog.All.Select(m => m.ModelId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        CostCalculator.PricedModelIds.Should().OnlyContain(id => ids.Contains(id));
    }

    [Fact]
    public void RowsOutsideTheOfferedSet_AreSeededDisabled()
    {
        var offered = ModelCatalog.Models.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Catalog.All
            .Where(m => m.Kind == AiModelKind.Chat && !offered.Contains(m.ModelId))
            .Should().NotBeEmpty("CostCalculator still prices renamed/superseded ids")
            .And.OnlyContain(m => !m.IsEnabled);
    }

    [Fact]
    public void SeededRates_MatchTheShippedRateTable()
    {
        foreach (var model in Catalog.All)
        {
            var shipped = CostCalculator.TryGetBaseRates(model.ModelId);
            model.InputRatePer1M.Should().Be(shipped?.Input, model.ModelId);
            model.OutputRatePer1M.Should().Be(shipped?.Output, model.ModelId);
        }
    }

    [Fact]
    public void RunnerOnlyProviders_AreFlagged()
    {
        // None of these can run on Orkka's own infrastructure, so the hosted UI must hide
        // them: Copilot has no API-key auth path, Ollama is a server on the user's machine,
        // and OpenCode authenticates against the user's own per-provider logins. Grok is
        // NOT here — the grok CLI takes an XAI_API_KEY, so Orkka can serve it from its own
        // account; whether a given deployment does is XaiOptions.IsConfigured's call at
        // runtime, not a property of the catalog row.
        Catalog.All.Should().AllSatisfy(m => m.RequiresRunner.Should().Be(
            m.Provider is AiProvider.GitHubCopilot or AiProvider.Ollama or AiProvider.OpenCode,
            m.ModelId));
    }

    // ── Copilot catalog (docs.github.com supported-models, 2026-09) ─────────

    private static readonly string[] OfferedCopilotIds =
    [
        "copilot/auto",
        "copilot/gpt-5-mini", "copilot/gpt-5.3-codex", "copilot/gpt-5.4", "copilot/gpt-5.4-mini",
        "copilot/gpt-5.5", "copilot/gpt-5.6-luna", "copilot/gpt-5.6-sol", "copilot/gpt-5.6-terra",
        "copilot/claude-haiku-4.5", "copilot/claude-opus-4.8", "copilot/claude-opus-5",
        "copilot/claude-sonnet-4.6", "copilot/claude-sonnet-5",
        "copilot/gemini-3.6-flash", "copilot/gemini-3.7-flash",
    ];

    [Fact]
    public void CopilotCatalog_OffersExactlyTheSupportedModelsList()
    {
        // Pinned on purpose: the ids follow Copilot's dotted convention and were taken from
        // GitHub's docs, not captured from the CLI's /model picker. A drift here should be a
        // deliberate edit, not a side effect.
        Catalog.EnabledChat.Where(m => m.Provider == AiProvider.GitHubCopilot)
            .Select(m => m.ModelId).Should().BeEquivalentTo(OfferedCopilotIds);
    }

    [Fact]
    public void EveryCopilotRow_RequiresARunner_AndNeverSupportsMcp()
    {
        // Enabled AND retired rows alike: Copilot has no API-key auth path (RequiresRunner),
        // and QA must stay on Claude via the Agent SDK (SupportsMcp=false).
        var copilotRows = Catalog.All.Where(m => m.ModelId.StartsWith("copilot/")).ToList();

        copilotRows.Should().HaveCountGreaterThanOrEqualTo(OfferedCopilotIds.Length + 2);
        copilotRows.Should().AllSatisfy(m =>
        {
            m.Provider.Should().Be(AiProvider.GitHubCopilot, m.ModelId);
            m.RequiresRunner.Should().BeTrue(m.ModelId);
            m.SupportsMcp.Should().BeFalse(m.ModelId);
            m.Kind.Should().Be(AiModelKind.Chat, m.ModelId);
        });
    }

    [Theory]
    [InlineData("copilot/claude-sonnet-4.5")]
    [InlineData("copilot/gpt-5")]
    public void RetiredCopilotIds_StayResolvable_ButDisabled(string retiredId)
    {
        // GitHub dropped both from the supported list. An agent saved on either must still
        // resolve (Find spans disabled rows) but nobody can pick it fresh. These ids are not
        // in CostCalculator.PricedModelIds — the seed reaches them through RetiredDisplayNames.
        var row = Catalog.Find(retiredId);

        row.Should().NotBeNull(retiredId);
        row!.IsEnabled.Should().BeFalse();
        row.Provider.Should().Be(AiProvider.GitHubCopilot);
        row.RequiresRunner.Should().BeTrue();
        Catalog.IsSelectable(retiredId).Should().BeFalse();
        CostCalculator.PricedModelIds.Should().NotContain(retiredId);
    }

    [Fact]
    public void SubscriptionOnlyRows_AreSeededAtZeroRates_NotNull()
    {
        // Copilot/Ollama/OpenCode carry no per-token vendor charge. The row must say 0/0, not
        // null: null reads as "unpriced" and a DB-backed rate lookup would fall through to the
        // shipped table (which now also answers 0/0 — but the row is what the admin UI shows).
        var subscriptionRows = Catalog.All
            .Where(m => CostCalculator.IsSubscriptionOnly(m.ModelId)).ToList();

        subscriptionRows.Select(m => m.Provider).Distinct().Should().BeEquivalentTo(
            [AiProvider.GitHubCopilot, AiProvider.Ollama, AiProvider.OpenCode]);
        subscriptionRows.Should().AllSatisfy(m =>
        {
            m.InputRatePer1M.Should().Be(0m, m.ModelId);
            m.OutputRatePer1M.Should().Be(0m, m.ModelId);
        });
    }

    [Fact]
    public void CopilotTierDefaults_PointAtOfferedCopilotRows()
    {
        Catalog.FastModel(AiProvider.GitHubCopilot).Should().Be("copilot/auto");
        Catalog.UtilityModel(AiProvider.GitHubCopilot).Should().Be("copilot/claude-sonnet-5");
        Catalog.CapableModel(AiProvider.GitHubCopilot).Should().Be("copilot/claude-sonnet-5");

        foreach (var tier in new[] { AiModelTier.Fast, AiModelTier.Utility, AiModelTier.Capable })
            Catalog.IsSelectable(Catalog.TierModel(AiProvider.GitHubCopilot, tier))
                .Should().BeTrue($"the {tier} default must be a live, enabled row");
    }

    [Fact]
    public void ShippedCatalog_HasNoImageModels()
    {
        // Orkka generates no images server-side, so nothing seeds an image row or an image
        // provider key. Image ids that are still priced (historical usage) are retired rows.
        Catalog.All.Should().NotContain(m => m.Kind == AiModelKind.Image);
        Catalog.All.Should().AllSatisfy(m => m.ImageProviderKey.Should().BeNull());
        Catalog.EnabledChat.Should().NotContain(m => m.ModelId.Contains("image"));
    }
}
