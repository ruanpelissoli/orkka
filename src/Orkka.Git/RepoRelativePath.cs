namespace Orkka.Git;

/// <summary>
/// Validates paths that originate from LLM output (scaffold file paths, PM task context files, etc.)
/// before they are written to disk or persisted. LLM output is untrusted — a hijacked or careless
/// model can emit <c>../../.env</c> or <c>/etc/passwd</c> to read/write outside the repository root.
/// A safe path is relative, contains no parent-directory (<c>..</c>) segments, and is not rooted.
/// </summary>
public static class RepoRelativePath
{
    /// <summary>
    /// Returns true if <paramref name="path"/> is a safe repo-relative path (no traversal, not
    /// absolute/rooted/UNC/home-relative, no null bytes).
    /// </summary>
    public static bool IsSafeRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = path.Trim().Replace('\\', '/');

        if (normalized.Contains('\0'))
            return false;

        // Absolute / rooted / UNC / home-relative forms are all rejected.
        if (normalized.StartsWith('/') || normalized.StartsWith("~") || normalized.StartsWith("//"))
            return false;

        // Windows drive-qualified path (e.g. "C:/...").
        if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
            return false;

        // Any parent-directory segment escapes the root.
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
            return false;

        return true;
    }

    /// <summary>
    /// Resolves <paramref name="candidate"/> against <paramref name="repoRoot"/> and confirms the
    /// result stays inside the root. Use at the point of an actual filesystem write. Returns false
    /// (and null) if the candidate is unsafe or would escape the root.
    /// </summary>
    public static bool TryResolveWithin(string repoRoot, string? candidate, out string? resolved)
    {
        resolved = null;

        if (!IsSafeRelative(candidate))
            return false;

        var rootFull = Path.GetFullPath(repoRoot);
        var combined = Path.GetFullPath(Path.Combine(rootFull, candidate!));

        // Ensure the resolved path is the root itself or sits beneath it.
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSep, StringComparison.Ordinal) &&
            !string.Equals(combined, rootFull, StringComparison.Ordinal))
            return false;

        resolved = combined;
        return true;
    }
}
