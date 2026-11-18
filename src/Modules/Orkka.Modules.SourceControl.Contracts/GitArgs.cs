namespace Orkka.Modules.SourceControl.Contracts;

/// <summary>
/// Shared git argument building + output parsing for repo history reads. Used by the
/// API-side bare-cache reader and the Orkka Local runner so both produce identical
/// shapes. Field/record separators are ASCII unit/record separators — they cannot
/// appear in commit metadata git prints with %s/%an (git strips control chars).
/// </summary>
public static class GitArgs
{
    private const char FieldSep = '\u001f';
    private const char RecordSep = '\u001e';

    /// <summary>Refs (branch names) we are willing to interpolate into git args.</summary>
    public static bool IsSafeRef(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.StartsWith('-')
        && value.All(c => !char.IsControl(c) && c is not '"' and not '\\' and not ' ');

    /// <summary>Repo-relative file paths we are willing to interpolate into git args.</summary>
    public static bool IsSafePath(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.StartsWith('-')
        && value.All(c => !char.IsControl(c) && c is not '"');

    public static bool IsSafeSha(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length is >= 4 and <= 40
        && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    /// <summary>git log over a ref; with a file path it follows renames.</summary>
    public static string CommitLog(string refName, int skip, int take, string? filePath = null)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);
        // No %ae: contributors' e-mail addresses are never read, so they cannot leave the machine.
        const string format = "%H%x1f%an%x1f%aI%x1f%s%x1e";
        var args = $"log \"{refName}\" --skip={skip} --max-count={take} --pretty=format:{format}";
        return filePath is null ? args : $"{args} --follow -- \"{filePath}\"";
    }

    public static List<GitCommitInfo> ParseCommitLog(string output)
    {
        var commits = new List<GitCommitInfo>();
        foreach (var record in output.Split(RecordSep, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Trim('\n', '\r').Split(FieldSep);
            if (fields.Length < 4 || fields[0].Length == 0)
                continue;
            commits.Add(new GitCommitInfo(
                fields[0],
                fields[1],
                DateTimeOffset.TryParse(fields[2], out var date) ? date : DateTimeOffset.MinValue,
                fields[3]));
        }
        return commits;
    }

    /// <summary>git for-each-ref over a ref namespace, newest commit first.</summary>
    public static string BranchList(string refNamespace) =>
        $"for-each-ref {refNamespace} --sort=-committerdate " +
        $"--format=\"%(refname:short){FieldSep}%(objectname){FieldSep}%(committerdate:iso-strict){FieldSep}%(subject)\"";

    public static List<GitBranchInfo> ParseBranchList(string output, string stripPrefix = "")
    {
        var branches = new List<GitBranchInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(FieldSep);
            if (fields.Length < 4)
                continue;

            var name = fields[0];
            if (stripPrefix.Length > 0 && name.StartsWith(stripPrefix, StringComparison.Ordinal))
                name = name[stripPrefix.Length..];
            // The symbolic origin/HEAD entry duplicates the default branch.
            if (name is "HEAD" or "origin")
                continue;

            branches.Add(new GitBranchInfo(
                name,
                fields[1],
                fields[3].Length > 0 ? fields[3] : null,
                DateTimeOffset.TryParse(fields[2], out var date) ? date : null));
        }
        return branches;
    }

    /// <summary>Patch a single commit introduced (first-parent for merges).</summary>
    public static string CommitDiff(string sha) =>
        $"show \"{sha}\" --first-parent --pretty=format:%H%n%an%n%aI%n%s%n --patch --no-color";

    /// <summary>One tree level: root entries when dirPath is empty, else that directory's
    /// children (the trailing slash makes ls-tree list contents, not the entry itself).</summary>
    public static string TreeLevel(string refName, string? dirPath) =>
        string.IsNullOrEmpty(dirPath)
            ? $"ls-tree \"{refName}\""
            : $"ls-tree \"{refName}\" -- \"{dirPath.TrimEnd('/')}/\"";

    public static List<RepoTreeEntry> ParseTreeLevel(string output)
    {
        // Lines: <mode> SP <type> SP <sha> TAB <path>
        var entries = new List<RepoTreeEntry>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;

            var meta = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (meta.Length < 3) continue;

            var path = line[(tab + 1)..];
            var name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
            entries.Add(new RepoTreeEntry(name, path, meta[1] == "tree"));
        }
        return entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
