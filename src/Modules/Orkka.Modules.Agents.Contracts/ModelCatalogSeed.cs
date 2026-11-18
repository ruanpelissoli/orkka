namespace Orkka.Modules.Agents.Contracts;

using Orkka.SharedKernel.Ai;

/// <summary>
/// The shipped model catalog: what AiModelSeeder inserts into an empty database, and what
/// <see cref="ModelCatalogSnapshot.Static"/> serves when no rows exist yet (fresh deploy before
/// the seeder runs) or when the DB is unreachable. Composed from the existing statics —
/// <see cref="ModelCatalog"/>, <see cref="ProviderModelDefaults"/> and
/// <see cref="CostCalculator"/> — so there is still exactly one place to add a model in code.
/// </summary>
public static class ModelCatalogSeed
{
    /// <summary>
    /// Every model runs on the developer's runner; these are the providers that ONLY exist
    /// there (no vendor model API to list them), so the picker shows their models only when
    /// the developer's runner reports them.</summary>
    public static bool ProviderRequiresRunner(AiProvider provider) =>
        provider is AiProvider.GitHubCopilot or AiProvider.Ollama or AiProvider.OpenCode;

    /// <summary>Models that <see cref="CostCalculator"/> still prices but that are no longer
    /// offered: renamed ids, superseded versions, and vendor aliases recorded by agents saved
    /// before a rename. Seeded DISABLED — not selectable, still billable, still resolvable.
    ///
    /// Subscription-only ids (<see cref="CostCalculator.IsSubscriptionOnly"/>) are listed here
    /// too even though the rate table never prices them: the retired-row loop in
    /// <see cref="BuildEntries"/> unions this dictionary with the priced ids precisely so a
    /// retired Copilot model still gets a disabled, resolvable, 0/0 row.</summary>
    private static readonly Dictionary<string, string> RetiredDisplayNames = new()
    {
        // Retired by GitHub, not by us: the Copilot supported-models list (2026-09) no longer
        // carries either. Agents saved on them keep resolving; the migration
        // RefreshCopilotCatalog disables the rows that were seeded enabled.
        ["copilot/claude-sonnet-4.5"] = "Copilot Claude Sonnet 4.5 (retired)",
        ["copilot/gpt-5"] = "Copilot GPT-5 (retired)",
        // Not retired — priced but deliberately not offered (see CostCalculator).
        ["claude-fable-5-1"] = "Claude Fable 5.1",
        ["claude-opus-4-7"] = "Claude Opus 4.7",
        ["claude-opus-4-5"] = "Claude Opus 4.5",
        ["claude-opus-4-1"] = "Claude Opus 4.1",
        ["claude-opus-4"] = "Claude Opus 4",
        ["claude-sonnet-4-5"] = "Claude Sonnet 4.5",
        ["claude-sonnet-4"] = "Claude Sonnet 4",
        // Retired by Google, not by us: gemini-3-pro-preview was shut down 2026-03-09 and
        // gemini-3-flash-preview is deprecated behind it. The bare ids were always Code
        // Assist aliases. All four still bill (and now run) as their 3.1/3.6 replacements.
        ["gemini-3-pro-preview"] = "Gemini 3 Pro (shut down)",
        ["gemini-3-flash-preview"] = "Gemini 3 Flash (deprecated)",
        ["gemini-3-pro"] = "Gemini 3 Pro (alias)",
        ["gemini-3-flash"] = "Gemini 3 Flash (alias)",
        ["gpt-4o"] = "GPT-4o",
        ["gpt-4o-mini"] = "GPT-4o mini",
        ["gpt-5"] = "GPT-5",
        ["gpt-5.5"] = "GPT-5.5",
        ["gpt-5.5-codex"] = "GPT-5.5 Codex",
        ["gpt-5.6"] = "GPT-5.6 (alias)",
    };

    private static IReadOnlyList<AiModelEntry>? _entries;
    private static IReadOnlyDictionary<(AiProvider, AiModelTier), string>? _tierDefaults;

    public static IReadOnlyList<AiModelEntry> Entries => _entries ??= BuildEntries();

    public static IReadOnlyDictionary<(AiProvider Provider, AiModelTier Tier), string> TierDefaults =>
        _tierDefaults ??= BuildTierDefaults();

    private static List<AiModelEntry> BuildEntries()
    {
        var entries = new List<AiModelEntry>();
        var sort = 0;

        foreach (var m in ModelCatalog.Models)
        {
            entries.Add(new AiModelEntry(
                ModelId: m.Id,
                DisplayName: m.Name,
                Provider: m.Provider,
                Kind: AiModelKind.Chat,
                Description: m.Description,
                SupportsMcp: m.SupportsMcp,
                IsEnabled: true,
                RequiresRunner: ProviderRequiresRunner(m.Provider),
                ImageProviderKey: null,
                InputRatePer1M: CostCalculator.TryGetBaseRates(m.Id)?.Input,
                OutputRatePer1M: CostCalculator.TryGetBaseRates(m.Id)?.Output,
                SortOrder: sort += 10));
        }

        // Everything the rate table prices that isn't offered above. Driven off
        // CostCalculator.PricedModelIds rather than a hand-kept list, so a rate added in code
        // can never end up without a row (and so billing an unknown id at $15/$75 stays a
        // genuine unknown rather than a bookkeeping gap). SupportsMcp stays false to preserve
        // today's fail-closed behavior for ids that were never in the catalog.
        //
        // Unioned with RetiredDisplayNames: subscription-only ids (copilot/…) are never in the
        // rate table, so a retired Copilot model would otherwise lose its row entirely and an
        // agent still pinned to it would fail to resolve.
        var offered = entries.Select(e => e.ModelId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retired = CostCalculator.PricedModelIds
            .Concat(RetiredDisplayNames.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => !offered.Contains(id));
        foreach (var id in retired)
        {
            var rates = CostCalculator.TryGetBaseRates(id);
            var provider = ModelProviders.FromModelId(id);
            entries.Add(new AiModelEntry(
                ModelId: id,
                DisplayName: RetiredDisplayNames.GetValueOrDefault(id, id),
                Provider: provider,
                Kind: AiModelKind.Chat,
                Description: CostCalculator.IsSubscriptionOnly(id)
                    ? "Retired from the picker — kept so agents saved on this id still resolve (subscription-billed, no per-token charge)."
                    : "Not offered in the picker — priced so usage under this id bills at its own rate.",
                SupportsMcp: false,
                IsEnabled: false,
                RequiresRunner: ProviderRequiresRunner(provider),
                ImageProviderKey: null,
                InputRatePer1M: rates?.Input,
                OutputRatePer1M: rates?.Output,
                SortOrder: sort += 10));
        }

        return entries;
    }

    private static Dictionary<(AiProvider, AiModelTier), string> BuildTierDefaults()
    {
        var providers = Enum.GetValues<AiProvider>().Where(p => p != AiProvider.PixelLab);
        var defaults = new Dictionary<(AiProvider, AiModelTier), string>();

        foreach (var provider in providers)
        {
            defaults[(provider, AiModelTier.Fast)] = ProviderModelDefaults.FastModel(provider);
            defaults[(provider, AiModelTier.Utility)] = ProviderModelDefaults.UtilityModel(provider);
            defaults[(provider, AiModelTier.Capable)] = ProviderModelDefaults.CapableModel(provider);
        }

        // QA is provider-independent — one row, always Claude.
        defaults[(AiProvider.Anthropic, AiModelTier.Qa)] = ProviderModelDefaults.QaModel;
        return defaults;
    }
}
