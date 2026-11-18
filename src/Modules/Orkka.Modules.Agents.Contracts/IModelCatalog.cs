namespace Orkka.Modules.Agents.Contracts;

/// <summary>One row of the model catalog. Rates are per 1M tokens and null when the row has no
/// admin-set price (billing then falls through to CostCalculator's shipped table). The cache
/// rates are absolute vendor prices per 1M tokens, written by the vendor sync; null means
/// CostCalculator's multiplier tables decide. Trailing optionals so the many call sites and
/// cached payloads written before the sync existed keep working unchanged.</summary>
public sealed record AiModelEntry(
    string ModelId,
    string DisplayName,
    AiProvider Provider,
    AiModelKind Kind,
    string Description,
    bool SupportsMcp,
    bool IsEnabled,
    bool RequiresRunner,
    string? ImageProviderKey,
    decimal? InputRatePer1M,
    decimal? OutputRatePer1M,
    int SortOrder,
    decimal? CacheReadRatePer1M = null,
    decimal? CacheWriteRatePer1M = null);

/// <summary>One provider/tier designation — the model the SYSTEM picks for that slot.</summary>
public sealed record AiTierDefaultEntry(AiProvider Provider, AiModelTier Tier, string ModelId);

/// <summary>
/// The catalog in the shape that gets cached, and the only shape that may be.
/// </summary>
/// <remarks>
/// <see cref="ModelCatalogSnapshot"/> is deliberately NOT cached directly: its constructor
/// parameters bind to no property of the same name, and its tier map is keyed by a tuple, so
/// System.Text.Json cannot rebuild it. That matters because HybridCache round-trips a mutable
/// payload through the serializer on <i>every</i> read, in-process hits included — so a
/// snapshot in the cache threw on the way out and the read degraded silently to the shipped
/// catalog, which is precisely the bug this record exists to prevent.
/// </remarks>
public sealed record ModelCatalogPayload(
    IReadOnlyList<AiModelEntry> Models,
    IReadOnlyList<AiTierDefaultEntry> TierDefaults)
{
    public ModelCatalogSnapshot ToSnapshot() =>
        new(Models, TierDefaults.ToDictionary(t => (t.Provider, t.Tier), t => t.ModelId));
}

/// <summary>
/// An immutable read of the whole catalog. A snapshot rather than a bag of async lookups
/// because several consumers need two or three answers at once, and two of them
/// (AgentSeeder, TaskOrchestrator.SelectMergeResolver) are static helpers with direct unit
/// tests — they take a snapshot parameter and stay static and pure.
/// </summary>
public sealed class ModelCatalogSnapshot
{
    private readonly Dictionary<string, AiModelEntry> _byId;
    private readonly IReadOnlyDictionary<(AiProvider Provider, AiModelTier Tier), string> _tiers;

    public ModelCatalogSnapshot(
        IReadOnlyList<AiModelEntry> entries,
        IReadOnlyDictionary<(AiProvider Provider, AiModelTier Tier), string> tierDefaults)
    {
        All = entries;
        _tiers = tierDefaults;
        // Last write wins on a duplicate id; the unique index makes that unreachable from the
        // DB, and the seed is asserted unique by test.
        _byId = new Dictionary<string, AiModelEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
            _byId[e.ModelId] = e;
    }

    /// <summary>Every row, enabled or not, both kinds.</summary>
    public IReadOnlyList<AiModelEntry> All { get; }

    /// <summary>What a user may pick for an agent or a task, in display order.</summary>
    public IEnumerable<AiModelEntry> EnabledChat =>
        All.Where(m => m is { IsEnabled: true, Kind: AiModelKind.Chat });

    /// <summary>Looks up a row INCLUDING disabled ones — a disabled model must still resolve
    /// its rates and its MCP capability, so agents already pinned to it keep working and keep
    /// billing correctly. Only new picks are gated (see <see cref="IsSelectable"/>).</summary>
    public AiModelEntry? Find(string? modelId) =>
        modelId is { Length: > 0 } id && _byId.TryGetValue(id, out var entry) ? entry : null;

    /// <summary>Whether a model can drive Playwright MCP browser tools — the QA-role gate.
    /// Unknown ids fail closed.</summary>
    public bool SupportsMcp(string? modelId) => Find(modelId)?.SupportsMcp == true;

    /// <summary>Namespaces whose members are discovered on the runner rather than listed in
    /// the catalog, so the prefix alone makes an id selectable. Ollama reports the tags
    /// actually pulled; OpenCode reports whatever its `opencode auth login` providers expose
    /// (it ships no models of its own). Either list is a property of the user's machine, and
    /// the catalog can only ever anchor a few of them.</summary>
    private static readonly string[] RunnerDiscoveredNamespaces = ["ollama/", "opencode/"];

    /// <summary>Whether a model id may be pinned by a user: a catalog row that is enabled and
    /// a chat model, or any id under a runner-discovered namespace.</summary>
    public bool IsSelectable(string? modelId) =>
        modelId is { Length: > 0 } id
        && (Find(id) is { IsEnabled: true, Kind: AiModelKind.Chat }
            || RunnerDiscoveredNamespaces.Any(ns => id.StartsWith(ns, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The model the SYSTEM (not the user) uses for a provider and tier. Falls back to
    /// the shipped <see cref="ProviderModelDefaults"/> when no row designates one.</summary>
    public string TierModel(AiProvider provider, AiModelTier tier)
    {
        // QA is provider-independent by design, so a project on any provider resolves to the
        // single Anthropic QA row.
        var key = tier == AiModelTier.Qa ? (AiProvider.Anthropic, tier) : (provider, tier);
        return _tiers.TryGetValue(key, out var id) && !string.IsNullOrWhiteSpace(id)
            ? id
            : ProviderModelDefaults.ForTier(provider, tier);
    }

    public string FastModel(AiProvider provider) => TierModel(provider, AiModelTier.Fast);
    public string UtilityModel(AiProvider provider) => TierModel(provider, AiModelTier.Utility);
    public string CapableModel(AiProvider provider) => TierModel(provider, AiModelTier.Capable);
    public string QaModel => TierModel(AiProvider.Anthropic, AiModelTier.Qa);

    private static ModelCatalogSnapshot? _static;

    /// <summary>The shipped catalog. Used by processes with no database (the local runner CLI),
    /// as the degraded read when the table is empty or unreachable, and by unit tests.</summary>
    public static ModelCatalogSnapshot Static =>
        _static ??= new ModelCatalogSnapshot(ModelCatalogSeed.Entries, ModelCatalogSeed.TierDefaults);
}

/// <summary>Reads the admin-managed model catalog (cached; see DbModelCatalog).</summary>
public interface IModelCatalog
{
    ValueTask<ModelCatalogSnapshot> GetAsync(CancellationToken ct = default);
}
