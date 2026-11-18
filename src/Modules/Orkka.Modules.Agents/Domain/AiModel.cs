namespace Orkka.Modules.Agents.Domain;

using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Domain;

/// <summary>
/// A selectable AI model, managed by platform admins so models can be added, repriced, enabled
/// and disabled without a redeploy. NOT a TenantEntity — the catalog belongs to the platform,
/// not to any org (like InfraCostItem and PlatformSetting).
///
/// Disabling is not deleting: a disabled row stays resolvable, so agents already pinned to it
/// keep running and keep billing at their own rate. Only new picks are gated.
/// </summary>
public sealed class AiModel : BaseEntity
{
    /// <summary>The vendor id the CLI/API is called with, e.g. "claude-opus-5",
    /// "copilot/auto", "ollama/gpt-oss:20b". Unique; never used as a URL path segment because
    /// real ids contain "/" and ":".</summary>
    public required string ModelId { get; set; }

    public required string DisplayName { get; set; }

    public AiProvider Provider { get; set; }

    public AiModelKind Kind { get; set; }

    public string Description { get; set; } = "";

    /// <summary>Whether the model can drive Playwright MCP browser tools — gates the QA role.</summary>
    public bool SupportsMcp { get; set; }

    /// <summary>Offered to users. False = retired/not offered, but still priced and resolvable.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Only runs through a vendor CLI on a connected runner (subscription auth — no
    /// API key Orkka can hold), so the hosted UI must not offer it.</summary>
    public bool RequiresRunner { get; set; }

    /// <summary>Image rows only: the provider key the image router accepts as a pin
    /// ("gemini" | "openai" | "pixellab"). The agent still stores the key, not the model id.</summary>
    public string? ImageProviderKey { get; set; }

    /// <summary>Vendor cost per 1M input tokens. Null = fall through to CostCalculator's
    /// shipped table (and then to the unknown-model rate).</summary>
    public decimal? InputRatePer1M { get; set; }

    /// <summary>Vendor cost per 1M output tokens. Null = fall through as above.</summary>
    public decimal? OutputRatePer1M { get; set; }

    /// <summary>Who owns <see cref="InputRatePer1M"/>/<see cref="OutputRatePer1M"/>. The
    /// vendor sync rewrites Seed and Vendor rows from the price feed and never touches an
    /// Admin row — that is the whole provenance rule, so an admin reprice survives every
    /// sweep until they "Reset to vendor".</summary>
    public AiModelRateSource RateSource { get; set; } = AiModelRateSource.Seed;

    /// <summary>The vendor's list price as last reported by the price feed — kept even when an
    /// admin overrides the billed rate, so the UI can show the gap and offer a reset.</summary>
    public decimal? VendorInputRatePer1M { get; set; }
    public decimal? VendorOutputRatePer1M { get; set; }

    /// <summary>Absolute vendor cache rates per 1M tokens (a cache hit and a cache write).
    /// Null = CostCalculator's per-model/per-vendor multiplier tables decide.</summary>
    public decimal? CacheReadRatePer1M { get; set; }
    public decimal? CacheWriteRatePer1M { get; set; }

    public int? ContextWindow { get; set; }
    public int? MaxOutputTokens { get; set; }

    public DateOnly? ReleaseDate { get; set; }

    /// <summary>The feed's status verbatim ("deprecated", "beta", …); null = current.</summary>
    public string? VendorStatus { get; set; }

    /// <summary>Whether the vendor's own models API listed this id on the last successful
    /// probe. Null = that provider has not been probed (no key, or probe failed).</summary>
    public bool? VendorListed { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>How the row came to exist: shipped seed, vendor sync, or an admin.</summary>
    public AiModelOrigin Origin { get; set; } = AiModelOrigin.Seed;

    /// <summary>Display order within a provider group.</summary>
    public int SortOrder { get; set; }

    /// <summary>Seeded by the platform. Blocks hard delete — retiring one means disabling it,
    /// so historical usage keeps billing at its own rate.</summary>
    public bool IsBuiltIn { get; set; }

    public string? Note { get; set; }
}
