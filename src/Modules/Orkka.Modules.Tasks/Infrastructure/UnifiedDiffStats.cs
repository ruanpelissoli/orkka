namespace Orkka.Modules.Tasks.Infrastructure;

/// <summary>
/// Minimal unified-diff → per-file breakdown, for code-change snapshots. Mirrors the Local
/// host's UnifiedDiffParser (internal there). <see cref="Parse"/> gives summary stats for
/// snapshot capture; <see cref="ParseWithPatches"/> additionally extracts each file's patch
/// so a stored snapshot can be served in the GitHub PR "files" shape when the live branch
/// is gone.
/// </summary>
public static class UnifiedDiffStats
{
    public sealed record FileStat(string Path, string Status, int Additions, int Deletions);

    public sealed record FilePatch(
        string Path, string Status, int Additions, int Deletions,
        string? Patch, string? PreviousPath);

    /// <summary>Per-file patches longer than this are head-truncated — same cap as the
    /// Local host's parser, far above what a diff viewer usefully renders.</summary>
    private const int MaxPatchChars = 60_000;

    public static (int Additions, int Deletions, List<FileStat> Files) Parse(string diff)
    {
        var (add, del, files) = ParseWithPatches(diff);
        return (add, del, files.Select(f => new FileStat(f.Path, f.Status, f.Additions, f.Deletions)).ToList());
    }

    public static (int Additions, int Deletions, List<FilePatch> Files) ParseWithPatches(string diff)
    {
        var files = new List<FilePatch>();
        int totalAdd = 0, totalDel = 0;

        var sections = diff.Split("\ndiff --git ", StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawSection in sections)
        {
            var section = rawSection.StartsWith("diff --git ", StringComparison.Ordinal)
                ? rawSection["diff --git ".Length..]
                : rawSection;

            var lines = section.Split('\n');
            if (lines.Length == 0)
                continue;

            // Header: a/path b/path — take the b/ side (post-change name).
            var header = lines[0];
            var bIdx = header.LastIndexOf(" b/", StringComparison.Ordinal);
            if (bIdx < 0)
                continue;
            var filename = header[(bIdx + 3)..].Trim();

            var status = "modified";
            string? previous = null;
            int add = 0, del = 0;

            foreach (var line in lines.Skip(1))
            {
                if (line.StartsWith("new file mode", StringComparison.Ordinal)) status = "added";
                else if (line.StartsWith("deleted file mode", StringComparison.Ordinal)) status = "removed";
                else if (line.StartsWith("rename from ", StringComparison.Ordinal))
                {
                    status = "renamed";
                    previous = line["rename from ".Length..].Trim();
                }
                else if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)) add++;
                else if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal)) del++;
            }

            totalAdd += add;
            totalDel += del;

            // Patch: everything from the first @@ hunk on (what the diff viewer renders).
            var hunkStart = section.IndexOf("\n@@", StringComparison.Ordinal);
            var patch = hunkStart >= 0 ? section[(hunkStart + 1)..] : null;
            if (patch?.Length > MaxPatchChars)
                patch = patch[..MaxPatchChars] + "\n... (patch truncated)";

            files.Add(new FilePatch(filename, status, add, del, patch, previous));
        }

        return (totalAdd, totalDel, files);
    }
}
