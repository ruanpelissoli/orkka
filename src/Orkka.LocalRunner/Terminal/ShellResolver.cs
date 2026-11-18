namespace Orkka.LocalRunner.Terminal;

/// <summary>
/// Picks the shell for a terminal session. Only allowlisted names from the wire are
/// honored (the request field is host-originated, but a path must never be spawnable
/// from it); anything else falls back to the OS default chain.
/// </summary>
internal static class ShellResolver
{
    // ResolveForCommand (below) reuses this for the Run button's dev-server processes.

    public sealed record ResolvedShell(string Path, string[] Args, string Name);

    public static ResolvedShell Resolve(string? requested)
    {
        if (requested is not null && ResolveByName(requested.ToLowerInvariant()) is { } named)
            return named;
        return OperatingSystem.IsWindows() ? ResolveWindowsDefault() : ResolveUnixDefault();
    }

    /// <summary>
    /// The OS default shell wired to run one command and exit — how the Run button starts a
    /// dev server. Unix uses a login shell (`-lc`): the user's node version manager, rbenv,
    /// pyenv and friends live in that profile, and a "command not found" for a tool that works
    /// in their own terminal is the most confusing way this feature can fail.
    ///
    /// Windows: run commands are POSIX-ish one-liners (`cd apps/web &amp;&amp; npm run dev`)
    /// and Windows PowerShell 5.1 has no <c>&amp;&amp;</c> at all — it fails to parse the line.
    /// So PowerShell 7 (<c>pwsh -NoLogo -NoProfile -Command</c>), otherwise <c>cmd.exe /c</c>,
    /// which runs <c>npm.cmd</c> and chains with <c>&amp;&amp;</c> just fine. Never
    /// <c>powershell.exe</c> for a run command.
    ///
    /// The execution policy is NEVER bypassed. A <c>-Command</c> string is not subject to it —
    /// only <c>.ps1</c> files are — so a plain command needs no bypass; when a command does run
    /// a <c>.ps1</c> (an npm.ps1 shim, a repo script) and the machine's policy refuses it, that
    /// is the company's policy working as intended: the run fails, and
    /// <see cref="ExecutionPolicyHint"/> explains why in the run's log. <c>-NoProfile</c>: a
    /// profile is itself a script under that policy, and nothing a run needs should depend on it.
    /// </summary>
    public static ResolvedShell ResolveForCommand(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            if (FindOnPath("pwsh.exe") is { } pwsh)
                return new(pwsh, ["-NoLogo", "-NoProfile", "-Command", command], "pwsh");
            return new(ComSpec() ?? "cmd.exe", ["/c", command], "cmd");
        }

        var shell = Resolve(null);
        return shell with { Args = ["-lc", command] };
    }

    /// <summary>
    /// When a run's output shows PowerShell's execution policy refusing a script, the line Orkka
    /// adds to the log; else null. Orkka never bypasses the policy — this only says what happened
    /// and what the developer can do within it.
    /// </summary>
    public static string? ExecutionPolicyHint(string output)
    {
        if (string.IsNullOrEmpty(output))
            return null;
        var blocked = output.Contains("running scripts is disabled on this system", StringComparison.OrdinalIgnoreCase)
            || output.Contains("is not digitally signed", StringComparison.OrdinalIgnoreCase)
            || output.Contains("about_Execution_Policies", StringComparison.OrdinalIgnoreCase);
        return blocked
            ? "PowerShell's execution policy on this machine refused a script (see above). Orkka does not bypass "
              + "it. Use the command's .cmd form (e.g. npm.cmd instead of npm.ps1) in orkka.json, or ask your IT team."
            : null;
    }

    private static ResolvedShell? ResolveByName(string name) => name switch
    {
        "pwsh" when FindOnPath("pwsh.exe") is { } p => new(p, ["-NoLogo"], "pwsh"),
        "powershell" when FindOnPath("powershell.exe") is { } p => new(p, ["-NoLogo"], "powershell"),
        "cmd" when ComSpec() is { } p => new(p, [], "cmd"),
        "bash" when FindUnixShell("bash") is { } p => new(p, [], "bash"),
        "zsh" when FindUnixShell("zsh") is { } p => new(p, [], "zsh"),
        "sh" when FindUnixShell("sh") is { } p => new(p, [], "sh"),
        _ => null,
    };

    private static ResolvedShell ResolveWindowsDefault()
    {
        if (FindOnPath("pwsh.exe") is { } pwsh)
            return new(pwsh, ["-NoLogo"], "pwsh");
        if (FindOnPath("powershell.exe") is { } powershell)
            return new(powershell, ["-NoLogo"], "powershell");
        return new(ComSpec() ?? "cmd.exe", [], "cmd");
    }

    private static ResolvedShell ResolveUnixDefault()
    {
        var userShell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrEmpty(userShell) && File.Exists(userShell))
            return new(userShell, [], System.IO.Path.GetFileName(userShell));
        foreach (var candidate in new[] { "/bin/zsh", "/bin/bash", "/bin/sh" })
        {
            if (File.Exists(candidate))
                return new(candidate, [], System.IO.Path.GetFileName(candidate));
        }
        return new("/bin/sh", [], "sh");
    }

    private static string? ComSpec()
    {
        var comspec = Environment.GetEnvironmentVariable("COMSPEC");
        return !string.IsNullOrEmpty(comspec) && File.Exists(comspec)
            ? comspec
            : FindOnPath("cmd.exe");
    }

    private static string? FindUnixShell(string name)
    {
        var wellKnown = $"/bin/{name}";
        return File.Exists(wellKnown) ? wellKnown : FindOnPath(name);
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = System.IO.Path.Combine(dir.Trim(), fileName);
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
