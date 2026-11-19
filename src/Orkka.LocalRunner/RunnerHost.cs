namespace Orkka.LocalRunner;

using Microsoft.Extensions.Logging.Console;
using Orkka.Git;
using Orkka.LocalRunner.Cli;
using Orkka.Modules.Execution.Agents;
using Orkka.SharedKernel.Ai;

/// <summary>What the runner reads from the machine before its environment is scrubbed.</summary>
/// <param name="MaxConcurrentRuns">Agent runs at once across every workspace
/// (<c>ORKKA_MAX_CONCURRENT_RUNS</c>, default 2).</param>
/// <param name="RepoBasePath">Where mirrors and task clones live (<c>~/.orkka/repos</c>).</param>
public sealed record RunnerMachineSettings(int MaxConcurrentRuns, string RepoBasePath);

/// <summary>
/// The process-wide preparation the runner needs before anything spawns an agent. Run once per
/// process, before the runner (or <c>doctor</c>) is built — <c>./orkka start</c> runs it after the
/// web host has read its configuration and before the runner starts.
/// </summary>
public static class RunnerProcess
{
    private static RunnerMachineSettings? _prepared;

    /// <summary>
    /// Captures the settings and sign-ins the runner needs, then deletes every sensitive variable
    /// from this process's environment (agents inherit it), stops git from ever prompting, and
    /// arms the Gemini dotenv guard and the Claude setup-token source. Idempotent.
    /// </summary>
    public static RunnerMachineSettings PrepareEnvironment(Action<string>? notice = null)
    {
        if (_prepared is not null)
            return _prepared;

        // ── Environment scrub ────────────────────────────────────────────────────
        // The claude CLI children inherit this process's environment. Delete (not blank)
        // everything sensitive, so no secret can leak into an agent and no stray
        // ANTHROPIC_API_KEY overrides subscription auth. Config values needed by the runner
        // itself are captured first.
        Trackers.WorkspaceSecretStores.CaptureOverride();
        var maxConcurrentRuns = MachineRunSlots.ParseCapacity(
            Environment.GetEnvironmentVariable(MachineRunSlots.SettingName) ?? RunnerEnvFile.TryReadValue(MachineRunSlots.SettingName));
        var repoBasePath = Environment.GetEnvironmentVariable("ORKKA_REPO_CACHE")
            ?? Path.Combine(RunnerEnvFile.OrkkaDir, "repos");

        // AI sign-in passthrough: the allowlisted sign-in variables the runner was started with
        // (an ANTHROPIC_API_KEY, CLAUDE_CODE_USE_BEDROCK + AWS_REGION, a GH_TOKEN, …) are captured
        // HERE, before the scrub deletes them from this process. They never return to the process
        // environment: ProviderAuthEnvironment hands each provider's own names to that provider's
        // CLI spawn only, after the sanitizer — workspace overrides (`workspace set-env`) first,
        // then ~/.orkka/runner.env, then this snapshot.
        var startupAuthEnvironment = RunnerProviderAuthSource.CaptureStartupEnvironment(Environment.GetEnvironmentVariables());
        ProviderAuthEnvironment.Source = new RunnerProviderAuthSource(
            RunnerEnvFile.OrkkaDir, startupAuthEnvironment, RunnerEnvFile.TryReadValue);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = entry.Key?.ToString();
            if (name is null)
                continue;

            // Claude-Code session markers: if the runner is launched from inside a Claude Code
            // session (a terminal it spawned, a script, CI), the child CLI would detect a nested
            // SDK session and expect auth from its "host" instead of the user's credentials file
            // (401 Invalid authentication credentials). The runner must always present a clean,
            // top-level environment to the CLIs it spawns.
            var isClaudeSessionMarker = name is "CLAUDECODE"
                || name.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("CLAUDE_AGENT_SDK_", StringComparison.OrdinalIgnoreCase);

            if (isClaudeSessionMarker || AgentEnvironment.IsSensitive(name))
                Environment.SetEnvironmentVariable(name, null);
        }

        // Git must NEVER go interactive under the runner: on Windows a credential helper or an
        // LFS smudge filter can pop an (easily missed) modal or wait on stdin, hanging worktree
        // checkouts forever. Pointer files instead of LFS blobs are an acceptable trade for
        // agent context. These inherit into every git the runner (or an agent) spawns.
        Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "0");
        Environment.SetEnvironmentVariable("GCM_INTERACTIVE", "never");
        Environment.SetEnvironmentVariable("GIT_LFS_SKIP_SMUDGE", "1");

        // Which git everything downstream will use, and the settings it must run with (long paths
        // on Windows among them). Deliberately after the scrub above: the shell path this may set
        // for Claude Code on Windows is configuration, not a session marker, and has to survive it.
        //
        // Advisory ONLY. This must never decide that a working runner cannot run: resolving an
        // executable is the operating system's job. So a "missing" verdict prints a hint and
        // carries on, and git stays exactly as resolvable as it was before.
        if (BundledGit.Activate() == GitAvailability.Missing)
        {
            notice?.Invoke(
                "Heads up: Orkka could not find git on this machine. If repository operations fail, "
                + "install git from your company's software center or https://git-scm.com/downloads.");
        }

        // ── Gemini dotenv-walk guard ─────────────────────────────────────────────
        // The gemini CLI dotenv-loads the first .env found walking up from its cwd. A controlled
        // guard file at the cache root (refreshed again at every clone/worktree op) stops the
        // walk at content Orkka owns: a mirror of the user's ~/.gemini/.env plus, when that has
        // no key, ORKKA_GEMINI_API_KEY from ~/.orkka/runner.env. Read from the FILE, never the
        // process env: the scrub above must stay authoritative.
        GeminiDotenvGuard.FallbackApiKey = RunnerEnvFile.TryReadValue("ORKKA_GEMINI_API_KEY");
        GeminiDotenvGuard.Refresh(repoBasePath);

        // ── Claude setup-token (headless-safe subscription auth) ────────────────
        // A long-lived `claude setup-token` stored as CLAUDE_CODE_OAUTH_TOKEN in
        // ~/.orkka/runner.env sidesteps the shared refresh-token race; the invoker re-applies it
        // per child AFTER the env sanitizer. Read from the FILE on every spawn.
        ClaudeSetupToken.Source = () => RunnerEnvFile.TryReadValue(ClaudeSetupToken.EnvVar);

        return _prepared = new RunnerMachineSettings(maxConcurrentRuns, repoBasePath);
    }
}

/// <summary>How <see cref="RunnerHost.Build"/> composes the runner.</summary>
public sealed class RunnerHostOptions
{
    public required RunnerMachineSettings Machine { get; init; }

    /// <summary>The workspace profiles to serve now; new ones on disk are picked up while it runs.</summary>
    public IReadOnlyList<WorkspaceProfile> Profiles { get; init; } = [];

    /// <summary>Verbose structured console logs instead of the clean end-user output.</summary>
    public bool Debug { get; init; }

    /// <summary>Extra logging providers (e.g. the log file of <c>./orkka start</c>).</summary>
    public Action<ILoggingBuilder>? ConfigureLogging { get; init; }

    /// <summary>Plain-language connection narration on the console (<see cref="ConsoleStatus"/>).</summary>
    public bool ConsoleStatus { get; init; } = true;

    /// <summary>How long a stop waits for in-flight work to drain.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>When true the host does not react to Ctrl+C / SIGTERM itself — whoever owns it
    /// stops it (<c>./orkka start</c> stops the runner before the web host).</summary>
    public bool ExternallyStopped { get; init; }
}

/// <summary>
/// The runner: one <see cref="RunnerSession"/> per workspace profile, each dialing the Orkka
/// host's <c>/hubs/runner</c> with that workspace's <c>orkr_</c> token, executing everything that
/// needs this machine — git with the developer's own credentials, the AI CLIs with the
/// developer's own subscriptions. Its own composition root, separate from the web host's.
/// </summary>
public static class RunnerHost
{
    public static IHost Build(RunnerHostOptions options)
    {
        // No args: the generic host must not re-parse the CLI's arguments as configuration.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            DisableDefaults = false,
        });
        if (options.ExternallyStopped)
            builder.Services.AddSingleton<IHostLifetime, ExternalLifetime>();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = options.ShutdownTimeout);

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // GitWorkspaceManager reads its base path from this key. The commit author is per
            // workspace (its profile.env) — RunnerSessionFactory layers it on per session.
            ["Worker:RepoBasePath"] = options.Machine.RepoBasePath,
        });

        builder.Logging.ClearProviders();
        if (options.Debug)
        {
            builder.Logging.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
            // SignalR at Debug is frame-level noise — framework categories stay at Information.
            builder.Logging.AddFilter("Microsoft", LogLevel.Information);
            builder.Logging.AddFilter("System", LogLevel.Information);
        }
        else
        {
            // End-user mode: warnings/errors only on the console, rendered without categories,
            // timestamps, or stack traces so the terminal never exposes internals.
            // ConsoleStatus narrates connection state in plain language instead.
            builder.Logging.AddConsole(o => o.FormatterName = CleanConsoleFormatter.FormatterName);
            builder.Logging.AddConsoleFormatter<CleanConsoleFormatter, ConsoleFormatterOptions>();
            builder.Logging.AddFilter<ConsoleLoggerProvider>(null, LogLevel.Warning);
            builder.Logging.SetMinimumLevel(LogLevel.Information);
            builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
            builder.Logging.AddFilter("System", LogLevel.Warning);
        }
        options.ConfigureLogging?.Invoke(builder.Logging);

        // The claude CLI authenticates with the developer's own ~/.claude login; the invoker only
        // reads DefaultModel from these options.
        builder.Services.Configure<AnthropicOptions>(o => o.DefaultModel = "");
        // The library default (100 tool calls) starves models that batch many parallel
        // Read/Glob/Grep calls per turn; the config bind below still lets AgentLimits__* override.
        builder.Services.Configure<AgentLimitsOptions>(o => o.MaxToolCalls = 500);
        builder.Services.Configure<AgentLimitsOptions>(builder.Configuration.GetSection(AgentLimitsOptions.SectionName));
        builder.Services.Configure<AgentSandboxOptions>(builder.Configuration.GetSection(AgentSandboxOptions.SectionName));

        builder.Services.AddOrkkaGit(GitTopologies.UserRunner);
        builder.Services.AddSingleton<Orkka.Modules.SourceControl.Contracts.IGitWorkspaceManager,
            Orkka.Modules.Execution.Git.GitWorkspaceManager>();
        builder.Services.AddSingleton<IClaudeAgentClient, ClaudeAgentClient>();
        // Interactive sessions ask the human through the host: the agent's Bash hook runs here,
        // the person deciding is at a browser. RunnerHostRouter sends the question to the session
        // the run came from.
        builder.Services.AddSingleton<RunnerHostRouter>();
        builder.Services.AddSingleton<IAgentApprovalGate, RunnerApprovalGate>();
        // Run-time auth evidence: every invoker reports auth-shaped failures / clean completions
        // into ProviderAuthOverrides. One instance behind both registrations.
        builder.Services.AddSingleton<ProviderAuthOverrides>();
        builder.Services.AddSingleton<IProviderAuthFeedback>(sp => sp.GetRequiredService<ProviderAuthOverrides>());
        // One invoker per vendor CLI; the dispatcher picks by AgentRequest.Provider. The runner
        // is its OWN composition root, so every invoker ProviderDispatchingAgentInvoker takes
        // must be registered here — a missing one only surfaces when the runner starts.
        builder.Services.AddSingleton<ClaudeAgentInvoker>();
        builder.Services.AddSingleton<CodexAgentInvoker>();
        builder.Services.AddSingleton<GeminiAgentInvoker>();
        builder.Services.AddSingleton<CopilotAgentInvoker>();
        builder.Services.AddSingleton<OllamaAgentInvoker>();
        builder.Services.AddSingleton<GrokAgentInvoker>();
        builder.Services.AddSingleton<OpenCodeAgentInvoker>();
        builder.Services.AddSingleton<IAiAgentInvoker, ProviderDispatchingAgentInvoker>();
        builder.Services.AddSingleton<Orkka.Modules.Agents.Contracts.IAgentAuditLog, ConsoleAuditLog>();
        builder.Services.AddSingleton<ProviderDetection>();
        // The developer's say over which repository commands this machine runs (see CommandTrust).
        builder.Services.AddSingleton(new Commands.CommandApprovalStore());
        builder.Services.AddSingleton<Commands.CommandTrust>();
        builder.Services.AddSingleton<ForgeCli>();
        // Tracker clients are stateless and shared; each workspace's tracker store, secrets,
        // attachment cache and poller are built per session by RunnerSessionFactory. Attachment
        // downloads get their own client, since a 25 MB file needs longer than the API deadline.
        var trackerHttp = Trackers.TrackerHttp.CreateDefault();
        var attachmentHttp = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        builder.Services.AddSingleton(new Trackers.JiraCloudClient(trackerHttp));
        builder.Services.AddSingleton(new Trackers.AzureDevOpsClient(trackerHttp));
        builder.Services.AddSingleton(new Trackers.GitHubProjectsClient(trackerHttp));
        builder.Services.AddSingleton<Apps.AppRunManager>();
        builder.Services.AddSingleton(new ConsoleStatus(enabled: options.ConsoleStatus && !options.Debug));
        // Agent runs at once across EVERY workspace: two jobs must not overload one laptop.
        builder.Services.AddSingleton(new MachineRunSlots(options.Machine.MaxConcurrentRuns));
        builder.Services.AddSingleton(sp => new RunnerSessionFactory(
            RunnerEnvFile.OrkkaDir,
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<GitProcess>(),
            sp.GetRequiredService<IGitOperationLedger>(),
            sp.GetRequiredService<IAiAgentInvoker>(),
            sp.GetRequiredService<ProviderDetection>(),
            sp.GetRequiredService<ForgeCli>(),
            sp.GetRequiredService<Apps.AppRunManager>(),
            sp.GetRequiredService<ProviderAuthOverrides>(),
            sp.GetRequiredService<ConsoleStatus>(),
            sp.GetRequiredService<MachineRunSlots>(),
            sp.GetRequiredService<RunnerHostRouter>(),
            sp.GetRequiredService<Trackers.JiraCloudClient>(),
            sp.GetRequiredService<Trackers.AzureDevOpsClient>(),
            attachmentHttp,
            Trackers.WorkspaceSecretStores.CreateDefault,
            sp.GetRequiredService<Commands.CommandTrust>(),
            sp.GetRequiredService<Trackers.GitHubProjectsClient>()));
        // One session per workspace profile (one per job), each with its own hub connection.
        // New profiles (a workspace created in the UI, bootstrapped by `./orkka start`) are
        // picked up while the runner runs.
        builder.Services.AddHostedService(sp => new RunnerSupervisor(
            options.Profiles,
            sp.GetRequiredService<RunnerSessionFactory>(),
            sp.GetRequiredService<ILogger<RunnerSupervisor>>(),
            () => WorkspaceProfiles.List(RunnerEnvFile.OrkkaDir)));
        // Worktrees live on THIS disk, so their age-based GC has to run here.
        builder.Services.AddHostedService<WorktreeGcService>();

        // Every Claude CLI the SDK starts is booked, so a stop can end what its run's own
        // teardown did not (RunnerSupervisor → AgentProcesses.KillAll).
        ih0.Claude.Agent.SDK.Internal.Transport.SubprocessCliTransport.ProcessStarted = AgentProcesses.Track;

        return builder.Build();
    }

    /// <summary>A lifetime that ignores Ctrl+C / SIGTERM: the owner stops the runner itself, in
    /// order, before the web host it talks to.</summary>
    private sealed class ExternalLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
