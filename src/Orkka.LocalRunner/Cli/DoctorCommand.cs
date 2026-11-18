namespace Orkka.LocalRunner.Cli;

using System.Diagnostics;
using Microsoft.Extensions.Options;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Execution.Agents;
using Orkka.SharedKernel.Ai;

/// <summary>
/// `./orkka doctor` — runs ONE minimal agent query through the exact same
/// invoker/SDK spawn path that chat and task runs use, with full tracing. Exists
/// because "claude works when I run it, but Orkka runs fail" is undebuggable from
/// user reports alone: this makes the runner incriminate itself on the machine
/// where it fails.
/// </summary>
public static class DoctorCommand
{
    public static async Task<int> RunAsync(string? providerArg = null)
    {
        Console.WriteLine("Orkka runner doctor — tracing one agent run through the runner's spawn path.");
        Console.WriteLine($"OS: {Environment.OSVersion}  Machine: {Environment.MachineName}  PID: {Environment.ProcessId}");
        PrintWorkspaces(Console.Out, RunnerEnvFile.OrkkaDir);
        PrintLegacyInstall(Console.Out, RunnerEnvFile.OrkkaDir);
        Console.WriteLine();

        using var loggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss.fff ";
            })
            .SetMinimumLevel(LogLevel.Debug));

        // Which CLI to probe: explicit arg, else the first connected provider, else claude.
        var detection = new ProviderDetection(loggerFactory.CreateLogger<ProviderDetection>());
        var statuses = detection.DetectAll();
        var cliKey = providerArg is not null && RunnerProviders.All.Contains(providerArg)
            ? providerArg
            : statuses.FirstOrDefault(p => p.Authenticated)?.Provider ?? RunnerProviders.Claude;
        if (providerArg is not null && providerArg != cliKey)
        {
            Console.Error.WriteLine(
                $"Unknown provider '{providerArg}' — use {string.Join(", ", RunnerProviders.All)}.");
            return 1;
        }
        Console.WriteLine($"Probing provider: {cliKey}");

        // The two model-discovering providers have readiness that a PATH probe cannot show:
        // Ollama needs its server up and a model pulled, OpenCode needs a provider logged in.
        // Surface it before the spawn so a failure below is attributable.
        if (RunnerProviders.DiscoversModels.Contains(cliKey))
        {
            var status = statuses.First(p => p.Provider == cliKey);
            Console.WriteLine(status.Authenticated
                ? $"{cliKey} ready — models: {string.Join(", ", status.Models ?? [])}"
                : $"{cliKey} NOT ready: {status.Detail}");
        }

        // 1. Where does PATH resolve the CLI (as the SDK spawn will see it)? Ollama runs
        // spawn the codex CLI, so that's the binary whose resolution matters here.
        var spawnCli = cliKey == RunnerProviders.Ollama ? RunnerProviders.Codex : cliKey;
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        string[] candidates = OperatingSystem.IsWindows()
            ? [$"{spawnCli}.cmd", $"{spawnCli}.exe", $"{spawnCli}.ps1", spawnCli]
            : [spawnCli];
        var hits = pathDirs
            .SelectMany(d => candidates.Select(c => Path.Combine(d.Trim(), c)))
            .Where(File.Exists)
            .Take(3)
            .ToList();
        Console.WriteLine(hits.Count > 0
            ? $"{spawnCli} resolved on PATH:\n  {string.Join("\n  ", hits)}"
            : $"{spawnCli} NOT FOUND on this process's PATH — that alone explains failing runs.");
        if (cliKey == RunnerProviders.Claude
            && ProviderDetection.DetectClaudeShimTrap() is { } shimWarning)
            Console.WriteLine($"\n*** {shimWarning}");
        if (cliKey == RunnerProviders.Claude)
        {
            // Program.cs installs the stack-env source before dispatching doctor, so this
            // reflects exactly what the probe below (and every real run) will use.
            Console.WriteLine(ClaudeSetupToken.Resolve() is null
                ? "Auth: ~/.claude credentials (no setup token). Parallel runs can race the CLI's "
                  + "single-use refresh token and log you out — if that keeps happening, run "
                  + "`claude setup-token` and add CLAUDE_CODE_OAUTH_TOKEN=<token> to ~/.orkka/runner.env."
                : "Auth: long-lived setup token (CLAUDE_CODE_OAUTH_TOKEN in ~/.orkka/runner.env) — "
                  + "children authenticate with it instead of the shared ~/.claude credentials file.");
        }

        // 2. Which env vars will the sanitizer blank for the child? (names only — the
        // whole point of the sanitizer is that values never travel anywhere.)
        var overrides = AgentEnvironment.BuildSanitizedOverridesFromProcess().ToList();
        Console.WriteLine();
        Console.WriteLine($"Env vars blanked for the agent child ({overrides.Count}):");
        Console.WriteLine($"  {string.Join(", ", overrides.Select(o => o.Key))}");
        Console.WriteLine();

        // 2b. How each CLI signs in, and which sign-in variables come back for the probed one
        // (names only — a value is never printed).
        Console.WriteLine("AI CLIs on this machine:");
        foreach (var s in statuses.Where(s => s.CliFound || s.AuthMode is not null))
            Console.WriteLine($"  {s.Provider,-9} {(s.CliVersion ?? "-"),-14} {(s.Authenticated ? "ready" : "not ready"),-10} "
                + $"{s.AuthMode ?? "-"}{(s.AuthDetail is null ? "" : $" ({s.AuthDetail})")}");
        var probedProvider = cliKey switch
        {
            RunnerProviders.Codex => Orkka.Modules.Agents.Contracts.AiProvider.OpenAI,
            RunnerProviders.Copilot => Orkka.Modules.Agents.Contracts.AiProvider.GitHubCopilot,
            _ => Orkka.Modules.Agents.Contracts.AiProvider.Anthropic,
        };
        var passed = ProviderAuthEnvironment.Resolve(probedProvider, workspace: null);
        Console.WriteLine($"Sign-in variables handed to {cliKey}: {passed}");
        Console.WriteLine();

        // 3. The real thing: one tiny run through the SAME invoker agent runs use.
        var limits = Options.Create(new AgentLimitsOptions());
        var audit = new ConsoleAuditLog(loggerFactory.CreateLogger<ConsoleAuditLog>());
        IAiAgentInvoker invoker = cliKey switch
        {
            "codex" => new CodexAgentInvoker(limits, audit, loggerFactory.CreateLogger<CodexAgentInvoker>()),
            "gemini" => new GeminiAgentInvoker(limits, audit, loggerFactory.CreateLogger<GeminiAgentInvoker>()),
            "copilot" => new CopilotAgentInvoker(limits, audit, loggerFactory.CreateLogger<CopilotAgentInvoker>()),
            "ollama" => new OllamaAgentInvoker(limits, audit, loggerFactory.CreateLogger<OllamaAgentInvoker>()),
            "grok" => new GrokAgentInvoker(limits, audit, loggerFactory.CreateLogger<GrokAgentInvoker>()),
            "opencode" => new OpenCodeAgentInvoker(limits, audit, loggerFactory.CreateLogger<OpenCodeAgentInvoker>()),
            _ => new ClaudeAgentInvoker(
                new ClaudeAgentClient(),
                Options.Create(new AnthropicOptions { DefaultModel = "" }),
                limits,
                Options.Create(new AgentSandboxOptions()),
                audit,
                // Doctor runs a fixed probe prompt with no session and nobody to ask.
                new DenyingAgentApprovalGate(),
                loggerFactory.CreateLogger<ClaudeAgentInvoker>()),
        };
        var provider = cliKey switch
        {
            "codex" => AiProvider.OpenAI,
            "gemini" => AiProvider.Google,
            "copilot" => AiProvider.GitHubCopilot,
            "ollama" => AiProvider.Ollama,
            "grok" => AiProvider.XAI,
            "opencode" => AiProvider.OpenCode,
            _ => AiProvider.Anthropic,
        };

        var workDir = Path.Combine(Path.GetTempPath(), $"orkka-doctor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        Console.WriteLine($"cwd for the run: {workDir}");
        Console.WriteLine($"Spawning the {cliKey} CLI through the invoker (identical to chat/task runs)...");
        Console.WriteLine();

        var request = new AgentRequest(
            SystemPrompt: "You are a diagnostic probe. Reply with exactly: OK",
            UserMessage: "Reply with exactly: OK",
            WorktreePath: workDir,
            Provider: provider,
            // A model-discovering provider's tier default may not exist on this machine
            // (an Ollama tag that was never pulled, an OpenCode provider never logged into)
            // — probe with whatever the runner actually reports first.
            // Deliberately the SHIPPED tier defaults, not the admin-managed catalog: the
            // runner CLI runs on the user's machine with no database access. A probe model
            // that differs from the platform's current default is fine — this only checks
            // that the provider's CLI is authenticated and responding.
            Model: RunnerProviders.DiscoversModels.Contains(cliKey)
                ? statuses.First(p => p.Provider == cliKey).Models?.FirstOrDefault()
                    ?? ProviderModelDefaults.FastModel(provider)
                : ProviderModelDefaults.FastModel(provider),
            MaxTurns: 2,
            AgentName: "doctor",
            AgentRole: "diagnostic");

        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await foreach (var chunk in invoker.InvokeAsync(request, cts.Token))
            {
                var content = chunk.Content.Length > 200 ? chunk.Content[..200] + "…" : chunk.Content;
                Console.WriteLine($"[{sw.Elapsed.TotalSeconds,6:F1}s] {chunk.Type}: {content.ReplaceLineEndings(" ")}");
            }
            Console.WriteLine();
            Console.WriteLine($"[{sw.Elapsed.TotalSeconds,6:F1}s] SUCCESS — the runner's spawn path works on this machine.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine($"[{sw.Elapsed.TotalSeconds,6:F1}s] TIMED OUT after 120s with no result — the spawn is hanging on this machine.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"[{sw.Elapsed.TotalSeconds,6:F1}s] FAILED: {ex.GetType().Name}: {ex.Message}");
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                Console.WriteLine($"  inner: {inner.GetType().Name}: {inner.Message}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The workspaces `connect` would serve — host, git author, tracker count. Never
    /// the token.</summary>
    /// <summary>What the old installer left behind (PATH entry, shell lines, folders), with the
    /// manual steps to remove it. Report only — nothing is changed.</summary>
    internal static void PrintLegacyInstall(TextWriter output, string orkkaRoot)
    {
        var findings = LegacyInstall.Find(orkkaRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());
        foreach (var finding in findings)
            output.WriteLine($"Old install: {finding}");
    }

    internal static void PrintWorkspaces(TextWriter output, string orkkaRoot)
    {
        WorkspaceProfiles.MigrateLegacy(orkkaRoot);
        var profiles = WorkspaceProfiles.List(orkkaRoot);
        if (profiles.Count == 0)
        {
            output.WriteLine("Workspaces: none — run `./orkka start`: it gives every workspace in Orkka a profile on this machine.");
            return;
        }
        output.WriteLine($"Workspaces ({profiles.Count}):");
        foreach (var p in profiles)
        {
            var trackers = 0;
            try { trackers = new Trackers.TrackerStore(orkkaRoot, p.Name).List().Count; }
            catch { /* unreadable trackers.json — shown as 0 */ }
            output.WriteLine($"  {p.Name}: {p.HostUrl}, git author "
                + (p.HasIdentity ? $"{p.GitAuthorName} <{p.GitAuthorEmail}>" : "from git config")
                + $", {trackers} tracker connection(s)");
        }
    }
}
