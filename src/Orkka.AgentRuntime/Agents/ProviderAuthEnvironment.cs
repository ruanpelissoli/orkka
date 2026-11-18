namespace Orkka.Modules.Execution.Agents;

using System.Text.RegularExpressions;

/// <summary>One variable a vendor CLI reads for its sign-in. <see cref="Secret"/> decides where
/// a workspace keeps it (the OS secret store vs plain profile.env) and whether the command
/// guard refuses a shell line that expands it.</summary>
public sealed record ProviderAuthVariable(string Name, bool Secret);

/// <summary>
/// What <see cref="ProviderAuthEnvironment.Resolve(AiProvider, string?)"/> hands one agent
/// process. Its <see cref="ToString"/> names the variables and never prints a value — a
/// resolution may well reach a log line by accident.
/// </summary>
public sealed class ProviderAuthResolution
{
    public static readonly ProviderAuthResolution None =
        new(new Dictionary<string, string>(StringComparer.Ordinal), loginOnly: false, fromWorkspace: false);

    public ProviderAuthResolution(IReadOnlyDictionary<string, string> variables, bool loginOnly, bool fromWorkspace)
    {
        Variables = variables;
        LoginOnly = loginOnly;
        FromWorkspace = fromWorkspace;
    }

    /// <summary>Name → value, only the provider's allowlisted names that are actually set.</summary>
    public IReadOnlyDictionary<string, string> Variables { get; }

    /// <summary>The workspace (or the machine) pinned this provider to the CLI's own stored
    /// login (<c>ORKKA_CLAUDE_AUTH=login</c> …): no variable is passed, not even a machine-wide
    /// one.</summary>
    public bool LoginOnly { get; }

    /// <summary>The values came from the run's workspace profile, not the machine.</summary>
    public bool FromWorkspace { get; }

    /// <summary>Any value that authenticates (as opposed to plain configuration such as a region)
    /// — what decides whether the CLI's own subprocess scrubbing is switched on.</summary>
    public bool CarriesSecret => Variables.Keys.Any(ProviderAuthEnvironment.IsSecret);

    public override string ToString() => Variables.Count == 0
        ? LoginOnly ? "CLI login only" : "no variables"
        : $"{string.Join(", ", Variables.Keys.Order(StringComparer.Ordinal))} (values hidden)"
          + (FromWorkspace ? " from the workspace" : " from the machine");
}

/// <summary>Where the runner keeps the values. Implemented on the runner (workspace
/// profile.env + the OS secret store, ~/.orkka/runner.env, the runner's environment as it
/// was at startup); absent anywhere else, which makes every resolution empty.</summary>
public interface IProviderAuthSource
{
    /// <summary>The workspace's own values for <paramref name="names"/> (only those set).
    /// Null or unknown workspace → empty.</summary>
    IReadOnlyDictionary<string, string> WorkspaceValues(string? workspace, IReadOnlyCollection<string> names);

    /// <summary>Machine-wide values: ~/.orkka/runner.env, then the runner's startup environment.</summary>
    IReadOnlyDictionary<string, string> MachineValues(IReadOnlyCollection<string> names);

    /// <summary>The <c>env_key</c> names the developer's own Codex config
    /// (<c>$CODEX_HOME/config.toml</c>) declares for its model providers — an Azure OpenAI
    /// provider reads its key from whatever variable it names.</summary>
    IReadOnlyCollection<string> CodexConfigEnvKeys();
}

/// <summary>
/// Per-provider sign-in passthrough: the explicit allowlist of variables each vendor CLI needs
/// for an environment-based sign-in (API key, company gateway, Amazon Bedrock, Google Vertex,
/// Microsoft Foundry, Azure OpenAI, a GitHub token, a GitHub Enterprise host), re-applied to the
/// agent process AFTER <see cref="AgentEnvironment"/> has blanked everything that looks like a
/// secret — and only for the provider being spawned: Claude never gets <c>OPENAI_API_KEY</c>,
/// Codex never gets AWS credentials, Copilot never gets an Anthropic key.
///
/// Values come from <see cref="Source"/> (the runner installs it). Layering, per provider:
/// <list type="number">
/// <item>The run's workspace profile pins <c>ORKKA_{CLAUDE|CODEX|COPILOT}_AUTH=login</c> → the
/// CLI's own login, nothing passed.</item>
/// <item>The workspace sets ANY of the provider's variables → exactly the workspace's values (a
/// job on the company's Bedrock never inherits a machine-wide personal key, and the other way
/// round).</item>
/// <item>Otherwise the machine's: <c>~/.orkka/runner.env</c>, then the environment the runner
/// was started in.</item>
/// </list>
///
/// Names and their sources (verified against the vendors' docs/source, 2026-11-17):
/// Claude Code — code.claude.com/docs/en/env-vars, /amazon-bedrock, /google-vertex-ai;
/// Codex — openai/codex <c>codex-rs/login/src/auth/manager.rs</c> (CODEX_API_KEY,
/// OPENAI_API_KEY, CODEX_ACCESS_TOKEN), learn.chatgpt.com config-advanced / environment-variables
/// (CODEX_HOME, CODEX_CA_CERTIFICATE, model_providers.*.env_key, the AZURE_OPENAI_API_KEY
/// example); Copilot CLI — docs.github.com copilot-cli-reference/cli-command-reference
/// "Environment variables". See AGENTS.md "Sign-in modes" and SECURITY.md "AI sign-in".
/// </summary>
public static partial class ProviderAuthEnvironment
{
    /// <summary>The value of a mode key that pins the CLI's own stored login.</summary>
    public const string LoginOnlyValue = "login";

    public const string ClaudeModeKey = "ORKKA_CLAUDE_AUTH";
    public const string CodexModeKey = "ORKKA_CODEX_AUTH";
    public const string CopilotModeKey = "ORKKA_COPILOT_AUTH";

    public static readonly IReadOnlyList<ProviderAuthVariable> Claude =
    [
        // Anthropic API key / company LLM gateway.
        new("ANTHROPIC_API_KEY", true),
        new("ANTHROPIC_AUTH_TOKEN", true),
        new("ANTHROPIC_CUSTOM_HEADERS", true),
        new("ANTHROPIC_BASE_URL", false),
        // `claude setup-token` — the long-lived subscription token.
        new("CLAUDE_CODE_OAUTH_TOKEN", true),
        // Amazon Bedrock.
        new("CLAUDE_CODE_USE_BEDROCK", false),
        new("CLAUDE_CODE_SKIP_BEDROCK_AUTH", false),
        new("AWS_REGION", false),
        new("AWS_DEFAULT_REGION", false),
        new("AWS_PROFILE", false),
        new("AWS_CONFIG_FILE", false),
        new("AWS_SHARED_CREDENTIALS_FILE", false),
        new("AWS_ACCESS_KEY_ID", true),
        new("AWS_SECRET_ACCESS_KEY", true),
        new("AWS_SESSION_TOKEN", true),
        new("AWS_BEARER_TOKEN_BEDROCK", true),
        new("ANTHROPIC_BEDROCK_BASE_URL", false),
        new("ANTHROPIC_BEDROCK_REGION_PREFIX", false),
        new("ANTHROPIC_SMALL_FAST_MODEL_AWS_REGION", false),
        // Google Vertex (Google Cloud's Agent Platform).
        new("CLAUDE_CODE_USE_VERTEX", false),
        new("CLAUDE_CODE_SKIP_VERTEX_AUTH", false),
        new("ANTHROPIC_VERTEX_PROJECT_ID", false),
        new("CLOUD_ML_REGION", false),
        new("ANTHROPIC_VERTEX_BASE_URL", false),
        new("GOOGLE_APPLICATION_CREDENTIALS", false),
        // Microsoft Foundry.
        new("CLAUDE_CODE_USE_FOUNDRY", false),
        new("CLAUDE_CODE_SKIP_FOUNDRY_AUTH", false),
        new("ANTHROPIC_FOUNDRY_RESOURCE", false),
        new("ANTHROPIC_FOUNDRY_BASE_URL", false),
        new("ANTHROPIC_FOUNDRY_API_KEY", true),
        new("ANTHROPIC_FOUNDRY_AUTH_TOKEN", true),
        // Model pins a Bedrock/Vertex/Foundry account needs (its own model ids).
        new("ANTHROPIC_DEFAULT_OPUS_MODEL", false),
        new("ANTHROPIC_DEFAULT_SONNET_MODEL", false),
        new("ANTHROPIC_DEFAULT_HAIKU_MODEL", false),
        new(ClaudeModeKey, false),
    ];

    public static readonly IReadOnlyList<ProviderAuthVariable> Codex =
    [
        // `codex exec` reads CODEX_API_KEY; OPENAI_API_KEY is what older releases (and the
        // realtime/onboarding paths) read; CODEX_ACCESS_TOKEN is a ChatGPT token for automation.
        new("CODEX_API_KEY", true),
        new("OPENAI_API_KEY", true),
        new("CODEX_ACCESS_TOKEN", true),
        // The Azure OpenAI example provider's env_key. Any other env_key the developer's
        // config.toml names joins at run time (IProviderAuthSource.CodexConfigEnvKeys).
        new("AZURE_OPENAI_API_KEY", true),
        // Older releases' base-URL variable (current ones read `openai_base_url` in config.toml).
        new("OPENAI_BASE_URL", false),
        // Blanked by the sanitizer's CODEX marker, yet plain configuration.
        new("CODEX_HOME", false),
        new("CODEX_CA_CERTIFICATE", false),
        new(CodexModeKey, false),
    ];

    public static readonly IReadOnlyList<ProviderAuthVariable> Copilot =
    [
        // In the CLI's own precedence order.
        new("COPILOT_GITHUB_TOKEN", true),
        new("GH_TOKEN", true),
        new("GITHUB_TOKEN", true),
        // GitHub Enterprise Cloud with data residency.
        new("GH_HOST", false),
        new("COPILOT_GH_HOST", false),
        new("COPILOT_HOME", false),
        // Bring-your-own-key model provider.
        new("COPILOT_PROVIDER_API_KEY", true),
        new("COPILOT_PROVIDER_HEADERS", true),
        new(CopilotModeKey, false),
    ];

    /// <summary>Installed once by the runner. Null (anywhere else) = no passthrough at all.</summary>
    public static IProviderAuthSource? Source { get; set; }

    private static readonly Dictionary<string, ProviderAuthVariable> ByName = Claude.Concat(Codex).Concat(Copilot)
        .GroupBy(v => v.Name, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>The static allowlist of a provider. Ollama runs codex against a local server
    /// with no key, and the unguarded CLIs get nothing.</summary>
    public static IReadOnlyList<ProviderAuthVariable> For(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => Claude,
        AiProvider.OpenAI => Codex,
        AiProvider.GitHubCopilot => Copilot,
        _ => [],
    };

    public static string? ModeKeyFor(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => ClaudeModeKey,
        AiProvider.OpenAI => CodexModeKey,
        AiProvider.GitHubCopilot => CopilotModeKey,
        _ => null,
    };

    /// <summary>A name some provider's allowlist holds (or a Codex config env_key) — the only
    /// names a workspace may set with <c>./orkka workspace set-env</c>.</summary>
    public static bool IsAllowlisted(string name, IEnumerable<string>? codexConfigKeys = null)
        => ByName.ContainsKey(name) || (codexConfigKeys?.Contains(name, StringComparer.Ordinal) ?? false);

    /// <summary>Whether a name carries a credential. Unknown names (a Codex env_key) are
    /// secret by default.</summary>
    public static bool IsSecret(string name) => !ByName.TryGetValue(name, out var v) || v.Secret;

    /// <summary>Which provider(s) a name belongs to, for the CLI's messages.</summary>
    public static IEnumerable<AiProvider> ProvidersOf(string name)
    {
        if (Claude.Any(v => v.Name == name)) yield return AiProvider.Anthropic;
        if (Codex.Any(v => v.Name == name)) yield return AiProvider.OpenAI;
        if (Copilot.Any(v => v.Name == name)) yield return AiProvider.GitHubCopilot;
    }

    /// <summary>Every credential name of every provider — what the command guard refuses to
    /// expand (<c>$ANTHROPIC_API_KEY</c>, <c>$env:GH_TOKEN</c>, …), plus the Codex env_keys
    /// the installed source reports.</summary>
    public static IReadOnlyCollection<string> SecretNames()
    {
        var names = new HashSet<string>(ByName.Values.Where(v => v.Secret).Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var key in Source?.CodexConfigEnvKeys() ?? [])
                names.Add(key);
        }
        catch
        {
            // A guard decision must never fail on a config read.
        }
        return names;
    }

    /// <summary>The names to look up for <paramref name="provider"/>, in allowlist order.</summary>
    public static IReadOnlyList<string> NamesFor(AiProvider provider, IEnumerable<string>? codexConfigKeys = null)
    {
        var names = For(provider).Select(v => v.Name).ToList();
        if (provider == AiProvider.OpenAI)
            foreach (var key in codexConfigKeys ?? [])
                if (!names.Contains(key, StringComparer.Ordinal))
                    names.Add(key);
        return names;
    }

    /// <summary>The variables for one spawn of <paramref name="provider"/>'s CLI on behalf of
    /// <paramref name="workspace"/>. Never throws: a failed read degrades to the CLI's own login.</summary>
    public static ProviderAuthResolution Resolve(AiProvider provider, string? workspace)
        => Source is { } source ? Resolve(provider, workspace, source) : ProviderAuthResolution.None;

    public static ProviderAuthResolution Resolve(AiProvider provider, string? workspace, IProviderAuthSource source)
    {
        if (ModeKeyFor(provider) is not { } modeKey)
            return ProviderAuthResolution.None;

        try
        {
            var codexKeys = provider == AiProvider.OpenAI ? source.CodexConfigEnvKeys() : [];
            var names = NamesFor(provider, codexKeys);

            var fromWorkspace = Clean(source.WorkspaceValues(workspace, names));
            if (IsLoginOnly(fromWorkspace, modeKey))
                return new(Empty(), loginOnly: true, fromWorkspace: true);
            fromWorkspace.Remove(modeKey);
            if (fromWorkspace.Count > 0)
                return new(fromWorkspace, loginOnly: false, fromWorkspace: true);

            var fromMachine = Clean(source.MachineValues(names));
            if (IsLoginOnly(fromMachine, modeKey))
                return new(Empty(), loginOnly: true, fromWorkspace: false);
            fromMachine.Remove(modeKey);
            return new(fromMachine, loginOnly: false, fromWorkspace: false);
        }
        catch
        {
            return ProviderAuthResolution.None;
        }
    }

    private static bool IsLoginOnly(Dictionary<string, string> values, string modeKey)
        => values.TryGetValue(modeKey, out var mode)
           && string.Equals(mode, LoginOnlyValue, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> Empty() => new(StringComparer.Ordinal);

    private static Dictionary<string, string> Clean(IReadOnlyDictionary<string, string> values)
    {
        var clean = Empty();
        foreach (var (name, value) in values)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                clean[name] = trimmed;
        }
        return clean;
    }

    // ── Codex config.toml env_key discovery ─────────────────────────────────

    /// <summary>
    /// The <c>env_key = "NAME"</c> values in a Codex config.toml (one per model provider). A
    /// line scan rather than a TOML parser: only the names matter, and a name that does not
    /// pass <see cref="IsAcceptableCodexEnvKey"/> is dropped.
    /// </summary>
    public static IReadOnlyList<string> ParseCodexEnvKeys(string toml)
    {
        var keys = new List<string>();
        foreach (Match m in EnvKeyLine().Matches(toml))
        {
            var name = m.Groups["name"].Value;
            if (IsAcceptableCodexEnvKey(name) && !keys.Contains(name, StringComparer.Ordinal))
                keys.Add(name);
        }
        return keys;
    }

    /// <summary>Platform and other providers' names a Codex config may not pull into Codex's
    /// environment, whatever it says: GitHub and cloud credentials, other AI vendors' keys,
    /// databases and registries.</summary>
    private static readonly string[] ForbiddenCodexKeyMarkers =
    [
        "GITHUB", "GH_", "COPILOT", "AWS_", "GOOGLE", "GCP", "ANTHROPIC", "CLAUDE", "DATABASE", "DB_",
        "POSTGRES", "PG", "MYSQL", "MONGO", "REDIS", "NPM", "NUGET", "PYPI", "DOCKER", "SSH", "STRIPE",
        "CLERK", "VERCEL", "PASSWORD", "ORKKA",
    ];

    public static bool IsAcceptableCodexEnvKey(string name)
        => EnvName().IsMatch(name)
           && !ForbiddenCodexKeyMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase))
           && (!ByName.TryGetValue(name, out _) || Codex.Any(v => v.Name == name));

    [GeneratedRegex("""^\s*env_key\s*=\s*["'](?<name>[^"']+)["']""", RegexOptions.Multiline)]
    private static partial Regex EnvKeyLine();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,63}$")]
    private static partial Regex EnvName();
}
