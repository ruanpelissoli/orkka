namespace Orkka.LocalRunner;

using Orkka.LocalRunner.Contracts;

/// <summary>
/// Finds desktop editors installed on this machine and maps an allowlisted editor key to a
/// launcher. The key never becomes a path: it selects from a fixed candidate table, so the
/// host cannot spawn an arbitrary process through this op.
/// </summary>
internal static class EditorResolver
{
    internal sealed record ResolvedEditor(string Key, string DisplayName, string Path);

    // Ordered by how likely an Orkka user is to have it as their primary editor. The
    // candidate lists are per-editor launcher names; Windows ships .cmd shims for the
    // Electron editors, Unix a plain symlink.
    private static readonly (string Key, string DisplayName, string[] Windows, string[] Unix)[] Catalog =
    [
        (RunnerEditors.VsCode, "VS Code", ["code.cmd", "code.exe"], ["code"]),
        (RunnerEditors.Cursor, "Cursor", ["cursor.cmd", "cursor.exe"], ["cursor"]),
        (RunnerEditors.Windsurf, "Windsurf", ["windsurf.cmd", "windsurf.exe"], ["windsurf"]),
        (RunnerEditors.Zed, "Zed", ["zed.exe"], ["zed"]),
        (RunnerEditors.Sublime, "Sublime Text", ["subl.exe", "subl.cmd"], ["subl"]),
        (RunnerEditors.WebStorm, "WebStorm", ["webstorm.cmd", "webstorm64.exe"], ["webstorm"]),
        (RunnerEditors.IntelliJ, "IntelliJ IDEA", ["idea.cmd", "idea64.exe"], ["idea"]),
    ];

    /// <summary>Every editor found on PATH, in catalog preference order.</summary>
    public static List<ResolvedEditor> ListInstalled()
    {
        var found = new List<ResolvedEditor>();
        foreach (var (key, displayName, windows, unix) in Catalog)
        {
            if (ResolveCandidates(OperatingSystem.IsWindows() ? windows : unix) is { } path)
                found.Add(new ResolvedEditor(key, displayName, path));
        }
        return found;
    }

    /// <summary>
    /// Resolves a requested key, or the first installed editor when none was requested.
    /// Returns null when the key is unknown or that editor is not installed here.
    /// </summary>
    public static ResolvedEditor? Resolve(string? requestedKey)
    {
        var installed = ListInstalled();
        if (string.IsNullOrWhiteSpace(requestedKey))
            return installed.FirstOrDefault();

        var key = requestedKey.Trim().ToLowerInvariant();
        return installed.FirstOrDefault(e => e.Key == key);
    }

    private static string? ResolveCandidates(string[] fileNames)
    {
        foreach (var name in fileNames)
        {
            if (FindOnPath(name) is { } hit)
                return hit;
        }

        // PATH is snapshotted when the runner process starts, and "Add to PATH" is an
        // installer OPTION — a user who skipped it, or installed the editor after starting
        // the runner, has a working editor this loop can't see. Probe the standard install
        // locations too, or the button just never appears for them.
        foreach (var name in fileNames)
        {
            foreach (var dir in WellKnownDirectories(name))
            {
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                    // malformed candidate — skip
                }
            }
        }

        return null;
    }

    /// <summary>Standard install directories for a launcher name, per platform.</summary>
    private static IEnumerable<string> WellKnownDirectories(string fileName)
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            switch (Path.GetFileNameWithoutExtension(fileName))
            {
                case "code":
                    yield return Path.Combine(localAppData, "Programs", "Microsoft VS Code", "bin");
                    yield return Path.Combine(programFiles, "Microsoft VS Code", "bin");
                    break;
                case "cursor":
                    yield return Path.Combine(localAppData, "Programs", "cursor", "resources", "app", "bin");
                    break;
                case "windsurf":
                    yield return Path.Combine(localAppData, "Programs", "Windsurf", "bin");
                    break;
                case "subl":
                    yield return Path.Combine(programFiles, "Sublime Text");
                    break;
            }
            yield break;
        }

        // macOS/Linux: the common symlink homes for CLI launchers.
        yield return "/usr/local/bin";
        yield return "/opt/homebrew/bin";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".local", "bin");
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // malformed PATH entry — skip
            }
        }
        return null;
    }
}
