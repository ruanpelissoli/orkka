namespace Orkka.Modules.Organizations.Features.Settings;

using System.Text.RegularExpressions;

/// <summary>Rules for a workspace's display name.</summary>
public static partial class OrgNaming
{
    /// <summary>Matches the column width in <c>OrganizationConfiguration</c>.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Trims and collapses whitespace. Returns null when nothing usable is left — empty,
    /// whitespace-only, or longer than the column allows — which callers answer as a 400.
    /// </summary>
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var collapsed = WhitespaceRegex().Replace(name.Trim(), " ");
        return collapsed.Length > MaxLength ? null : collapsed;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
