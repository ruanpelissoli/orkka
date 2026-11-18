namespace Orkka.Modules.Execution.Agents;

/// <summary>
/// Long-lived Claude subscription token (from `claude setup-token`) for headless runs.
///
/// The claude CLI's default subscription auth keeps a short-lived access token plus a
/// SINGLE-USE refresh token in ~/.claude/.credentials.json. Orkka runs many claude
/// processes concurrently against that one file — when the access token expires
/// mid-fleet, several children race the refresh, the losers' stale writes clobber the
/// rotated pair, and the user is forced back through `/login`. A setup-token is
/// long-lived and never refreshed, so concurrent children cannot log each other out.
///
/// The token cannot ride the runner's own process environment: the startup scrub
/// deletes every CLAUDE_CODE_* variable (nested-session markers) and the Layer-2
/// sanitizer blanks *TOKEN* names for children. So the runner installs a file-backed
/// resolver here (CLAUDE_CODE_OAUTH_TOKEN in ~/.orkka/runner.env) and
/// <see cref="ClaudeAgentInvoker"/> re-applies the value per child AFTER sanitization.
/// Hosted workers never set <see cref="Source"/> (they authenticate with an API key),
/// so this stays a no-op there.
/// </summary>
public static class ClaudeSetupToken
{
    /// <summary>The variable the claude CLI reads the token from — also the key users
    /// put in ~/.orkka/runner.env.</summary>
    public const string EnvVar = "CLAUDE_CODE_OAUTH_TOKEN";

    /// <summary>Set once at runner startup to a fresh-read resolver over the stack env
    /// file. Resolved on every spawn so a token added while the runner is up takes
    /// effect without a restart.</summary>
    public static Func<string?>? Source { get; set; }

    /// <summary>The configured token, or null when no source is installed or the
    /// source yields nothing usable.</summary>
    public static string? Resolve()
    {
        var token = Source?.Invoke();
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }
}
