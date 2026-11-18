namespace Orkka.LocalRunner.Cli;

/// <summary>
/// Leftovers of the installer Orkka had before it ran from source (2026-11): the
/// <c>~/.orkka/bin</c> folder on PATH (Windows user Path, or lines in <c>~/.bashrc</c>,
/// <c>~/.zshrc</c>, <c>~/.profile</c>) and the installed bundle under <c>~/.orkka/app</c>.
/// <c>doctor</c> only REPORTS them with the exact manual steps — Orkka never edits PATH or a
/// shell file itself.
/// </summary>
public static class LegacyInstall
{
    /// <summary>The marker the old opt-in <c>path add</c> wrote above its export line.</summary>
    internal const string RcMarkerPrefix = "# Added by Orkka (orkka-local path add)";

    /// <summary>The line installers before 2026-11 appended unasked.</summary>
    internal const string LegacyRcLine = "export PATH=\"$HOME/.orkka/bin:$PATH\"";

    /// <summary>Human-readable findings, each with how to undo it by hand; empty when clean.</summary>
    public static IReadOnlyList<string> Find(string orkkaRoot, string userHome, string? pathVariable, bool isWindows)
    {
        var findings = new List<string>();
        var binDir = Path.GetFullPath(Path.Combine(orkkaRoot, "bin"));

        var onPath = (pathVariable ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('"'))
            .Any(p => SamePath(Expand(p, userHome), binDir, isWindows));
        if (onPath)
        {
            findings.Add(isWindows
                ? $"{binDir} is on your PATH (added by the old Orkka installer). Orkka no longer needs it: "
                  + "open Settings → System → About → Advanced system settings → Environment Variables, "
                  + "edit your user \"Path\" and delete that entry (then open a new terminal)."
                : $"{binDir} is on your PATH (added by the old Orkka installer). Orkka no longer needs it: "
                  + "remove the line that adds it from your shell's startup file (see below), then open a new terminal.");
        }

        if (!isWindows)
        {
            foreach (var name in new[] { ".bashrc", ".zshrc", ".profile" })
            {
                var rc = Path.Combine(userHome, name);
                string[] lines;
                try
                {
                    if (!File.Exists(rc))
                        continue;
                    lines = File.ReadAllLines(rc);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                var hits = lines
                    .Select((line, i) => (line: line.TrimEnd('\r'), number: i + 1))
                    .Where(l => l.line == LegacyRcLine || l.line.StartsWith(RcMarkerPrefix, StringComparison.Ordinal)
                        || (l.line.Contains(".orkka/bin", StringComparison.Ordinal) && l.line.Contains("PATH", StringComparison.Ordinal)))
                    .ToList();
                if (hits.Count > 0)
                {
                    findings.Add($"{rc} still has the old installer's PATH lines (line {string.Join(", ", hits.Select(h => h.number))}). "
                        + "Delete them in an editor; Orkka never edits this file.");
                }
            }
        }

        foreach (var folder in new[] { "bin", "app", "runtime" })
        {
            var dir = Path.Combine(orkkaRoot, folder);
            if (Directory.Exists(dir))
                findings.Add($"{dir} is left from the old installer (the orkka-local binary, its bundle or the Node.js it downloaded). "
                    + "Orkka runs from your clone now; you can delete this folder.");
        }
        return findings;
    }

    private static string Expand(string entry, string userHome)
    {
        var value = Environment.ExpandEnvironmentVariables(entry);
        if (value.StartsWith("~/", StringComparison.Ordinal) || value == "~")
            value = userHome + value[1..];
        value = value.Replace("$HOME", userHome, StringComparison.Ordinal);
        try { return Path.GetFullPath(value); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return value; }
    }

    private static bool SamePath(string a, string b, bool isWindows) =>
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'),
            isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
