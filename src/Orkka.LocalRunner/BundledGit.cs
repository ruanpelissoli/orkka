namespace Orkka.LocalRunner;

using System.Diagnostics;

/// <summary>
/// The git the runner and every process it spawns will use.
///
/// Orkka does not install git. The runner prints a hint when it is missing
/// (company software center / git-scm.com), and nothing in Orkka downloads it. Older Windows
/// installers unpacked a private PortableGit into <c>~/.orkka/git</c>; that copy is still USED
/// when it is there and the machine has no git of its own, so those installs keep working, but
/// it is never fetched or updated, and deleting <c>~/.orkka</c> removes it with the rest.
///
/// Two rules shape all of it:
///
/// 1. A git the user already has always wins. The private copy is a fallback for a machine with
///    none — and nothing here touches the machine's PATH, only the environment of children this
///    process starts.
/// 2. Configuration is injected through GIT_CONFIG_* rather than written to any file the
///    user owns. It applies to what we spawn and disappears with the process.
/// </summary>
public static class BundledGit
{
    /// <summary>Where an older installer unpacked it. Overridable so a test (or a user with a
    /// portable git of their own) can point this somewhere else.</summary>
    public static string Root =>
        Environment.GetEnvironmentVariable("ORKKA_GIT_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".orkka", "git");

    /// <summary>What the last <see cref="Activate"/> concluded, for the startup banner.</summary>
    public static GitAvailability Availability { get; private set; } = GitAvailability.Unknown;

    /// <summary>The bash the bundle carries, when it carries one (Windows). Claude Code's
    /// Bash tool needs a POSIX shell on Windows and finds it through Git for Windows — so
    /// bundling git for our own sake removes that prerequisite too.</summary>
    public static string? BundledBashPath
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return null;
            var bash = Path.Combine(Root, "bin", "bash.exe");
            return File.Exists(bash) ? bash : null;
        }
    }

    /// <summary>
    /// Resolves which git the runner will use and prepares the environment every child
    /// process inherits: PATH (only when the bundle is actually needed), the git settings
    /// that must hold for agent work, and Claude Code's shell on Windows.
    ///
    /// Call AFTER the environment scrub in Program: the scrub deletes every CLAUDE_CODE_*
    /// marker so the child CLI never mistakes the runner for a session host, and the shell
    /// path set here is configuration rather than a session marker — it has to outlive it.
    /// </summary>
    public static GitAvailability Activate()
    {
        Availability = ResolveAvailability();

        if (Availability == GitAvailability.Bundled)
        {
            PrependToPath(BinDirectories());

            // Only when the user has not chosen a shell themselves.
            if (BundledBashPath is { } bash
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ClaudeGitBashVariable)))
            {
                Environment.SetEnvironmentVariable(ClaudeGitBashVariable, bash);
            }
        }

        ApplyConfigOverrides();
        return Availability;
    }

    private const string ClaudeGitBashVariable = "CLAUDE_CODE_GIT_BASH_PATH";

    private static GitAvailability ResolveAvailability()
    {
        if (SystemGitUsable())
            return GitAvailability.System;

        return BundleExecutable() is not null
            ? GitAvailability.Bundled
            : GitAvailability.Missing;
    }

    /// <summary>
    /// Whether the machine's own git can actually run.
    ///
    /// Resolved by looking for the executable rather than running it: on macOS
    /// <c>/usr/bin/git</c> exists as a stub even with no developer tools installed, and
    /// EXECUTING it is what pops Apple's "install command line developer tools" dialog —
    /// a modal a background daemon must never raise. So the stub is confirmed through
    /// <c>xcode-select -p</c>, which stays silent either way.
    /// </summary>
    private static bool SystemGitUsable()
    {
        var found = FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git");
        if (found is null)
            return false;

        if (OperatingSystem.IsMacOS() && IsAppleGitStub(found))
            return CommandLineToolsInstalled();

        return true;
    }

    private static bool IsAppleGitStub(string path) =>
        path.StartsWith("/usr/bin/", StringComparison.Ordinal);

    private static bool CommandLineToolsInstalled()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("xcode-select", "-p")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return false;

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The bundle's git, or null when nothing is unpacked. Windows layout is Git
    /// for Windows' own (cmd/git.exe); the mac and linux bundles are prefix installs.</summary>
    public static string? BundleExecutable()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [Path.Combine(Root, "cmd", "git.exe"), Path.Combine(Root, "bin", "git.exe")]
            : [Path.Combine(Root, "bin", "git")];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string[] BinDirectories()
    {
        if (!OperatingSystem.IsWindows())
            return [Path.Combine(Root, "bin")];

        // cmd first: it holds the wrappers meant for callers outside the shell. usr/bin
        // carries the POSIX helpers git's own plumbing shells out to.
        return
        [
            Path.Combine(Root, "cmd"),
            Path.Combine(Root, "bin"),
            Path.Combine(Root, "usr", "bin"),
        ];
    }

    private static void PrependToPath(IEnumerable<string> directories)
    {
        var existing = Environment.GetEnvironmentVariable("PATH") ?? "";
        var additions = directories
            .Where(Directory.Exists)
            .Where(d => !existing.Split(Path.PathSeparator)
                .Any(p => string.Equals(p.TrimEnd(Path.DirectorySeparatorChar), d,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal)))
            .ToArray();

        if (additions.Length == 0)
            return;

        Environment.SetEnvironmentVariable(
            "PATH", string.Join(Path.PathSeparator, additions) + Path.PathSeparator + existing);
    }

    /// <summary>
    /// Git settings that must hold for every git this process spawns, applied through
    /// GIT_CONFIG_* — the highest-priority source git reads, and one that touches no file
    /// the user owns.
    ///
    /// Composed in one place because the count is positional: a second caller setting
    /// GIT_CONFIG_KEY_0 silently replaces the first one's setting.
    /// </summary>
    private static void ApplyConfigOverrides()
    {
        var settings = new List<(string Key, string Value)>();

        if (OperatingSystem.IsWindows())
        {
            // Worktree paths (cache + org/repo/task ULIDs) plus real repo file paths routinely
            // exceed Windows' 260-char default ("Filename too long" during worktree checkout).
            settings.Add(("core.longpaths", "true"));
        }

        if (Availability == GitAvailability.Bundled)
        {
            // Git for Windows ships autocrlf=true in its system config. On an imported folder
            // that rewrites the line endings of every file the user brought — a diff nobody
            // asked for, on code that was working. Our bundle, our call: leave bytes alone.
            settings.Add(("core.autocrlf", "false"));

            // A folder copied from another machine or created by an elevated process reads as
            // "dubious ownership" and git refuses to touch it at all. On a personal machine
            // running the user's own runner that check protects nothing and blocks everything.
            settings.Add(("safe.directory", "*"));

            settings.Add(("init.defaultBranch", "main"));
        }

        if (settings.Count == 0)
            return;

        foreach (var (key, value) in ConfigOverrideVariables(Environment.GetEnvironmentVariable, settings))
            Environment.SetEnvironmentVariable(key, value);
    }

    /// <summary>
    /// The <c>GIT_CONFIG_*</c> variables that add <paramref name="settings"/> AFTER the pairs the
    /// process inherited. The developer's own environment may already carry some (a proxy, a
    /// <c>url.….insteadOf</c> for a mirror, a CI's settings); starting again at index 0 would
    /// silently overwrite their first entries and drop the rest with the lower count.
    /// </summary>
    internal static IReadOnlyList<(string Key, string Value)> ConfigOverrideVariables(
        Func<string, string?> getEnvironment, IReadOnlyList<(string Key, string Value)> settings)
    {
        var start = int.TryParse(getEnvironment("GIT_CONFIG_COUNT"), out var inherited) && inherited > 0 ? inherited : 0;
        var vars = new List<(string Key, string Value)>(settings.Count * 2 + 1);
        for (var i = 0; i < settings.Count; i++)
        {
            vars.Add(($"GIT_CONFIG_KEY_{start + i}", settings[i].Key));
            vars.Add(($"GIT_CONFIG_VALUE_{start + i}", settings[i].Value));
        }
        vars.Add(("GIT_CONFIG_COUNT", (start + settings.Count).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return vars;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry (quotes, invalid characters) — skip it, as the OS does.
            }
        }

        return null;
    }
}

/// <summary>Which git the runner ended up with.</summary>
public enum GitAvailability
{
    Unknown,

    /// <summary>The machine's own installation — always preferred.</summary>
    System,

    /// <summary>The copy an older Orkka installer unpacked under ~/.orkka/git (never fetched now).</summary>
    Bundled,

    /// <summary>Neither. Every repository operation on this machine will fail until one exists.</summary>
    Missing,
}
