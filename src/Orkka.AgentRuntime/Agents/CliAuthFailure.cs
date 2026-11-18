namespace Orkka.Modules.Execution.Agents;

using System.Text.RegularExpressions;

/// <summary>
/// Recognises a vendor CLI's "you are not signed in" failure so the run can say so in
/// plain words — and so the runner can stop offering that provider until the user logs
/// back in. The CLIs report this in wildly different shapes (an HTTP 401 in a JSON line,
/// "Not logged in", "run `codex login`", a Google RPC status, gh's "Bad credentials"),
/// and left alone each one surfaces as "&lt;cli&gt; CLI exited with code 1" plus a stderr
/// dump the user has to decode. Deliberately conservative: a false positive tells a
/// signed-in user to log in again AND blocks their runs (see
/// <see cref="IProviderAuthFeedback"/>), so rate limits, overloads, dropped connections
/// and budget errors must all read as null here.
///
/// Kept SharedKernel-free and dependency-free on purpose: the runner's
/// <c>ProviderDetection.LoginCommand</c> delegates to <see cref="LoginCommand"/> so the
/// remedy in a failed run's timeline and the remedy in the status panel can never drift.
/// </summary>
public static class CliAuthFailure
{
    /// <summary>
    /// The actionable message for an auth-shaped failure, or null when the failure is
    /// anything else (or nothing at all). <paramref name="exitCode"/> is informational —
    /// no CLI reserves an exit code for auth, so the text is what decides.
    /// </summary>
    public static string? Classify(string cliName, int exitCode, string? stderrTail, string? streamError)
    {
        var line = FirstMatchingLine(cliName, stderrTail)
                   ?? FirstMatchingLine(cliName, streamError);
        if (line is null)
            return null;

        return $"{cliName} refused this run because it isn't signed in on this machine ({line}). "
               + $"Run `{LoginCommand(cliName)}` there, then retry.";
    }

    /// <summary>True when the text carries any sign-in failure signature for the CLI —
    /// the predicate behind <see cref="Classify"/>, for callers that already have a
    /// composed message and only need the verdict.</summary>
    public static bool IsAuthShaped(string cliName, string? text) =>
        FirstMatchingLine(cliName, text) is not null;

    /// <summary>The command a visible terminal runs to walk the user through login. One
    /// table for every surface: the failed run's timeline, the runner's status panel and
    /// its Connect button all quote it. `claude /login` executes the slash command on
    /// startup (a bare `claude` greets returning users with a cached banner even when the
    /// token is expired); `gemini` with no args offers Google sign-in on first run (dead
    /// for personal accounts since 2026-06-18, but still the verify step for a configured
    /// API key); `grok` with no args opens the TUI, which runs the browser login on first
    /// launch; opencode's login is per PROVIDER, so its command prompts for which one;
    /// Ollama has no login at all — its only terminal-fixable state is a stopped server.</summary>
    public static string LoginCommand(string cliName) => cliName switch
    {
        "codex" => "codex login",
        "gemini" => "gemini",
        "copilot" => "copilot login",
        "ollama" => "ollama serve",
        "grok" => "grok",
        "opencode" => "opencode auth login",
        _ => "claude /login",
    };

    /// <summary>The first line of <paramref name="text"/> carrying a signature, trimmed
    /// and capped so a 4K stderr dump cannot swallow the message. Line-by-line so the
    /// quoted evidence is the CLI's own sentence, not the whole tail.</summary>
    private static string? FirstMatchingLine(string cliName, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var signatures = SignaturesFor(cliName);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (signatures.Any(s => s.IsMatch(line)))
                return line.Length <= MaxQuotedLength ? line : line[..MaxQuotedLength] + "…";
        }

        return null;
    }

    private const int MaxQuotedLength = 200;

    private static Regex[] SignaturesFor(string cliName) => cliName switch
    {
        "codex" or "ollama" => [.. Generic, .. Codex],
        "gemini" => [.. Generic, .. Gemini],
        "copilot" => [.. Generic, .. Copilot],
        "grok" => [.. Generic, .. Grok],
        _ => Generic,
    };

    private static Regex Signature(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Word-bounded status codes: "401" inside a token count or an id must not match.
    // "unauthenticated" is listed on its own because "not authenticated" does not cover it.
    private static readonly Regex[] Generic =
    [
        Signature(@"\b401\b"),
        Signature(@"\b403\b"),
        Signature(@"\bUnauthorized\b"),
        Signature(@"\bunauthenticated\b"),
        Signature(@"\bnot logged in\b"),
        Signature(@"\bnot authenticated\b"),
        Signature(@"\blogin required\b"),
        Signature(@"\binvalid_grant\b"),
        Signature(@"\btoken (?:has )?expired\b"),
        Signature(@"\bInvalid API key\b"),
        Signature(@"\bNo API key\b"),
        Signature(@"\bPlease sign in\b"),
        Signature(@"\bBad credentials\b"),
        Signature(@"\bInvalid authentication credentials\b"),
    ];

    private static readonly Regex[] Codex =
    [
        Signature(@"run `?codex login`?"),
    ];

    private static readonly Regex[] Gemini =
    [
        Signature(@"\bUNAUTHENTICATED\b"),
        Signature(@"\bPERMISSION_DENIED\b"),
        Signature(@"\bAPI key not valid\b"),
        Signature(@"\bIneligibleTierError\b"),
    ];

    private static readonly Regex[] Copilot =
    [
        Signature(@"\bcopilot login\b"),
        Signature(@"\bGitHub authentication\b"),
    ];

    private static readonly Regex[] Grok =
    [
        Signature(@"\binvalid api key\b"),
        Signature(@"run `?grok`?(?:\s|$|\.)"),
    ];
}
