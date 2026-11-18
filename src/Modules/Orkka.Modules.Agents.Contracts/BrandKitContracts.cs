namespace Orkka.Modules.Agents.Contracts;

/// <summary>
/// The project's colours, named to match the CSS custom properties the scaffold writes into
/// globals.css. Keeping the names identical is the point: the same nine values reach the
/// stylesheet, the Designer's SVGs, and every asset generated afterwards.
/// </summary>
public sealed record BrandPalette(
    string Primary,
    string PrimaryHover,
    string Secondary,
    string Accent,
    string Background,
    string Surface,
    string Text,
    string TextMuted,
    string Border);

/// <summary>Where a brand kit's values came from — worth knowing before overwriting them.</summary>
public enum BrandKitSource
{
    /// <summary>Invented from the project brief, for a project with no code yet.</summary>
    Brief,

    /// <summary>Read out of an existing codebase's stylesheet or theme config.</summary>
    Repository,

    /// <summary>Corrected by a human on the Design page. Never overwritten automatically.</summary>
    Manual,

    /// <summary>Chosen by the user from the design-briefing options. A deliberate human pick —
    /// like Manual, derivation never overwrites it.</summary>
    DesignBrief,
}

/// <param name="Tone">One short line on the visual character — "warm and editorial",
/// "dense and technical". What a palette alone cannot carry.</param>
public sealed record BrandKitSummary(
    BrandPalette Palette,
    string? DisplayFont,
    string? BodyFont,
    string? Tone,
    BrandKitSource Source);
