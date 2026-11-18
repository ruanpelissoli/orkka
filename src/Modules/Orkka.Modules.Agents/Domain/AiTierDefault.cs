namespace Orkka.Modules.Agents.Domain;

using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Domain;

/// <summary>
/// Which model the SYSTEM (not the user) uses for a provider and tier: utility fallbacks, merge
/// resolution, agent seeding.
///
/// Its own table rather than a Tier column on <see cref="AiModel"/> because tiers are not
/// one-per-model — "gpt-5.5" is both Fast and Utility, "claude-sonnet-4-6" is both Utility and
/// Capable, and "ollama/gpt-oss:20b" is all three. With a unique model id, a single Tier column
/// would silently drop two tiers per provider.
///
/// ModelId is a plain string reference (no FK, matching the module's no-cross-schema-FK
/// convention); the write endpoints validate that it resolves.
/// </summary>
public sealed class AiTierDefault : BaseEntity
{
    public AiProvider Provider { get; set; }

    public AiModelTier Tier { get; set; }

    public required string ModelId { get; set; }
}
