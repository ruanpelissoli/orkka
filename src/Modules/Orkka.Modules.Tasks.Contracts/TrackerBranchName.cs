namespace Orkka.Modules.Tasks.Contracts;

using System.Globalization;
using System.Text;

/// <summary>
/// The branch a tracker ticket's task works on, from its ticket source's template:
/// <c>{type}</c> (feature, bugfix, refactor, improvement), <c>{key}</c> (SHOP-12) and <c>{slug}</c>
/// (the title, lower-case and hyphenated). Always a valid git ref name, or null when the template
/// renders to nothing usable — the caller then falls back to Orkka's own naming.
/// </summary>
public static class TrackerBranchName
{
    public const string DefaultTemplate = "{type}/{key}-{slug}";
    private const int MaxSlug = 40;
    private const int MaxLength = 120;

    public static string? Render(string? template, TaskType type, string ticketKey, string title)
    {
        var typeName = type switch
        {
            TaskType.BugFix => "bugfix",
            TaskType.Refactor => "refactor",
            TaskType.Improvement => "improvement",
            _ => "feature",
        };

        var raw = (string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template)
            .Replace("{type}", typeName, StringComparison.Ordinal)
            .Replace("{key}", ticketKey, StringComparison.Ordinal)
            .Replace("{slug}", Slug(StripKeyPrefix(title, ticketKey)), StringComparison.Ordinal);

        var name = Sanitize(raw);
        return name.Length == 0 ? null : name;
    }

    /// <summary>Imported titles read "[SHOP-12] Add invoice export"; the key is already in the name.</summary>
    private static string StripKeyPrefix(string title, string key)
    {
        var prefix = $"[{key}]";
        return title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? title[prefix.Length..] : title;
    }

    private static string Slug(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue; // é → e
            sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        var slug = Collapse(sb.ToString(), '-').Trim('-');
        if (slug.Length > MaxSlug)
            slug = slug[..MaxSlug].TrimEnd('-');
        return slug;
    }

    /// <summary>
    /// Keeps what git allows in a ref name (git check-ref-format): ASCII letters, digits, and
    /// <c>/ - _ .</c>; no empty, dot-led or ".lock" components, no "..", no trailing dot.
    /// </summary>
    private static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' ? c : '-');

        var parts = Collapse(sb.ToString(), '-')
            .Replace("..", ".", StringComparison.Ordinal)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim('-', '.'))
            .Select(p => p.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ? p[..^5] : p)
            .Where(p => p.Length > 0);

        var joined = string.Join('/', parts);
        if (joined.Length > MaxLength)
            joined = joined[..MaxLength].TrimEnd('-', '.', '/');
        return joined;
    }

    private static string Collapse(string text, char c)
    {
        var doubled = new string(c, 2);
        while (text.Contains(doubled, StringComparison.Ordinal))
            text = text.Replace(doubled, c.ToString(), StringComparison.Ordinal);
        return text;
    }
}
