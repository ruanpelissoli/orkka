namespace Orkka.SharedKernel.Ai;

/// <summary>
/// Maps well-known gemini CLI failure signatures to actionable user-facing messages —
/// the same job <see cref="ClaudeCliErrors"/> does for the claude CLI. The signature that
/// matters today: Google shut down the free "Gemini Code Assist for individuals" OAuth
/// tier on 2026-06-18, so personal Google-account sign-ins fail every run with
/// IneligibleTierError telling users to "migrate to the Antigravity suite" — the fix that
/// actually keeps the gemini CLI working is switching it to API-key auth.
/// </summary>
public static class GeminiCliErrors
{
    public const string IneligibleTierMessage =
        "Google has discontinued the gemini CLI's free personal sign-in tier, so " +
        "Google-account auth now fails (IneligibleTierError). Switch the CLI to API-key " +
        "auth on the runner machine: create a free key at https://aistudio.google.com/apikey, " +
        "add GEMINI_API_KEY=<your key> to ~/.gemini/.env, and set " +
        "\"selectedAuthType\": \"gemini-api-key\" in ~/.gemini/settings.json.";

    public const string ModelNotFoundMessage =
        "The Gemini API does not serve this run's model id (ModelNotFoundError). API-key " +
        "auth — the only Gemini auth path since Google closed the free CLI sign-in tier — " +
        "serves a different id set than the old subscription tiers, and Google retires ids " +
        "on its own schedule (e.g. gemini-3.1-pro-preview, not gemini-3-pro or the shut-down " +
        "gemini-3-pro-preview). Pick a current Gemini model in the agent's model settings.";

    public const string MissingApiKeyMessage =
        "The gemini CLI could not find an API key for this run. Easiest fix: create a free " +
        "key at https://aistudio.google.com/apikey and save it on Orkka Local's Settings " +
        "page — every run then receives it automatically. Alternatives on the runner " +
        "machine: GEMINI_API_KEY=<your key> in ~/.gemini/.env (Orkka mirrors that file " +
        "into every task workspace) or ORKKA_GEMINI_API_KEY in ~/.orkka/runner.env.";

    /// <summary>
    /// Returns an actionable message for a known CLI failure signature, or null when the
    /// message matches nothing we recognize (callers keep their generic path).
    /// </summary>
    public static string? Describe(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        // "IneligibleTierError" is the stable marker in the CLI's stderr/error event;
        // "antigravity.google" catches reworded variants of the same shutdown notice.
        if (message.Contains("IneligibleTierError", StringComparison.OrdinalIgnoreCase)
            || message.Contains("antigravity.google", StringComparison.OrdinalIgnoreCase))
            return IneligibleTierMessage;

        // Exit-41 "must specify GEMINI_API_KEY": API-key auth is selected but the CLI's
        // dotenv walk found no key (no ~/.gemini/.env key to mirror into the workspace
        // guard, no ORKKA_GEMINI_API_KEY fallback).
        if (message.Contains("specify GEMINI_API_KEY", StringComparison.OrdinalIgnoreCase)
            || message.Contains("set an Auth method", StringComparison.OrdinalIgnoreCase))
            return MissingApiKeyMessage;

        // "[ModelNotFoundError] models/<id> is not found for API version v1beta, or is
        // not supported for generateContent" — a model id the API-key backend doesn't
        // serve (Code Assist tier aliases, typos in custom agents, retired ids).
        if (message.Contains("ModelNotFoundError", StringComparison.OrdinalIgnoreCase)
            || message.Contains("is not found for API version", StringComparison.OrdinalIgnoreCase))
            return ModelNotFoundMessage;

        return null;
    }
}
