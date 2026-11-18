namespace Orkka.Modules.Agents.Domain;

using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Domain;

/// <summary>
/// The project's visual identity, decided once and reused everywhere.
///
/// Before this existed there was no such record: the Designer invented colours inside the SVGs
/// it authored, and the managed scaffold independently invented a different set into
/// globals.css — two model calls reading the same brief and agreeing only by luck. Persisting
/// the decision is what lets the stylesheet, the brand assets, and every later asset share one
/// answer, and what lets a human correct that answer once instead of per asset.
///
/// One row per project, enforced by a unique index on ProjectId.
/// </summary>
public sealed class BrandKit : TenantEntity
{
    // Cross-module reference (Projects module) — plain id, no navigation.
    public Ulid ProjectId { get; set; }

    /// <summary>Serialized <see cref="BrandPalette"/>. JSON rather than nine columns: the
    /// token set moves with the scaffold's stylesheet, and adding one should not be a
    /// migration.</summary>
    public required string PaletteJson { get; set; }

    public string? DisplayFont { get; set; }
    public string? BodyFont { get; set; }

    /// <summary>One short line on the visual character. Carries what a palette cannot.</summary>
    public string? Tone { get; set; }

    public BrandKitSource Source { get; set; } = BrandKitSource.Brief;

    /// <summary>Set when a human edits it. A kit with this set is never regenerated —
    /// re-deriving over someone's correction is how a tool loses their trust.</summary>
    public string? UpdatedByUserId { get; set; }
}
