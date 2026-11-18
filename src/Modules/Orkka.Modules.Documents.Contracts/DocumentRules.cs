namespace Orkka.Modules.Documents.Contracts;

/// <summary>
/// Shared validation/normalization for document titles and folder paths. Lives in Contracts so
/// the Agents module can sanitize chat-extracted specs with the exact same rules the Documents
/// endpoints enforce.
/// </summary>
public static class DocumentRules
{
    public const int MaxTitleLength = 200;
    public const int MaxPathLength = 500;
    public const int MaxContentLength = 200_000;

    /// <summary>PDFs are stored as the file the user uploaded — never decoded into the markdown
    /// body — so the cap is on bytes, not characters. 20 MB matches the ceiling the agent-side
    /// text extractor already refuses to read past.</summary>
    public const long MaxFileSizeBytes = 20L * 1024 * 1024;

    public const string PdfContentType = "application/pdf";

    public static bool IsPdfFileName(string? fileName) =>
        fileName is not null && fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Trims, strips slashes and control characters, caps length.
    /// Returns null when nothing usable remains.</summary>
    public static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var chars = title.Trim()
            .Where(c => c is not ('/' or '\\') && !char.IsControl(c))
            .ToArray();
        var cleaned = new string(chars).Trim();

        if (cleaned.Length == 0)
            return null;

        return cleaned.Length <= MaxTitleLength ? cleaned : cleaned[..MaxTitleLength].TrimEnd();
    }

    /// <summary>Normalizes a folder path: splits on '/', drops empty/'.'/'..' segments, keeps only
    /// [a-zA-Z0-9-_ .] characters per segment, rejoins. Never fails — unusable input becomes "" (root).</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var segments = path.Replace('\\', '/').Split('/')
            .Select(s => new string(s.Trim()
                .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' or '.')
                .ToArray()).Trim())
            .Where(s => s.Length > 0 && s is not ("." or ".."))
            .ToArray();

        var joined = string.Join('/', segments);
        return joined.Length <= MaxPathLength ? joined : joined[..MaxPathLength];
    }
}
