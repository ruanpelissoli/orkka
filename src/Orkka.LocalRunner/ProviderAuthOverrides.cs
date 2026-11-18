namespace Orkka.LocalRunner;

using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// What the runs themselves taught this runner about each provider's sign-in, layered over
/// <see cref="ProviderDetection"/>'s structural probes. The probes can be wrong in both
/// directions — Copilot and Grok may keep their token only in the OS credential store (no
/// file to check), and a Claude credentials file can look fine while every run 401s — so
/// the invokers report the ground truth through <see cref="IProviderAuthFeedback"/>:
///
/// <list type="bullet">
/// <item>An auth-shaped failure is held against the provider: it reads as
/// <c>Authenticated=false</c> with the failure's own remedy in Detail, so the status panel
/// flips, the pickers stop offering its models, and <c>RunnerOperations.ProviderBlocker</c>
/// refuses further runs instead of burning a run per attempt.</item>
/// <item>A clean completion is remembered as positive proof — the only proof there is for a
/// provider whose probe is inconclusive.</item>
/// </list>
///
/// A failure clears on the next clean run, when the login flow sees credentials appear, or
/// when the provider's credential store was written AFTER the failure (the user re-logged
/// in from a terminal of their own; the runner never gets told). <see cref="Changed"/>
/// lets <see cref="RunnerSession"/> re-Hello immediately so the UI reflects the flip
/// within seconds rather than at the next 60s tick. Process-local and unpersisted on
/// purpose: a restart re-probes from scratch, which is the right answer after any change
/// the user made meanwhile.
/// </summary>
public sealed class ProviderAuthOverrides : IProviderAuthFeedback
{
    private sealed record AuthFailure(DateTimeOffset At, string Detail);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, AuthFailure> _failures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _successes = new(StringComparer.Ordinal);

    /// <summary>Raised after any recorded change (failure, success, clear) — never from a
    /// read, so a detection pass can call <see cref="Apply"/> without re-entering itself.</summary>
    public event Action? Changed;

    /// <summary>Test seam: local wall-clock, since the Detail quotes the time to the user.</summary>
    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    /// <summary>Test seam: when the provider's credential store was last written, or null
    /// when there is nothing to check. Defaults to the real files.</summary>
    internal Func<string, DateTimeOffset?> CredentialsWrittenAt { get; init; } =
        ProviderCredentialFiles.LastWrittenAt;

    public void ReportAuthFailure(AiProvider provider, string detail)
    {
        var cliKey = RunnerProviders.FromAiProviderName(provider.ToString());
        lock (_lock)
        {
            _failures[cliKey] = new AuthFailure(Now(), detail);
            _successes.Remove(cliKey);
        }
        Changed?.Invoke();
    }

    public void ReportSuccess(AiProvider provider)
    {
        var cliKey = RunnerProviders.FromAiProviderName(provider.ToString());
        bool changed;
        lock (_lock)
        {
            changed = _failures.Remove(cliKey) | _successes.Add(cliKey);
        }
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Drops a held failure — the login flow saw credentials appear.</summary>
    public void ClearFailure(string cliKey)
    {
        bool changed;
        lock (_lock)
        {
            changed = _failures.Remove(cliKey);
        }
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>True when a run on this provider completed cleanly since the last failure —
    /// the positive evidence an inconclusive structural probe (copilot, grok) leans on.</summary>
    public bool HasRecordedSuccess(string cliKey)
    {
        lock (_lock)
            return _successes.Contains(cliKey);
    }

    /// <summary>The held failure's user-facing Detail, or null when none is held.</summary>
    public string? FailureDetail(string cliKey)
    {
        lock (_lock)
            return _failures.TryGetValue(cliKey, out var failure) ? Describe(failure) : null;
    }

    /// <summary>Layers a held failure over a probed status: Authenticated becomes false and
    /// Detail names the failure (CliFound is kept — the CLI is there, it is the login that
    /// is not). A failure whose credential store was written after it is dropped here, so
    /// a re-login from the user's own terminal heals the status on the next probe.</summary>
    public RunnerProviderStatus Apply(RunnerProviderStatus status)
    {
        AuthFailure? failure;
        lock (_lock)
        {
            if (!_failures.TryGetValue(status.Provider, out failure))
                return status;
        }

        if (CredentialsWrittenAt(status.Provider) is { } written && written > failure.At)
        {
            // Not through ClearFailure: this runs inside a detection pass, whose caller
            // (a Hello) is about to report the corrected status anyway — raising Changed
            // here would just schedule a second Hello on its heels.
            lock (_lock)
                _failures.Remove(status.Provider);
            return status;
        }

        return status with { Authenticated = false, Detail = Describe(failure) };
    }

    private static string Describe(AuthFailure failure) =>
        $"last run failed to authenticate ({failure.At:t}) — {failure.Detail}";
}

/// <summary>
/// Where each CLI keeps the credentials its login command writes — read for mtimes only,
/// never for contents. Copilot's dir excludes what the VS Code extension and the CLI's
/// own logging write on every run (<c>ide/</c>, <c>logs/</c>), since those would make
/// any activity look like a fresh login. The stack env counts for every provider: a
/// token pasted into <c>~/.orkka/runner.env</c> is how several of them are fixed durably.
/// </summary>
internal static class ProviderCredentialFiles
{
    internal static readonly string[] CopilotNoiseDirs = ["ide", "logs"];

    public static DateTimeOffset? LastWrittenAt(string cliKey)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<DateTimeOffset?>
        {
            FileWrittenAt(Path.Combine(home, ".orkka", "runner.env")),
        };

        switch (cliKey)
        {
            case RunnerProviders.Claude:
                candidates.Add(FileWrittenAt(Path.Combine(home, ".claude", ".credentials.json")));
                break;
            case RunnerProviders.Codex:
            case RunnerProviders.Ollama:
                candidates.Add(FileWrittenAt(Path.Combine(home, ".codex", "auth.json")));
                break;
            case RunnerProviders.Gemini:
                candidates.Add(FileWrittenAt(Path.Combine(home, ".gemini", "oauth_creds.json")));
                candidates.Add(FileWrittenAt(Path.Combine(home, ".gemini", ".env")));
                candidates.Add(FileWrittenAt(Path.Combine(home, ".gemini", "settings.json")));
                break;
            case RunnerProviders.Copilot:
                candidates.Add(NewestFileUnder(
                    ProviderDetection.CopilotConfigDir(home), CopilotNoiseDirs));
                break;
            case RunnerProviders.Grok:
                candidates.Add(NewestFileUnder(ProviderDetection.GrokConfigDir(home), []));
                break;
            case RunnerProviders.OpenCode:
                var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                candidates.Add(FileWrittenAt(Path.Combine(
                    string.IsNullOrWhiteSpace(dataHome) ? Path.Combine(home, ".local", "share") : dataHome,
                    "opencode", "auth.json")));
                break;
        }

        DateTimeOffset? newest = null;
        foreach (var candidate in candidates)
            newest = Max(newest, candidate);
        return newest;
    }

    private static DateTimeOffset? FileWrittenAt(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Newest file directly in <paramref name="dir"/> or one level below it,
    /// skipping <paramref name="noiseDirs"/>. One level is as deep as any login artifact
    /// lives; deeper trees are session state.</summary>
    internal static DateTimeOffset? NewestFileUnder(string dir, string[] noiseDirs)
    {
        try
        {
            if (!Directory.Exists(dir))
                return null;

            DateTimeOffset? newest = null;
            foreach (var file in Directory.EnumerateFiles(dir))
                newest = Max(newest, FileWrittenAt(file));

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (noiseDirs.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                    continue;
                foreach (var file in Directory.EnumerateFiles(sub))
                    newest = Max(newest, FileWrittenAt(file));
            }

            return newest;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
