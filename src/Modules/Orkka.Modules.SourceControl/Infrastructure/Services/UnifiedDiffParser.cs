namespace Orkka.Modules.SourceControl.Infrastructure.Services;

using Orkka.Modules.SourceControl.Contracts;

/// <summary>
/// Minimal unified-diff → per-file breakdown in GitHub's PR "files" shape, so a diff read
/// from someone's own git renders in the same review UI as one fetched from a forge.
///
/// Shared by both local-path pull request implementations (Orkka Local's and the hosted one)
/// — the parsing has nothing to do with which host is asking.
/// </summary>
public static class UnifiedDiffParser
{
    public static (int Additions, int Deletions, List<GitHubPrFile> Files) Parse(string diff)
    {
        var files = new List<GitHubPrFile>();
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
            if (patch?.Length > 60_000)
                patch = patch[..60_000] + "\n... (patch truncated)";

            files.Add(new GitHubPrFile("local", filename, status, add, del, add + del, patch, previous));
        }

        return (totalAdd, totalDel, files);
    }
}
