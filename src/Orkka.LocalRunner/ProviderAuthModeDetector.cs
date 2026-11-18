namespace Orkka.LocalRunner;

using System.Text.Json;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Execution.Agents;

/// <summary>How a CLI signs in, as <see cref="ProviderDetection"/> reports it.
/// <see cref="Ready"/>: the mode counts as ready on its own — the sign-in comes from variables
/// Orkka hands the CLI (or the CLI's own settings env block), or the CLI itself said it holds a
/// login. <see cref="Problem"/>: the mode is
/// configured but cannot work (a Codex provider whose env_key is not set).</summary>
public sealed record DetectedAuthMode(string Mode, string? Detail, bool Ready, string? Problem = null);

/// <summary>
/// Works out each provider's sign-in mode from NON-SECRET signals only: which allowlisted
/// variables are set (names; values only of plain settings such as a region or a host), the
/// Claude settings file's <c>env</c> block and <c>apiKeyHelper</c> key, and Codex's
/// <c>config.toml</c> provider table. Nothing here returns, logs or prints a credential: a
/// detail is a region, a host name (never a URL's user info, path or query) or a variable name.
/// </summary>
public static class ProviderAuthModeDetector
{
    // ── Claude ────────────────────────────────────────────────────────────

    /// <summary>What Claude Code's user settings contribute: the <c>env</c> block (allowlisted
    /// names; plain values only) and whether an <c>apiKeyHelper</c> is configured.</summary>
    public sealed record ClaudeSettings(IReadOnlyDictionary<string, string> Env, bool ApiKeyHelper)
    {
        public static readonly ClaudeSettings Empty = new(new Dictionary<string, string>(), false);
    }

    /// <summary>Reads <c>settings.json</c> under the Claude config dir. Secret env values are
    /// replaced by a marker — only their presence matters here. Never throws.</summary>
    public static ClaudeSettings ReadClaudeSettings(string configDir)
    {
        try
        {
            var path = Path.Combine(configDir, "settings.json");
            if (!File.Exists(path))
                return ClaudeSettings.Empty;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ClaudeSettings.Empty;

            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("env", out var block) && block.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in block.EnumerateObject())
                {
                    if (!ProviderAuthEnvironment.Claude.Any(v => v.Name == entry.Name))
                        continue;
                    var value = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : entry.Value.GetRawText();
                    if (string.IsNullOrWhiteSpace(value))
                        continue;
                    env[entry.Name] = ProviderAuthEnvironment.IsSecret(entry.Name) ? SecretMarker : value;
                }
            }

            var helper = root.TryGetProperty("apiKeyHelper", out var h)
                         && h.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(h.GetString());
            return new ClaudeSettings(env, helper);
        }
        catch
        {
            return ClaudeSettings.Empty;
        }
    }

    private const string SecretMarker = "(set)";

    /// <summary>Claude's env-based mode, or null when it signs in with its own login.
    /// <paramref name="passed"/> = what Orkka hands the CLI; it outranks the settings block.</summary>
    public static DetectedAuthMode? Claude(IReadOnlyDictionary<string, string> passed, ClaudeSettings settings)
    {
        var env = new Dictionary<string, string>(settings.Env, StringComparer.Ordinal);
        foreach (var (k, v) in passed)
            env[k] = v;
        string? Get(string name) => env.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        if (Truthy(Get("CLAUDE_CODE_USE_BEDROCK")))
            return new(ProviderAuthModes.Bedrock,
                Get("AWS_REGION") ?? Get("AWS_DEFAULT_REGION")
                ?? (Get("AWS_PROFILE") is { } profile ? $"profile {profile}" : null),
                Ready: true);
        if (Truthy(Get("CLAUDE_CODE_USE_VERTEX")))
            return new(ProviderAuthModes.Vertex,
                Join(Get("CLOUD_ML_REGION"), Get("ANTHROPIC_VERTEX_PROJECT_ID")), Ready: true);
        if (Truthy(Get("CLAUDE_CODE_USE_FOUNDRY")))
            return new(ProviderAuthModes.Foundry,
                Get("ANTHROPIC_FOUNDRY_RESOURCE") ?? HostOf(Get("ANTHROPIC_FOUNDRY_BASE_URL")), Ready: true);
        if (Get("ANTHROPIC_BASE_URL") is { } baseUrl)
            return new(ProviderAuthModes.Gateway, HostOf(baseUrl), Ready: true);
        if (Get("ANTHROPIC_API_KEY") is not null)
            return new(ProviderAuthModes.ApiKey, "ANTHROPIC_API_KEY", Ready: true);
        if (settings.ApiKeyHelper)
            return new(ProviderAuthModes.ApiKey, "apiKeyHelper", Ready: true);
        if (Get("ANTHROPIC_AUTH_TOKEN") is not null)
            return new(ProviderAuthModes.Token, "ANTHROPIC_AUTH_TOKEN", Ready: true);
        if (Get(ClaudeSetupToken.EnvVar) is not null)
            return new(ProviderAuthModes.Token, ClaudeSetupToken.EnvVar, Ready: true);
        return null;
    }

    // ── Codex ─────────────────────────────────────────────────────────────

    /// <summary>The parts of a Codex config.toml that decide its sign-in.</summary>
    public sealed record CodexConfig(string? ModelProvider, string? OpenAiBaseUrl,
        IReadOnlyDictionary<string, CodexModelProvider> Providers)
    {
        public static readonly CodexConfig Empty = new(null, null, new Dictionary<string, CodexModelProvider>());
    }

    public sealed record CodexModelProvider(string Id, string? EnvKey, string? BaseUrl);

    /// <summary>A line scan of config.toml: top-level <c>model_provider</c> and
    /// <c>openai_base_url</c>, and <c>env_key</c>/<c>base_url</c> under each
    /// <c>[model_providers.ID]</c> table. Never throws.</summary>
    public static CodexConfig ParseCodexConfig(string? toml)
    {
        if (string.IsNullOrWhiteSpace(toml))
            return CodexConfig.Empty;
        try
        {
            string? modelProvider = null, openAiBaseUrl = null, table = null;
            var providers = new Dictionary<string, (string? EnvKey, string? BaseUrl)>(StringComparer.Ordinal);
            foreach (var raw in toml.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;
                if (line.StartsWith('['))
                {
                    table = line.Trim('[', ']', ' ');
                    continue;
                }
                var eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                var key = line[..eq].Trim();
                var value = Unquote(line[(eq + 1)..]);
                if (table is null)
                {
                    if (key == "model_provider") modelProvider = value;
                    else if (key == "openai_base_url") openAiBaseUrl = value;
                }
                else if (table.StartsWith("model_providers.", StringComparison.Ordinal))
                {
                    var id = table["model_providers.".Length..].Trim('"', '\'');
                    providers.TryGetValue(id, out var p);
                    if (key == "env_key") p.EnvKey = value;
                    else if (key == "base_url") p.BaseUrl = value;
                    providers[id] = p;
                }
            }
            return new CodexConfig(modelProvider, openAiBaseUrl,
                providers.ToDictionary(kv => kv.Key, kv => new CodexModelProvider(kv.Key, kv.Value.EnvKey, kv.Value.BaseUrl),
                    StringComparer.Ordinal));
        }
        catch
        {
            return CodexConfig.Empty;
        }
    }

    /// <summary>Codex's mode. <paramref name="storedLogin"/>: what <c>codex login</c> stored —
    /// "chatgpt", "api-key" or null.</summary>
    public static DetectedAuthMode? Codex(IReadOnlyDictionary<string, string> passed, CodexConfig config, string? storedLogin)
    {
        string? Get(string name) => passed.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        if (config.ModelProvider is { Length: > 0 } providerId && providerId != "openai"
            && config.Providers.TryGetValue(providerId, out var provider))
        {
            var azure = providerId.Contains("azure", StringComparison.OrdinalIgnoreCase)
                        || (provider.BaseUrl?.Contains("azure", StringComparison.OrdinalIgnoreCase) ?? false);
            var mode = azure ? ProviderAuthModes.AzureOpenAi : ProviderAuthModes.Gateway;
            var host = HostOf(provider.BaseUrl) ?? providerId;
            if (provider.EnvKey is { Length: > 0 } envKey)
                return Get(envKey) is not null
                    ? new(mode, host, Ready: true)
                    : new(mode, host, Ready: true,
                        Problem: $"Codex is set to use the '{providerId}' provider, which reads its key from {envKey} — "
                                 + $"set it with `./orkka workspace set-env <workspace> {envKey}` (or in ~/.orkka/runner.env).");
            // A custom provider that authenticates some other way (an auth command, OpenAI auth).
            return new(mode, host, Ready: true);
        }

        var gatewayHost = HostOf(Get("OPENAI_BASE_URL")) ?? HostOf(config.OpenAiBaseUrl);
        foreach (var keyName in new[] { "CODEX_API_KEY", "OPENAI_API_KEY" })
            if (Get(keyName) is not null)
                return gatewayHost is not null
                    ? new(ProviderAuthModes.Gateway, gatewayHost, Ready: true)
                    : new(ProviderAuthModes.ApiKey, keyName, Ready: true);
        if (Get("CODEX_ACCESS_TOKEN") is not null)
            return new(ProviderAuthModes.Token, "CODEX_ACCESS_TOKEN", Ready: true);

        return storedLogin switch
        {
            "chatgpt" => new(gatewayHost is null ? ProviderAuthModes.Login : ProviderAuthModes.Gateway,
                gatewayHost ?? "ChatGPT", Ready: true),
            "api-key" => new(ProviderAuthModes.ApiKey, gatewayHost ?? "codex login --with-api-key", Ready: true),
            _ => null,
        };
    }

    // ── Copilot ───────────────────────────────────────────────────────────

    /// <summary>Copilot's mode. <paramref name="hostFromEnvironment"/>: GH_HOST/COPILOT_GH_HOST as
    /// the runner sees them when nothing is passed (they are not secret, so not scrubbed).</summary>
    public static DetectedAuthMode? Copilot(IReadOnlyDictionary<string, string> passed, string? hostFromEnvironment)
    {
        string? Get(string name) => passed.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        var host = NormalizeHost(Get("COPILOT_GH_HOST") ?? Get("GH_HOST") ?? hostFromEnvironment);
        var tokenVar = new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" }.FirstOrDefault(n => Get(n) is not null);
        if (tokenVar is not null)
            return host is not null
                ? new(ProviderAuthModes.EnterpriseHost, host, Ready: true)
                : new(ProviderAuthModes.Token, tokenVar, Ready: true);
        if (Get("COPILOT_PROVIDER_API_KEY") is not null)
            return new(ProviderAuthModes.ApiKey, "COPILOT_PROVIDER_API_KEY", Ready: true);
        if (host is not null)
            return new(ProviderAuthModes.EnterpriseHost, host, Ready: false);
        return null;
    }

    // ── helpers ───────────────────────────────────────────────────────────

    internal static bool Truthy(string? value)
        => value is not null && (value.Trim() is "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                                 || value.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>The host (and a non-default port) of a URL — never its user info, path or query.</summary>
    internal static string? HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return null;
        return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    }

    /// <summary>A GitHub host as given (bare or a URL), or null for github.com itself.</summary>
    internal static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;
        var bare = HostOf(host) ?? host.Trim().TrimEnd('/');
        return bare.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? null : bare;
    }

    private static string? Join(string? a, string? b)
        => a is null ? b : b is null ? a : $"{a} · {b}";

    private static string? Unquote(string value)
    {
        var v = value.Trim();
        var hash = v.IndexOf(" #", StringComparison.Ordinal);
        if (hash >= 0 && !(v.StartsWith('"') || v.StartsWith('\'')))
            v = v[..hash].Trim();
        if (v.Length >= 2 && (v[0] == '"' || v[0] == '\''))
        {
            var close = v.IndexOf(v[0], 1);
            if (close > 0)
                return v[1..close];
        }
        return v.Length == 0 ? null : v;
    }
}
