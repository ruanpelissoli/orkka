namespace Orkka.SharedKernel.Ai;

/// <summary>
/// Resolves a provider CLI to an absolute executable path.
///
/// One resolver, used by BOTH the readiness probe and the spawn. Before this existed the two
/// answered differently: readiness scanned PATH itself while the Agent SDK ran its own
/// discovery, so a machine could report "claude connected" and then fail the run with
/// "Claude Code not found". Whatever this returns is exactly what gets spawned.
///
/// SECURITY: local sources only — an operator-set environment variable, the process PATH, and
/// well-known per-user install directories. Nothing here may ever read a path out of a task
/// payload, an agent's output, or anything else the hosted backend sends: that would turn a
/// remote string into the path of a process this machine executes. The env override is checked
/// as a literal existing file and is never itself PATH-searched.
/// </summary>
public static class ProviderCliResolver
{
    /// <summary>Per-provider override, e.g. ORKKA_CLAUDE_CLI. For an operator whose CLI lives
    /// somewhere this resolver would not think to look. Read from the runner's own process
    /// environment, which only someone with access to this machine can set.</summary>
    public static string EnvVarFor(string provider) =>
        $"ORKKA_{provider.ToUpperInvariant()}_CLI";

    /// <summary>Test seam. Swapped rather than mutating the real process environment —
    /// blanking PATH for the whole test host breaks every sibling test that shells out.</summary>
    internal static Func<string, string?> ReadEnv { get; set; } = Environment.GetEnvironmentVariable;

    /// <summary>
    /// Absolute path to the provider's executable, or null when it cannot be found — null is a
    /// real answer, not a reason to guess. Callers that get null should report the provider as
    /// unavailable rather than spawning a bare name and hoping.
    /// </summary>
    public static string? Resolve(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return null;

        // 1. Operator override. Must already be a file — no PATH search, no extension probing,
        //    so a typo fails closed instead of resolving to some other binary.
        var overridePath = ReadEnv(EnvVarFor(provider));
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return Path.GetFullPath(overridePath);

        foreach (var dir in SearchDirectories())
        {
            foreach (var candidate in CandidateNames(provider))
            {
                var full = Path.Combine(dir, candidate);
                if (File.Exists(full))
                    return full;
            }
        }

        return null;
    }

    /// <summary>PATH first (an operator's explicit ordering wins), then the native installer's
    /// per-user directory. That last entry is why this is not just a PATH scan: the native
    /// install drops claude in ~/.local/bin without touching PATH, and a process started before
    /// the user added it — a long-lived terminal, a service — never sees the update.</summary>
    private static IEnumerable<string> SearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in (ReadEnv("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = dir.Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed))
                yield return trimmed;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var localBin = Path.Combine(home, ".local", "bin");
            if (seen.Add(localBin))
                yield return localBin;
        }
    }

    /// <summary>
    /// Windows extension order is deliberate: the native .exe FIRST, script shims LAST.
    /// The SDK spawns the process directly with piped stdio, which a .cmd/.ps1 shim cannot
    /// drive — picking one hangs every run at the initialize handshake while the same command
    /// works fine by hand. Preferring the .exe makes that trap unreachable through this path.
    ///
    /// The bare extensionless name is NOT a Windows candidate. npm drops one next to its
    /// .cmd/.ps1 shims, but it is a POSIX sh script — CreateProcess cannot run it and neither
    /// can cmd.exe, so counting it made the readiness probe report a CLI the spawn could
    /// never launch (a runner said "copilot=ready" and the run died with cmd.exe's
    /// "'copilot' is not recognized", 2026-08-24).
    /// </summary>
    private static string[] CandidateNames(string provider) =>
        OperatingSystem.IsWindows()
            ? [$"{provider}.exe", $"{provider}.cmd", $"{provider}.ps1"]
            : [provider];

    /// <summary>
    /// <see cref="Resolve"/> plus HOW to start what it found, so the spawn cannot disagree
    /// with the probe about launchability. A .exe (or any POSIX binary) spawns directly; a
    /// .cmd/.bat shim must go through `cmd /c` (raw CreateProcess with piped stdio cannot
    /// drive it). A .ps1 is only reached when no .exe/.cmd sits next to it: if a .cmd shim does
    /// exist beside it (npm installs both) that is used; otherwise <c>pwsh</c>/<c>powershell
    /// -NoProfile -File</c> — never with <c>-ExecutionPolicy Bypass</c>, so a machine whose policy
    /// refuses the script refuses it here too. Null exactly when <see cref="Resolve"/> is null.
    /// Callers append their own arguments after <see cref="CliLaunch.ArgumentPrefix"/>.
    /// </summary>
    public static CliLaunch? ResolveLaunch(string provider) =>
        Resolve(provider) is { } path ? LaunchFor(path) : null;

    internal static CliLaunch LaunchFor(string cliPath)
    {
        if (!OperatingSystem.IsWindows())
            return new(cliPath, [], cliPath);

        return Path.GetExtension(cliPath).ToLowerInvariant() switch
        {
            ".cmd" or ".bat" => new("cmd.exe", ["/c", cliPath], cliPath),
            ".ps1" when File.Exists(Path.ChangeExtension(cliPath, ".cmd")) =>
                new("cmd.exe", ["/c", Path.ChangeExtension(cliPath, ".cmd")], cliPath),
            ".ps1" => new(PowerShellForScripts(), ["-NoProfile", "-File", cliPath], cliPath),
            _ => new(cliPath, [], cliPath),
        };
    }

    /// <summary>pwsh when it is on PATH (its default policy on Windows is RemoteSigned), else
    /// Windows PowerShell. Either way the machine's execution policy applies.</summary>
    private static string PowerShellForScripts()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "pwsh.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        return "powershell.exe";
    }
}

/// <summary>How to start a resolved provider CLI: the process to spawn and the arguments
/// that must precede the CLI's own. <paramref name="CliPath"/> is what <see
/// cref="ProviderCliResolver.Resolve"/> found, kept for logs and error messages.</summary>
public sealed record CliLaunch(
    string FileName, IReadOnlyList<string> ArgumentPrefix, string CliPath);
