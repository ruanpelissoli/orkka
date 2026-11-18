namespace Orkka.Modules.Execution.Agents;

/// <summary>
/// Builds the sanitized set of environment variables handed to an agent subprocess.
///
/// The Claude Agent SDK spawns the <c>claude</c> CLI as a child process that inherits the
/// Worker's environment. The Worker holds platform secrets (database connection strings,
/// Stripe/Clerk secrets, provider keys, etc.), and an agent runs untrusted, prompt-injectable
/// customer code — so anything sensitive the child could read must be blanked out.
///
/// Because the SDK only lets us <i>add</i> environment variables (it has no "clear all" switch),
/// we override every sensitive variable currently in the process with an empty string. Setting a
/// key to "" is enough to deny an agent a usable secret while leaving CLI-required variables
/// (PATH, HOME, TEMP, …) intact. Full allowlist isolation with a clean base environment is
/// delivered by the container-per-run sandbox (roadmap P2); this is the in-process backstop.
/// See SECURITY.md "Layer 2".
/// </summary>
public static class AgentEnvironment
{
    // Substrings (case-insensitive) that mark a variable as sensitive. Kept broad on purpose:
    // it is safe to blank a variable an agent does not need, and dangerous to miss one.
    private static readonly string[] SensitiveMarkers =
    [
        "SECRET", "PASSWORD", "PASSWD", "TOKEN", "APIKEY", "API_KEY",
        "CONNECTIONSTRING", "CONNECTION_STRING", "CONNECTIONSTRINGS",
        "DATABASE", "DB_", "POSTGRES", "PGPASSWORD", "PGUSER", "PGHOST", "PGDATABASE",
        "MYSQL", "MONGO", "REDIS",
        "GITHUB", "GH_TOKEN", "VERCEL", "CLERK", "STRIPE", "ANTHROPIC", "OPENAI",
        "GEMINI", "CODEX", "NEON",
        "AZURE", "AWS_", "GCP", "GOOGLE_APPLICATION", "SENDGRID", "SMTP", "TWILIO",
        "PRIVATE_KEY", "PRIVATEKEY", "WEBHOOK", "ENCRYPTION",
    ];

    /// <summary>
    /// Returns the name→value pairs to apply to the agent subprocess. Every sensitive variable
    /// present in <paramref name="source"/> maps to "" (blanked). Non-sensitive variables are
    /// left untouched (they are inherited as-is by the child).
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildSanitizedOverrides(
        IDictionary<string, string?> source)
    {
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in source.Keys)
        {
            if (key is null)
                continue;

            if (IsSensitive(key))
                overrides[key] = string.Empty;
        }

        return overrides;
    }

    /// <summary>Snapshots the current process environment and blanks every sensitive variable.</summary>
    public static IReadOnlyDictionary<string, string> BuildSanitizedOverridesFromProcess()
    {
        var source = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = entry.Key?.ToString();
            if (name is not null)
                source[name] = entry.Value?.ToString();
        }

        return BuildSanitizedOverrides(source);
    }

    public static bool IsSensitive(string variableName) =>
        SensitiveMarkers.Any(marker =>
            variableName.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
