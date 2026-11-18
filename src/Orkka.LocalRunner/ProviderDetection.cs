namespace Orkka.LocalRunner;

using System.Text.Json;
using Orkka.LocalRunner.Contracts;

/// <summary>
/// Detects each vendor CLI (claude / codex / gemini / copilot / grok / opencode) on PATH and
/// whether its subscription credentials look usable. Claude gets a freshness check — an
/// expired subscription token whose headless refresh fails produces "401 Invalid
/// authentication credentials" on every run, so it must read as unauthenticated. The Codex
/// CLI refreshes its own tokens from a stored refresh token, so its check is structural (file
/// exists and carries tokens) rather than expiry-based. Gemini is ready on an API key only: a
/// Google sign-in (~/.gemini/oauth_creds.json) proves an account, not an entitlement — any
/// Google account can sign the CLI in, and since Google closed the free personal tier
/// (2026-06-18) that login only runs for paid Code Assist / Workspace licences, which the
/// runner cannot see — so it never counts, and the Detail steers to a key. Copilot
/// and Grok may keep their tokens only in the OS credential store, so their structural
/// checks are inconclusive on their own — what the runs themselves report
/// (<see cref="ProviderAuthOverrides"/>) is layered over every probe here, and for those two
/// it is the deciding evidence.
///
/// The last two are not subscription CLIs at all, and are exact opposites of each other:
/// Ollama is models with no harness — "server reachable, codex CLI installed (that is what
/// drives them), at least one model pulled" — while OpenCode is a harness with no models:
/// "CLI installed, and at least one provider logged in", since an OpenCode with no logins
/// starts fine and then cannot answer anything.
/// </summary>
public sealed class ProviderDetection(
    ILogger<ProviderDetection> logger,
    // What the runs reported about each provider's login — null in the CLI-only callers
    // (doctor), which have no runs to learn from.
    ProviderAuthOverrides? overrides = null,
    // Test seam for the machine-touching legs (runner.env, gh, PATH, home dir).
    ProviderDetectionProbes? probes = null)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly ProviderDetectionProbes _probes = probes ?? ProviderDetectionProbes.Default;

    /// <param name="workspace">The workspace profile the answer is for: its own AI sign-in
    /// overrides (`workspace set-env`) decide the auth mode. Null = the machine's.</param>
    public RunnerProviderStatus[] DetectAll(string? workspace = null)
    {
        // One gh spawn per pass at most — see GhLoggedIn.
        _ghLoggedIn = null;
        return [.. RunnerProviders.All.Select(p => Detect(p, workspace))];
    }

    /// <summary>The probed status with the sign-in mode and the CLI version added, and any
    /// run-time auth evidence layered over it: a run that failed to authenticate outranks a
    /// probe that says the files (or the variables) look fine.</summary>
    public RunnerProviderStatus Detect(string provider, string? workspace = null)
    {
        var status = Probe(provider);
        status = ApplyAuthMode(status, workspace);
        if (status.CliFound && provider != RunnerProviders.Ollama)
        {
            // A launcher stub on PATH (VS Code's `copilot` shim prints "GitHub Copilot CLI was
            // not found") is not the CLI: no run could start, whatever its login looks like.
            if (_probes.CliIsStub(provider))
                status = status with
                {
                    CliFound = false,
                    Authenticated = false,
                    AuthMode = null,
                    AuthDetail = null,
                    Detail = $"`{provider}` on this machine's PATH is a launcher stub, not the {provider} CLI — install the CLI itself.",
                };
            else
                status = status with { CliVersion = _probes.CliVersion(provider) };
        }
        status = overrides?.Apply(status) ?? status;
        return ApplyGuardPolicy(status);
    }

    /// <summary>
    /// Adds how the CLI signs in (<see cref="RunnerProviderStatus.AuthMode"/>) for Claude, Codex
    /// and Copilot, from non-secret signals (<see cref="ProviderAuthModeDetector"/>). An
    /// environment-based sign-in — an API key, a gateway, Bedrock, Vertex, Foundry, Azure
    /// OpenAI, a GitHub token — counts as ready on its own when the CLI is installed: the
    /// probe cannot verify a key without spending it, and a key that is wrong surfaces as the
    /// CLI's own auth failure, which <see cref="ProviderAuthOverrides"/> then holds against the
    /// provider. A configured mode that cannot work (a Codex provider whose env_key is unset)
    /// is not ready, with the variable to set in Detail.
    /// </summary>
    private RunnerProviderStatus ApplyAuthMode(RunnerProviderStatus status, string? workspace)
    {
        var aiProvider = status.Provider switch
        {
            RunnerProviders.Claude => Orkka.Modules.Agents.Contracts.AiProvider.Anthropic,
            RunnerProviders.Codex => Orkka.Modules.Agents.Contracts.AiProvider.OpenAI,
            RunnerProviders.Copilot => Orkka.Modules.Agents.Contracts.AiProvider.GitHubCopilot,
            _ => (Orkka.Modules.Agents.Contracts.AiProvider?)null,
        };
        if (aiProvider is null)
            return status;

        DetectedAuthMode? mode;
        var loginOnly = false;
        try
        {
            var resolution = _probes.AuthSource() is { } source
                ? Orkka.Modules.Execution.Agents.ProviderAuthEnvironment.Resolve(aiProvider.Value, workspace, source)
                : Orkka.Modules.Execution.Agents.ProviderAuthResolution.None;
            loginOnly = resolution.LoginOnly;
            var passed = resolution.Variables;
            mode = status.Provider switch
            {
                RunnerProviders.Claude => ProviderAuthModeDetector.Claude(passed,
                    ProviderAuthModeDetector.ReadClaudeSettings(_probes.ClaudeConfigDir())),
                RunnerProviders.Codex => ProviderAuthModeDetector.Codex(passed,
                    ProviderAuthModeDetector.ParseCodexConfig(_probes.CodexConfigToml()), CodexStoredLogin(status.CliFound)),
                _ => ProviderAuthModeDetector.Copilot(passed, _probes.GitHubHostFromEnvironment()),
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not work out the {Provider} sign-in mode: {Message}", status.Provider, ex.Message);
            mode = null;
        }

        // A workspace pinned to the CLI's own login: a machine-wide setup token does not count.
        if (loginOnly && status.Provider == RunnerProviders.Claude && mode is null)
            status = status with { Authenticated = HasFreshClaudeCredentials(ignoreSetupToken: true) };

        if (mode is null)
            return status with
            {
                AuthMode = status.Authenticated ? ProviderAuthModes.Login
                    : status.CliFound ? ProviderAuthModes.None : null,
                AuthDetail = status.Authenticated && status.Provider == RunnerProviders.Codex ? "ChatGPT" : null,
            };

        if (mode.Problem is not null)
            return status with { Authenticated = false, Detail = mode.Problem, AuthMode = mode.Mode, AuthDetail = mode.Detail };

        if (mode.Ready && status.CliFound)
            return status with { Authenticated = true, Detail = null, AuthMode = mode.Mode, AuthDetail = mode.Detail };

        return status with { AuthMode = status.Authenticated ? mode.Mode : ProviderAuthModes.None, AuthDetail = mode.Detail };
    }

    /// <summary>What `codex login` stored: "chatgpt" (a ChatGPT plan), "api-key", or null.
    /// ~/.codex/auth.json first; when the CLI keeps its login in the OS keyring
    /// (cli_auth_credentials_store) there is no file, and `codex login status` — which prints
    /// the method, never the secret in full — answers instead (cached, see the probe).</summary>
    private string? CodexStoredLogin(bool cliFound)
    {
        var authPath = Path.Combine(_probes.CodexHome(), "auth.json");
        try
        {
            if (File.Exists(authPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(authPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
                    return "chatgpt";
                if (root.TryGetProperty("OPENAI_API_KEY", out var key) && key.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(key.GetString()))
                    return "api-key";
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not read codex auth.json: {Message}", ex.Message);
        }
        return cliFound ? _probes.CodexLoginStatus() : null;
    }

    /// <summary>
    /// CLIs Orkka cannot put its command guard on. Claude takes PreToolUse hooks, Codex (and
    /// Ollama, which runs through it) loads execpolicy rules and runs commands in its OS sandbox,
    /// Copilot takes <c>--deny-tool shell(...)</c> and confines paths to the worktree. These three
    /// expose neither a hook nor a deny list Orkka can drive, so an agent on them would run with
    /// the developer's full shell. They are offered only when the developer opts in on this
    /// machine — never by anything the cloud sends.
    /// </summary>
    internal static readonly string[] UnguardedProviders =
        [RunnerProviders.Gemini, RunnerProviders.Grok, RunnerProviders.OpenCode];

    /// <summary>The runner.env key listing the unguarded CLIs the developer accepted.</summary>
    internal const string AllowUnguardedKey = "ORKKA_ALLOW_UNGUARDED_PROVIDERS";

    private RunnerProviderStatus ApplyGuardPolicy(RunnerProviderStatus status)
    {
        if (!status.Authenticated || !UnguardedProviders.Contains(status.Provider))
            return status;

        var allowed = (_probes.ReadRunnerEnv(AllowUnguardedKey) ?? "")
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Any(p => p.Equals(status.Provider, StringComparison.OrdinalIgnoreCase));
        if (allowed)
            return status;

        return status with
        {
            Authenticated = false,
            Detail = UnguardedHint(status.Provider),
        };
    }

    internal static string UnguardedHint(string provider) =>
        $"{provider} is signed in, but Orkka cannot restrict what its agent runs in your shell "
        + "(no command hook or deny list it can drive), so it is off by default. To use it anyway "
        + $"on this machine, add {AllowUnguardedKey}={provider} to ~/.orkka/runner.env "
        + "(comma-separate several). Claude, Codex and Copilot run with Orkka's command guard.";

    private RunnerProviderStatus Probe(string provider)
    {
        if (provider == RunnerProviders.Ollama)
            return DetectOllama();
        if (provider == RunnerProviders.OpenCode)
            return DetectOpenCode();

        var cliFound = CliOnPath(provider);
        var authenticated = HasCredentials(provider);

        if (provider is RunnerProviders.Copilot or RunnerProviders.Grok)
            return ProbeStructural(provider, cliFound, authenticated);

        // Google closed the gemini CLI's free personal OAuth tier (2026-06-18), so the
        // old "run `gemini` and sign in" remedy dead-ends in IneligibleTierError for
        // personal accounts — point unauthenticated users at API-key auth instead, and
        // say so specifically when a Google sign-in IS on disk (a user who "logged in to
        // manage the API key" reads a bare "not signed in" as a lie).
        // Claude's logged-out state is usually the refresh-token race (parallel agent
        // runs), so its remedy names the durable fix alongside the re-login.
        var detail = cliFound && !authenticated
            ? provider switch
            {
                RunnerProviders.Gemini => HasGeminiOAuthCredentials()
                    ? GeminiOAuthUnverifiedHint
                    : GeminiApiKeyHint,
                RunnerProviders.Claude => ClaudeSetupTokenHint,
                _ => null,
            }
            : null;
        return new(provider, cliFound, authenticated, Detail: detail);
    }

    /// <summary>
    /// Copilot and Grok have no credential file the runner can read with certainty, so
    /// their probes are structural and inconclusive when they come back false. Two rules
    /// make them honest anyway. First, never authenticated without the CLI: an extension
    /// or a stray config dir cannot vouch for a CLI that is not installed (the VS Code
    /// Copilot extension writes ~/.copilot/{ide,logs,config.json}, which read as "copilot
    /// connected" on a machine with no copilot CLI at all). Second, a clean run recorded by
    /// <see cref="ProviderAuthOverrides"/> is proof the probe cannot produce, and counts.
    /// Beyond that the two differ: Copilot without evidence is NOT signed in — its login
    /// is one command, and a wrong "true" costs a failed run per attempt — while Grok
    /// still fails open, with the caveat in Detail, because its CLI is expected to keep
    /// the token in the OS store and the run-time feedback corrects a wrong "true" on the
    /// first failure.
    /// </summary>
    private RunnerProviderStatus ProbeStructural(string provider, bool cliFound, bool structural)
    {
        if (!cliFound)
            return new(provider, false, false);

        if (structural || overrides?.HasRecordedSuccess(provider) == true)
            return new(provider, true, true);

        return provider == RunnerProviders.Grok
            ? new(provider, true, true, Detail: GrokUnverifiedHint)
            : new(provider, true, false, Detail: CopilotLoginHint);
    }

    internal const string CopilotLoginHint =
        "the copilot CLI is not signed in on this machine — run `copilot login` (or "
        + "`gh auth login`; the copilot CLI also uses the GitHub CLI's login). For a login "
        + "that survives everything, add COPILOT_GITHUB_TOKEN=<a GitHub token with Copilot "
        + "access> to ~/.orkka/runner.env.";

    internal const string GrokUnverifiedHint =
        "grok's sign-in could not be verified on this machine (the CLI may keep its token in "
        + "the OS credential store), so it is offered as ready. If runs fail to authenticate, "
        + "run `grok` and complete the browser login (needs a SuperGrok or X Premium Plus "
        + "plan), or create a key at https://console.x.ai and save it as the xAI key on the "
        + "Settings page or as XAI_API_KEY=<your key> in ~/.orkka/runner.env.";

    internal const string GeminiApiKeyHint =
        "Google no longer offers the gemini CLI's free personal sign-in. Create a free API "
        + "key at https://aistudio.google.com/apikey, add GEMINI_API_KEY=<your key> to "
        + "~/.gemini/.env, and set \"selectedAuthType\": \"gemini-api-key\" in "
        + "~/.gemini/settings.json.";

    internal const string GeminiOAuthUnverifiedHint =
        "a Google sign-in was found (~/.gemini/oauth_creds.json), but it proves an account, "
        + "not a Gemini plan: Google closed the gemini CLI's free personal tier on 2026-06-18, "
        + "so that login only runs on a paid Gemini Code Assist or Workspace licence, which "
        + "Orkka cannot verify. Use an API key instead — create a free one at "
        + "https://aistudio.google.com/apikey, add GEMINI_API_KEY=<your key> to "
        + "~/.gemini/.env, and set \"selectedAuthType\": \"gemini-api-key\" in "
        + "~/.gemini/settings.json (a paid licence can create one too).";

    internal const string ClaudeSetupTokenHint =
        "the claude CLI is not logged in on this machine — run `claude /login` to sign back "
        + "in. If Orkka keeps logging you out (parallel agent runs race the CLI's single-use "
        + "refresh token), run `claude setup-token` and add CLAUDE_CODE_OAUTH_TOKEN=<token> "
        + "to ~/.orkka/runner.env — the runner then authenticates every agent with that "
        + "long-lived token.";

    public bool HasCredentials(string provider) => provider switch
    {
        RunnerProviders.Codex => HasCodexCredentials(),
        RunnerProviders.Gemini => HasGeminiCredentials(),
        RunnerProviders.Copilot => HasCopilotCredentials(),
        RunnerProviders.Ollama => DetectOllama().Authenticated,
        RunnerProviders.Grok => HasGrokCredentials(),
        RunnerProviders.OpenCode => DetectOpenCode().Authenticated,
        _ => HasFreshClaudeCredentials(),
    };

    /// <summary>The command a visible terminal should run to walk the user through login.
    /// `claude /login` executes the slash command on startup (a bare `claude` greets
    /// returning users with a cached banner even when the token is expired); `gemini` with
    /// no args offers Google sign-in on first run — dead for personal accounts since
    /// 2026-06-18 (IneligibleTierError), so the gemini Detail steers users to API-key auth
    /// and this command mainly verifies a configured key (or serves paid Code Assist /
    /// Workspace accounts, whose OAuth still works). Ollama has no login — the only
    /// terminal-fixable state is a stopped server, so its "login" starts it.</summary>
    ///
    /// One table, owned by the AgentRuntime so a failed run's timeline (see
    /// <see cref="Orkka.Modules.Execution.Agents.CliAuthFailure"/>) and this status panel
    /// quote the same command — the two used to be separate copies.</summary>
    public static string LoginCommand(string provider) =>
        Orkka.Modules.Execution.Agents.CliAuthFailure.LoginCommand(provider);

    /// <summary>
    /// Ollama readiness has three legs: the server answers /api/tags, the codex CLI (which
    /// supplies the agentic harness for Ollama models) is on PATH, and at least one model
    /// is pulled. Status field semantics are tuned for the UI's generic rendering:
    /// CliFound=true means "a terminal running `ollama serve` would fix this" (so the
    /// Connect/Start button shows), while install-something states report CliFound=false
    /// with the remedy in Detail.
    /// </summary>
    private RunnerProviderStatus DetectOllama()
    {
        var baseUrl = Orkka.Modules.Execution.Agents.OllamaEnvironment.BaseUrl;
        var models = TryListOllamaModels(baseUrl);
        var serverUp = models is not null;
        var ollamaPresent = serverUp || CliOnPath(RunnerProviders.Ollama);
        var codexPresent = CliOnPath(RunnerProviders.Codex);

        var ready = serverUp && codexPresent && models!.Length > 0;
        var detail = ready ? null
            : !ollamaPresent
                ? "Install Ollama from ollama.com, then pull a model (e.g. `ollama pull gpt-oss:20b`)."
            : !codexPresent
                ? "Ollama models are driven through the Codex CLI — install it with `npm install -g @openai/codex` (free, no OpenAI account needed)."
            : !serverUp
                ? $"The Ollama server isn't reachable at {baseUrl} — start it with `ollama serve` (or the Ollama app)."
                : "Ollama is running but has no models pulled — run e.g. `ollama pull gpt-oss:20b`.";

        var startable = ollamaPresent && codexPresent && (ready || !serverUp);
        return new(RunnerProviders.Ollama, startable, ready,
            Models: models is { Length: > 0 }
                ? [.. models.Select(m => "ollama/" + m)]
                : null,
            Detail: detail);
    }

    /// <summary>The installed model tags from GET /api/tags, or null when the server is
    /// unreachable. Never throws — this runs on every Hello and every run pre-flight.</summary>
    private string[]? TryListOllamaModels(string baseUrl)
    {
        try
        {
            var json = Http.GetStringAsync($"{baseUrl}/api/tags").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
                return [];
            return
            [
                .. models.EnumerateArray()
                    .Select(m => m.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                        ? name.GetString()
                        : null)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
            ];
        }
        catch (Exception ex)
        {
            logger.LogDebug("Ollama server not reachable at {BaseUrl}: {Message}", baseUrl, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// OpenCode readiness is Ollama's inverted: the CLI IS the harness, so what it lacks is
    /// models. Ready means "CLI on PATH AND at least one provider logged in", because
    /// `opencode auth login` is per provider and a fresh install has none — it would start
    /// happily and then fail every run. Status fields follow the same UI convention as
    /// Ollama: CliFound=true means a terminal could fix this (so the Connect button shows),
    /// while install-something states report CliFound=false with the remedy in Detail.
    ///
    /// The reported <see cref="RunnerProviderStatus.Models"/> is what makes OpenCode usable
    /// at all — it ships no models, so a static catalog cannot know what this machine can
    /// run. The list is `opencode models` narrowed to the providers actually authenticated,
    /// namespaced "opencode/&lt;provider&gt;/&lt;model&gt;" to match the catalog.
    /// </summary>
    private RunnerProviderStatus DetectOpenCode()
    {
        var cliPresent = CliOnPath(RunnerProviders.OpenCode);
        if (!cliPresent)
            return new(RunnerProviders.OpenCode, false, false,
                Detail: "Install OpenCode from opencode.ai (`npm install -g opencode-ai`), then "
                    + "sign in to a provider with `opencode auth login`.");

        var authenticated = ReadOpenCodeAuthProviders();
        if (authenticated.Count == 0)
            return new(RunnerProviders.OpenCode, true, false,
                Detail: "OpenCode is installed but no provider is signed in — run "
                    + "`opencode auth login` and pick a provider (Anthropic, OpenAI, xAI, "
                    + "OpenRouter, a local server, …). OpenCode ships no models of its own.");

        var models = TryListOpenCodeModels()
            // Only offer what this machine can actually run: `opencode models` reports the
            // registry, not the user's logins, so a model from an unauthenticated provider
            // would be selectable and then fail at run time with an auth error.
            .Where(m => authenticated.Contains(ProviderOf(m)))
            .Select(m => "opencode/" + m)
            .ToArray();

        return new(RunnerProviders.OpenCode, true, true,
            Models: models.Length > 0 ? models : null,
            Detail: null);

        static string ProviderOf(string providerSlashModel)
        {
            var slash = providerSlashModel.IndexOf('/');
            return slash > 0 ? providerSlashModel[..slash] : providerSlashModel;
        }
    }

    /// <summary>The provider ids in ~/.local/share/opencode/auth.json (its top-level keys),
    /// plus any provider whose API key is in this process's environment — OpenCode loads
    /// env vars and project .env files at startup too, so an env-only setup is genuinely
    /// authenticated even with no auth.json. Never throws: this runs on every Hello.</summary>
    internal static HashSet<string> ReadOpenCodeAuthProviders(
        string? authJson = null, Func<string, string?>? readEnv = null)
    {
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        authJson ??= TryReadOpenCodeAuthFile();
        if (authJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(authJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var entry in doc.RootElement.EnumerateObject())
                        providers.Add(entry.Name);
            }
            catch
            {
                // A malformed auth file reads as "no logins here" — the env sweep below still
                // gets its chance, and the remedy text tells the user to log in again.
            }
        }

        readEnv ??= Environment.GetEnvironmentVariable;
        foreach (var (envVar, provider) in OpenCodeEnvKeys)
            if (!string.IsNullOrWhiteSpace(readEnv(envVar)))
                providers.Add(provider);

        return providers;
    }

    /// <summary>Env var → models.dev provider id, for the providers a user is most likely to
    /// authenticate that way. Not exhaustive by design: missing one costs a user who has no
    /// auth.json a "run opencode auth login" nudge, never a broken run.</summary>
    private static readonly (string EnvVar, string Provider)[] OpenCodeEnvKeys =
    [
        ("ANTHROPIC_API_KEY", "anthropic"),
        ("OPENAI_API_KEY", "openai"),
        ("XAI_API_KEY", "xai"),
        ("GEMINI_API_KEY", "google"),
        ("GOOGLE_API_KEY", "google"),
        ("OPENROUTER_API_KEY", "openrouter"),
        ("GROQ_API_KEY", "groq"),
        ("DEEPSEEK_API_KEY", "deepseek"),
        ("MISTRAL_API_KEY", "mistral"),
    ];

    private static string? TryReadOpenCodeAuthFile()
    {
        // XDG_DATA_HOME wins when set; otherwise the documented default. Windows installs put
        // the same tree under the user profile.
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var path = Path.Combine(
            string.IsNullOrWhiteSpace(dataHome) ? Path.Combine(DefaultHome, ".local", "share") : dataHome,
            "opencode", "auth.json");

        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    // `opencode models` shells out, and the runner re-Hellos every MINUTE to refresh
    // credential status — spawning a process that often for an answer that changes when the
    // user logs into a new provider would be pure waste. Cached behind a TTL; a fresh login
    // shows up within it.
    private static readonly TimeSpan OpenCodeModelsTtl = TimeSpan.FromMinutes(5);
    private static string[]? _openCodeModels;
    private static DateTimeOffset _openCodeModelsAt;
    private static readonly Lock OpenCodeModelsLock = new();

    /// <summary>`opencode models` output: one "provider/model" per line. Empty on any
    /// failure — a missing list degrades the picker to the catalog's anchors, which is a
    /// worse experience but never a broken one.</summary>
    private string[] TryListOpenCodeModels()
    {
        lock (OpenCodeModelsLock)
        {
            if (_openCodeModels is not null
                && DateTimeOffset.UtcNow - _openCodeModelsAt < OpenCodeModelsTtl)
                return _openCodeModels;
        }

        var models = RunOpenCodeModels();

        lock (OpenCodeModelsLock)
        {
            _openCodeModels = models;
            _openCodeModelsAt = DateTimeOffset.UtcNow;
        }
        return models;
    }

    private string[] RunOpenCodeModels()
    {
        try
        {
            // ResolveLaunch, not Resolve: on Windows the CLI may be an npm .cmd shim,
            // which must go through cmd /c like every other spawn of it.
            var launch = Orkka.SharedKernel.Ai.ProviderCliResolver.ResolveLaunch(RunnerProviders.OpenCode);
            if (launch is null)
                return [];

            var psi = new System.Diagnostics.ProcessStartInfo(launch.FileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in launch.ArgumentPrefix)
                psi.ArgumentList.Add(arg);
            psi.ArgumentList.Add("models");

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return [];

            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return [];
            }

            return process.ExitCode == 0 ? ParseOpenCodeModels(stdout) : [];
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not list opencode models: {Message}", ex.Message);
            return [];
        }
    }

    /// <summary>Keeps the "provider/model" lines and drops everything else (headers, blank
    /// lines, any decoration the CLI adds). A line qualifies when it is a single token
    /// containing a slash — deliberately strict, since a false positive becomes a selectable
    /// model id that cannot run.</summary>
    internal static string[] ParseOpenCodeModels(string stdout) =>
    [
        .. stdout
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0
                && !line.Contains(' ')
                && !line.Contains('\t')
                && line.IndexOf('/') > 0
                && line.IndexOf('/') < line.Length - 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
    ];

    /// <summary>Grok Build keeps its config in ~/.grok (GROK_HOME overrides the whole path)
    /// but, like Copilot, may hold the OAuth token only in the OS credential store — there is
    /// no documented credential file to check. Structural evidence only: an XAI_API_KEY in
    /// ~/.orkka/runner.env authenticates on its own (the invoker passes it through to the CLI),
    /// and a credentials-looking file under the config dir counts. Anything less is "not
    /// verified", which <see cref="ProbeStructural"/> turns into fail-open-with-caveat when
    /// the CLI is present.</summary>
    private bool HasGrokCredentials()
    {
        if (!string.IsNullOrWhiteSpace(_probes.ReadRunnerEnv("XAI_API_KEY")))
            return true;

        return HasLoginArtifact(GrokConfigDir(Home), noiseDirs: []);
    }

    /// <summary>~/.grok, or GROK_HOME when set (the CLI honours it for the whole tree).</summary>
    internal static string GrokConfigDir(string home)
    {
        var grokHome = Environment.GetEnvironmentVariable("GROK_HOME");
        return string.IsNullOrWhiteSpace(grokHome) ? Path.Combine(home, ".grok") : grokHome;
    }

    /// <summary>
    /// Delegates to the same resolver the spawn uses, so "cliFound" cannot be true for a CLI
    /// the invoker would then fail to launch. This used to be its own PATH scan while the SDK
    /// ran separate discovery — a machine could report the provider connected and then blow up
    /// with "Claude Code not found" on the first task.
    /// </summary>
    private bool CliOnPath(string provider) => _probes.CliOnPath(provider);

    /// <summary>Detects the Windows trap where `claude` resolves to the npm .cmd/.ps1
    /// shim before (or instead of) the native claude.exe: the SDK's raw process spawn
    /// cannot drive shims with piped stdio, so every agent run hangs at the initialize
    /// handshake while hand-run commands work fine. Returns a user-facing warning, or
    /// null when the resolution is healthy. Cost a real user half a day (2026-07-09) —
    /// the machine had claude.exe in ~\.local\bin, just not on PATH.</summary>
    public static string? DetectClaudeShimTrap()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        // First hit in PATH order is what a bare `claude` spawn gets.
        var winner = pathDirs
            .SelectMany(dir => new[] { "claude.exe", "claude.cmd", "claude.ps1" }
                .Select(c => Path.Combine(dir.Trim(), c)))
            .FirstOrDefault(File.Exists);

        if (winner is null || winner.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;

        var nativeHint = File.Exists(Path.Combine(DefaultHome, ".local", "bin", "claude.exe"))
            ? $"A native claude.exe already exists at {Path.Combine(DefaultHome, ".local", "bin")} — put that directory FIRST on your user PATH."
            // Point at Anthropic's install page, not a piped script: `irm … | iex` is a pattern
            // corporate EDRs alert on, and a company may ship the CLI through its software center.
            : "Install the native Claude Code CLI (https://code.claude.com/docs/en/setup, or your company's software center) — then make sure its folder comes before the npm shim on PATH.";

        return $"claude resolves to a script shim ({winner}) — agent runs will hang at startup. {nativeHint}";
    }

    /// <summary>Test seam + fresh read: the long-lived `claude setup-token` value in the
    /// runner.env file. Read per detection (never cached) so a token the user adds while
    /// the runner is up flips the very next check.</summary>
    internal static Func<string?> ReadClaudeSetupToken { get; set; } =
        () => Cli.RunnerEnvFile.TryReadValue(
            Orkka.Modules.Execution.Agents.ClaudeSetupToken.EnvVar);

    private bool HasFreshClaudeCredentials(bool ignoreSetupToken = false)
    {
        // A configured setup-token authenticates every child on its own — the credentials
        // file's state stops mattering (it may well sit there expired after a lost refresh
        // race, which is exactly the situation the token exists to survive).
        if (!ignoreSetupToken && !string.IsNullOrWhiteSpace(ReadClaudeSetupToken()))
            return true;

        var credentialsPath = Path.Combine(Home, ".claude", ".credentials.json");
        if (!File.Exists(credentialsPath))
        {
            // macOS stores Claude Code's OAuth credentials in the Keychain, not this
            // file — a missing file there is normal for a logged-in user. This result
            // feeds the provider blocker that gates task execution, so a wrong "false"
            // blocks a working setup while a wrong "true" merely surfaces the CLI's own
            // 401 in the task timeline. Fail open.
            if (OperatingSystem.IsMacOS())
                return MacKeychainHasClaudeEntry(logger);
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(credentialsPath));
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                && oauth.TryGetProperty("expiresAt", out var expiresAt)
                && expiresAt.ValueKind == JsonValueKind.Number)
            {
                var expiry = DateTimeOffset.FromUnixTimeMilliseconds(expiresAt.GetInt64());
                if (expiry < DateTimeOffset.UtcNow)
                {
                    logger.LogWarning(
                        "Claude subscription token expired {Expiry:u} — run `claude` and `/login` to refresh. "
                        + "To stop parallel agent runs logging you out, run `claude setup-token` and add "
                        + "CLAUDE_CODE_OAUTH_TOKEN=<token> to ~/.orkka/runner.env",
                        expiry);
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not read claude credentials: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>Checks whether the macOS Keychain HAS a Claude Code credentials entry —
    /// existence only, the secret is never read (only the spawned claude CLI reads it).
    /// Returns true when the lookup can't run: better a 401 in a task timeline than
    /// blocking a logged-in user on a detection failure.</summary>
    internal static bool MacKeychainHasClaudeEntry(ILogger? logger = null)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(
                "security", "find-generic-password -s \"Claude Code-credentials\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null || !process.WaitForExit(5000))
                return true;
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            logger?.LogWarning("Keychain lookup failed ({Message}) — assuming logged in", ex.Message);
            return true;
        }
    }

    /// <summary>~/.codex/auth.json is written by `codex login`: either an OAuth `tokens`
    /// object (ChatGPT subscription) or an `OPENAI_API_KEY` field.</summary>
    private bool HasCodexCredentials()
    {
        var authPath = Path.Combine(Home, ".codex", "auth.json");
        if (!File.Exists(authPath))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(authPath));
            var root = doc.RootElement;
            return (root.TryGetProperty("tokens", out var tokens)
                       && tokens.ValueKind == JsonValueKind.Object)
                   || (root.TryGetProperty("OPENAI_API_KEY", out var key)
                       && key.ValueKind == JsonValueKind.String
                       && !string.IsNullOrWhiteSpace(key.GetString()));
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not read codex credentials: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>Gemini is ready on an API key, and only on one: GEMINI_API_KEY in the CLI's
    /// own ~/.gemini/.env dotenv file, selectedAuthType "gemini-api-key" in
    /// ~/.gemini/settings.json, or ORKKA_GEMINI_API_KEY in ~/.orkka/runner.env. A Google sign-in
    /// (~/.gemini/oauth_creds.json) deliberately does NOT count: any Google account can sign
    /// the CLI in — signing in to manage an API key is enough — and the file says nothing
    /// about whether that account holds the paid Code Assist / Workspace licence the CLI
    /// needs since the free personal tier closed (2026-06-18). Counting it showed "Gemini
    /// connected" on a machine with no Gemini plan at all (prod, 2026-09-15), and the
    /// picker then promised a free runner run that would die in IneligibleTierError. The
    /// key legs stay structural and fail open in spirit (like Copilot): the runner never
    /// verifies the key's validity — a configured-but-broken key surfaces the CLI's own
    /// error in the task timeline, which beats blocking a working setup.</summary>
    private bool HasGeminiCredentials()
    {
        var geminiDir = Path.Combine(Home, ".gemini");
        return HasGeminiApiKeyInEnvFile(geminiDir)
            || GeminiSettingsSelectApiKeyAuth(geminiDir)
            // ORKKA_GEMINI_API_KEY in ~/.orkka/runner.env also authenticates the CLI: the
            // dotenv guard the runner writes at every workspace root injects it as
            // GEMINI_API_KEY when ~/.gemini/.env carries no key of its own.
            || !string.IsNullOrWhiteSpace(_probes.ReadRunnerEnv("ORKKA_GEMINI_API_KEY"));
    }

    /// <summary>~/.gemini/oauth_creds.json holds a usable-looking Google token set: a
    /// refresh token, or an access token not yet past its expiry_date (unix ms). Read only
    /// to word the Detail — see <see cref="HasGeminiCredentials"/> for why it is not
    /// readiness.</summary>
    private bool HasGeminiOAuthCredentials()
    {
        var credsPath = Path.Combine(Home, ".gemini", "oauth_creds.json");
        if (!File.Exists(credsPath))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(credsPath));
            var root = doc.RootElement;

            if (root.TryGetProperty("refresh_token", out var refresh)
                && refresh.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(refresh.GetString()))
                return true;

            if (root.TryGetProperty("expiry_date", out var expiryDate)
                && expiryDate.ValueKind == JsonValueKind.Number)
            {
                var expiry = DateTimeOffset.FromUnixTimeMilliseconds(expiryDate.GetInt64());
                if (expiry > DateTimeOffset.UtcNow)
                    return true;

                logger.LogDebug(
                    "Gemini OAuth token expired {Expiry:u} with no refresh token — ignored, readiness is API-key only",
                    expiry);
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not read gemini credentials: {Message}", ex.Message);
            return false;
        }
    }

    private bool HasGeminiApiKeyInEnvFile(string geminiDir)
    {
        var envPath = Path.Combine(geminiDir, ".env");
        if (!File.Exists(envPath))
            return false;

        try
        {
            return GeminiEnvHasApiKey(File.ReadAllText(envPath));
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not read gemini .env: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>Scans dotenv text for a non-blank GEMINI_API_KEY (or GOOGLE_API_KEY — the
    /// CLI honors both). Minimal dotenv rules: `#` comments, optional `export ` prefix,
    /// optional single/double quotes around the value. The value is only length-checked,
    /// never held onto — the CLI loads the file itself at spawn time (the runner's env
    /// sanitizer blanks these variables by design, so a process-env key would not work).</summary>
    internal static bool GeminiEnvHasApiKey(string envText)
    {
        foreach (var rawLine in envText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line["export ".Length..].TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            if (key is not ("GEMINI_API_KEY" or "GOOGLE_API_KEY"))
                continue;

            var value = line[(eq + 1)..].Trim().Trim('"', '\'');
            if (!string.IsNullOrWhiteSpace(value))
                return true;
        }

        return false;
    }

    private bool GeminiSettingsSelectApiKeyAuth(string geminiDir)
    {
        var settingsPath = Path.Combine(geminiDir, "settings.json");
        if (!File.Exists(settingsPath))
            return false;

        try
        {
            return GeminiSettingsSelectApiKey(File.ReadAllText(settingsPath));
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not read gemini settings.json: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>True when settings.json selects API-key auth: root `selectedAuthType`
    /// (settings schema v1) or `security.auth.selectedType` (v2+) equals "gemini-api-key".
    /// The CLI tolerates comments and trailing commas in this file, so parsing must too.</summary>
    internal static bool GeminiSettingsSelectApiKey(string json)
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var doc = JsonDocument.Parse(json, options);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (root.TryGetProperty("selectedAuthType", out var v1)
            && v1.ValueKind == JsonValueKind.String
            && v1.GetString() == "gemini-api-key")
            return true;

        return root.TryGetProperty("security", out var security)
            && security.ValueKind == JsonValueKind.Object
            && security.TryGetProperty("auth", out var auth)
            && auth.ValueKind == JsonValueKind.Object
            && auth.TryGetProperty("selectedType", out var v2)
            && v2.ValueKind == JsonValueKind.String
            && v2.GetString() == "gemini-api-key";
    }

    /// <summary>The env vars the copilot CLI takes a GitHub token from, in its own precedence
    /// order. A value in the runner's runner.env is the durable non-interactive login — the
    /// invoker re-applies it to the spawned CLI after the sanitizer.</summary>
    internal static readonly string[] CopilotTokenEnvVars =
        ["COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"];

    /// <summary>Copilot sign-in evidence, strongest first: a GitHub token in ~/.orkka/runner.env;
    /// a gh CLI login (the copilot CLI falls back to gh's token); a login artifact under
    /// ~/.copilot (COPILOT_HOME overrides the whole path). `copilot login` may store its
    /// OAuth token ONLY in the OS credential store, so the artifact leg looks for files a
    /// login would plausibly write, never for the dir merely existing — the VS Code
    /// extension creates ~/.copilot/{ide,logs,config.json} on a machine that has never
    /// run `copilot login`, and that used to read as signed in.</summary>
    private bool HasCopilotCredentials()
    {
        if (CopilotTokenEnvVars.Any(v => !string.IsNullOrWhiteSpace(_probes.ReadRunnerEnv(v))))
            return true;

        if (GhLoggedIn())
            return true;

        return HasLoginArtifact(CopilotConfigDir(Home), ProviderCredentialFiles.CopilotNoiseDirs);
    }

    /// <summary>~/.copilot, or COPILOT_HOME when set.</summary>
    internal static string CopilotConfigDir(string home)
    {
        var copilotHome = Environment.GetEnvironmentVariable("COPILOT_HOME");
        return string.IsNullOrWhiteSpace(copilotHome) ? Path.Combine(home, ".copilot") : copilotHome;
    }

    // `gh auth token` reads a keyring, but it is still a process spawn, and Detect runs
    // once per provider per Hello plus once per run pre-flight. Cached for a short TTL and
    // reset at the top of every DetectAll pass, so a pass costs at most one spawn and a
    // fresh `gh auth login` shows within the TTL either way.
    private static readonly TimeSpan GhCheckTtl = TimeSpan.FromSeconds(30);
    private bool? _ghLoggedIn;
    private DateTimeOffset _ghCheckedAt;

    private bool GhLoggedIn()
    {
        if (_ghLoggedIn is { } cached && DateTimeOffset.UtcNow - _ghCheckedAt < GhCheckTtl)
            return cached;

        bool result;
        try
        {
            result = _probes.GhLoggedIn();
        }
        catch (Exception ex)
        {
            logger.LogDebug("gh login check failed: {Message}", ex.Message);
            result = false;
        }

        _ghLoggedIn = result;
        _ghCheckedAt = DateTimeOffset.UtcNow;
        return result;
    }

    /// <summary>File-name fragments that mark a file as something a login wrote. Matched
    /// against files directly in the config dir and one level below it; noise dirs are
    /// skipped because other tools write there on every run.</summary>
    internal static readonly string[] CredentialFileMarkers = ["auth", "token", "session", "credential"];

    /// <summary>True when the config dir holds a credentials-looking FILE. Directory names
    /// never count (a `session-state/` tree is run history, not a login), nor does the dir
    /// merely existing, nor a bare config.json.</summary>
    internal bool HasLoginArtifact(string configDir, string[] noiseDirs)
    {
        try
        {
            if (!Directory.Exists(configDir))
                return false;

            if (Directory.EnumerateFiles(configDir).Any(LooksLikeCredentialFile))
                return true;

            foreach (var sub in Directory.EnumerateDirectories(configDir))
            {
                if (noiseDirs.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                    continue;
                if (Directory.EnumerateFiles(sub).Any(LooksLikeCredentialFile))
                    return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not inspect {ConfigDir}: {Message}", configDir, ex.Message);
            return false;
        }
    }

    internal static bool LooksLikeCredentialFile(string path)
    {
        var name = Path.GetFileName(path);
        return CredentialFileMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private string Home => _probes.Home();

    private static string DefaultHome =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

/// <summary>
/// The legs of <see cref="ProviderDetection"/> that touch this machine, swappable so tests
/// can stage a machine (temp home, no gh, no CLI, an empty runner.env) without mutating the
/// process — the runner's real ~/.orkka/runner.env may well hold a token of the developer's.
/// </summary>
public sealed class ProviderDetectionProbes
{
    public static readonly ProviderDetectionProbes Default = new();

    /// <summary>A value from ~/.orkka/runner.env, fresh on every call so a token added while
    /// the runner is up flips the very next check.</summary>
    public Func<string, string?> ReadRunnerEnv { get; init; } = Cli.RunnerEnvFile.TryReadValue;

    /// <summary>Whether the gh CLI holds a github.com login.</summary>
    public Func<bool> GhLoggedIn { get; init; } = () => GhCliCredentials.IsLoggedIn();

    /// <summary>Whether the provider's CLI resolves — the same resolver the spawn uses.</summary>
    public Func<string, bool> CliOnPath { get; init; } =
        provider => Orkka.SharedKernel.Ai.ProviderCliResolver.Resolve(provider) is not null;

    /// <summary>The user's home directory (the root of every per-CLI config dir).</summary>
    public Func<string> Home { get; init; } =
        () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Where the AI sign-in variables come from (workspace, runner.env, startup env).</summary>
    public Func<Orkka.Modules.Execution.Agents.IProviderAuthSource?> AuthSource { get; init; } =
        () => Orkka.Modules.Execution.Agents.ProviderAuthEnvironment.Source;

    /// <summary>Claude Code's config dir: CLAUDE_CONFIG_DIR, else {home}/.claude.</summary>
    public Func<string> ClaudeConfigDir { get; init; }

    /// <summary>Codex's home: CODEX_HOME (as the runner was started), else {home}/.codex.</summary>
    public Func<string> CodexHome { get; init; }

    /// <summary>The text of Codex's config.toml, or null.</summary>
    public Func<string?> CodexConfigToml { get; init; }

    /// <summary>`codex login status` reduced to "chatgpt" / "api-key" / null (cached).</summary>
    public Func<string?> CodexLoginStatus { get; init; } = CliProbes.CodexLoginStatus;

    /// <summary>GH_HOST / COPILOT_GH_HOST as the runner's environment has them.</summary>
    public Func<string?> GitHubHostFromEnvironment { get; init; } = () =>
        Environment.GetEnvironmentVariable("COPILOT_GH_HOST") is { Length: > 0 } copilotHost
            ? copilotHost
            : Environment.GetEnvironmentVariable("GH_HOST");

    /// <summary>The installed CLI's version (`&lt;cli&gt; --version`, cached per binary).</summary>
    public Func<string, string?> CliVersion { get; init; } = CliProbes.Version;

    /// <summary>Whether `&lt;cli&gt; --version` says the CLI itself is not installed (a stub).</summary>
    public Func<string, bool> CliIsStub { get; init; } = CliProbes.IsStub;

    public ProviderDetectionProbes()
    {
        // Lambdas read Home at call time, so a test that only swaps Home stages every dir.
        ClaudeConfigDir = () =>
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
                ? dir
                : Path.Combine(Home(), ".claude");
        CodexHome = () =>
            AuthSource()?.MachineValues(["CODEX_HOME"]).GetValueOrDefault("CODEX_HOME") is { Length: > 0 } dir
                ? dir
                : Path.Combine(Home(), ".codex");
        CodexConfigToml = () =>
        {
            try
            {
                var path = Path.Combine(CodexHome(), "config.toml");
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch
            {
                return null;
            }
        };
    }
}

/// <summary>
/// The two process-spawning probes behind <see cref="ProviderDetectionProbes"/>, cached so a
/// Hello every minute does not spawn CLIs every minute. Neither ever logs what the CLI printed.
/// </summary>
internal static partial class CliProbes
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, (DateTime Written, string? Version, bool Stub)> Versions = new(StringComparer.Ordinal);
    private static (DateTimeOffset At, string? Value)? _codexLogin;
    private static readonly TimeSpan CodexLoginTtl = TimeSpan.FromMinutes(5);

    /// <summary>`&lt;cli&gt; --version` → the first version-shaped token, cached by binary path and
    /// its write time (an update re-probes). Null when it cannot be read in 5 s.</summary>
    public static string? Version(string provider) => Probe(provider).Version;

    /// <summary>True when `--version` answered that the CLI is not installed — a launcher stub.</summary>
    public static bool IsStub(string provider) => Probe(provider).Stub;

    private static (string? Version, bool Stub) Probe(string provider)
    {
        var path = Orkka.SharedKernel.Ai.ProviderCliResolver.Resolve(provider);
        if (path is null)
            return (null, false);
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(path); } catch { written = default; }
        lock (Gate)
        {
            if (Versions.TryGetValue(path, out var cached) && cached.Written == written)
                return (cached.Version, cached.Stub);
        }
        var output = Run(provider, ["--version"]);
        var stub = output is not null
                   && (output.Contains("was not found", StringComparison.OrdinalIgnoreCase)
                       || output.Contains("is not installed", StringComparison.OrdinalIgnoreCase));
        var match = output is null || stub ? null : VersionToken().Match(output);
        var version = match is { Success: true } ? match.Value : null;
        lock (Gate)
            Versions[path] = (written, version, stub);
        return (version, stub);
    }

    /// <summary>`codex login status`, reduced to the method. Only consulted when
    /// ~/.codex/auth.json is absent (a keyring-stored login).</summary>
    public static string? CodexLoginStatus()
    {
        lock (Gate)
        {
            if (_codexLogin is { } cached && DateTimeOffset.UtcNow - cached.At < CodexLoginTtl)
                return cached.Value;
        }
        var output = Run(RunnerProviders.Codex, ["login", "status"]);
        string? value = output is null ? null
            : output.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ? "chatgpt"
            : output.Contains("API key", StringComparison.OrdinalIgnoreCase) ? "api-key"
            : null;
        lock (Gate)
            _codexLogin = (DateTimeOffset.UtcNow, value);
        return value;
    }

    private static string? Run(string provider, string[] args)
    {
        try
        {
            var launch = Orkka.SharedKernel.Ai.ProviderCliResolver.ResolveLaunch(provider);
            if (launch is null)
                return null;
            var psi = new System.Diagnostics.ProcessStartInfo(launch.FileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in launch.ArgumentPrefix)
                psi.ArgumentList.Add(arg);
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            // Same sanitized environment an agent spawn gets: a probe must not see a secret either.
            foreach (var (name, value) in Orkka.Modules.Execution.Agents.AgentEnvironment.BuildSanitizedOverridesFromProcess())
                psi.Environment[name] = value;

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return null;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }
            return stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\d+\.\d+(\.\d+)?([-+.][0-9A-Za-z.\-]+)?")]
    private static partial System.Text.RegularExpressions.Regex VersionToken();
}
