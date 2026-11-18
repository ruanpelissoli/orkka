namespace Orkka.SharedKernel.Ai;

/// <summary>
/// Maps well-known claude CLI failure signatures to actionable user-facing messages.
/// The Agent SDK surfaces these as raw <c>ClaudeAgentException</c> messages that mean
/// nothing to an end user (e.g. "Control request timeout: initialize" when the spawned
/// CLI never completes the stream-json handshake — outdated CLI, unfinished first-run
/// onboarding, or a hung process on the machine running the CLI).
/// </summary>
public static class ClaudeCliErrors
{
    public const string InitializeTimeoutMessage =
        "The claude CLI on the runner machine did not respond (initialize timeout). " +
        "On that machine, verify `claude -p \"hi\"` works and update the CLI with " +
        "`npm i -g @anthropic-ai/claude-code`.";

    /// <summary>
    /// User-facing message for an error ResultMessage. The CLI's limit subtypes
    /// (error_max_turns, error_during_execution) arrive with is_error set and NO result
    /// text, which would otherwise surface as a blank log line and "Unknown agent error".
    /// </summary>
    public static string DescribeErrorResult(string? subtype, string? resultText)
    {
        if (!string.IsNullOrEmpty(resultText))
            return resultText;

        return subtype switch
        {
            "error_max_turns" =>
                "The agent hit its maximum-turns limit before it could finish the reply. " +
                "Retry, or split the request into smaller pieces.",
            "error_during_execution" =>
                "The claude CLI reported an execution error without details. Retry; if it " +
                "persists, run the CLI on the runner machine with --debug to see the cause.",
            _ => $"Unknown agent error (result subtype: {(string.IsNullOrEmpty(subtype) ? "none" : subtype)})."
        };
    }

    /// <summary>
    /// True when the CLI killed the run for exceeding <c>CLAUDE_CODE_MAX_OUTPUT_TOKENS</c>
    /// ("API Error: Claude's response exceeded the N output token maximum"). The CLI fails
    /// the run outright rather than truncating, and the same prompt under the same cap
    /// fails the same way every time — so this is the one utility failure a retry can
    /// only make slower: each dead attempt still bills its full generation.
    /// </summary>
    public static bool IsOutputTokenCap(string? error) =>
        !string.IsNullOrWhiteSpace(error)
        && error.Contains("output token maximum", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the error is a transport/availability blip that a plain re-run can fix:
    /// dropped streams, resets, API overload/5xx. Deliberately narrow — auth, billing,
    /// budget (max_turns) and configuration errors must NOT match, because retrying those
    /// re-bills a run that is guaranteed to fail the same way.
    /// </summary>
    public static bool IsTransient(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return false;

        foreach (var signature in TransientSignatures)
        {
            if (error.Contains(signature, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static readonly string[] TransientSignatures =
    [
        // The CLI's dropped-stream notices all share this suffix ("Connection closed/lost
        // mid-response", "Server error mid-response", "Your computer went to sleep
        // mid-response" — each followed by "The response above may be incomplete.").
        "mid-response",
        "stopped arriving",      // "The response stopped arriving. The response above may be incomplete."
        "Connection lost",       // also emitted without the mid-response suffix
        "Connection error",
        "ECONNRESET",
        "ETIMEDOUT",
        "socket hang up",
        "fetch failed",
        "Overloaded",            // Anthropic 529 overloaded_error
        "Internal server error", // Anthropic 500
        "api_error",
        // The CLI process itself died with a Windows fail-fast (0xC0000409, signed and unsigned
        // forms) — seen on a user's laptop seconds after its runner reconnected (2026-09-17).
        // Nothing about the request was wrong; the same run succeeds on the next start. A CLI
        // install that crashes every time is still bounded by the auto-recovery rounds.
        "exit code: -1073740791",
        "exit code: 3221226505",
    ];

    /// <summary>
    /// True when a streamed assistant text block is the CLI's own error notice ("API
    /// Error: Connection lost mid-response. The response above may be incomplete.")
    /// rather than agent prose. The CLI injects these into the assistant stream before
    /// the error result arrives — they are retry signals, and persisting them puts a
    /// raw transport error into the conversation thread.
    /// </summary>
    public static bool IsStreamErrorNotice(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.TrimStart().StartsWith("API Error", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns an actionable message for a known CLI failure signature, or null when the
    /// exception message matches nothing we recognize (callers keep their generic path).
    /// </summary>
    public static string? Describe(string? exceptionMessage)
    {
        if (string.IsNullOrWhiteSpace(exceptionMessage))
            return null;

        // SDK control-protocol request timed out before the CLI answered. The initialize
        // request is the handshake — any control request timing out means the CLI process
        // is unresponsive, so the guidance is the same.
        if (exceptionMessage.Contains("Control request timeout", StringComparison.OrdinalIgnoreCase))
            return InitializeTimeoutMessage;

        return null;
    }
}
