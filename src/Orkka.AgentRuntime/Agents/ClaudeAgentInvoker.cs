namespace Orkka.Modules.Execution.Agents;

using System.Runtime.CompilerServices;
using System.Text.Json;
using ih0.Claude.Agent.SDK;
using ih0.Claude.Agent.SDK.Exceptions;
using ih0.Claude.Agent.SDK.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orkka.SharedKernel.Ai;

public sealed class ClaudeAgentInvoker(
    IClaudeAgentClient client,
    IOptions<AnthropicOptions> anthropicOptions,
    IOptions<AgentLimitsOptions> agentLimits,
    IOptions<AgentSandboxOptions> agentSandbox,
    IAgentAuditLog auditLog,
    // Answers approval prompts from interactive turns. Hosts that never run sessions leave the
    // denying default registered — see IAgentApprovalGate for why the fallback refuses.
    IAgentApprovalGate approvalGate,
    ILogger<ClaudeAgentInvoker> logger,
    // Optional: only the local runner has readiness state to update (see
    // IProviderAuthFeedback); the hosted Worker leaves it null.
    IProviderAuthFeedback? authFeedback = null) : IAiAgentInvoker
{
    private readonly IProviderAuthFeedback _authFeedback =
        authFeedback ?? NoOpProviderAuthFeedback.Instance;

    /// <summary>In-process SDK invoker — AgentRequest.ToolServer registers as a real MCP
    /// server on the spawned CLI (see BuildOptions).</summary>
    public bool SupportsInProcessToolServer => true;

    private const string DefaultModel = "claude-sonnet-5";

    // The SDK passes the system prompt to claude.exe as a --system-prompt command-line
    // argument, and Windows caps the whole command line at ~32K chars (CreateProcess
    // fails with Win32Exception 206). Prompt assembly budgets its sections to stay well
    // under this — warn when a prompt slips through so the source can be fixed before
    // runs start failing outright.
    private const int SystemPromptWarnLength = 26_000;

    // Floor for CLAUDE_CODE_MAX_OUTPUT_TOKENS on utility runs. The CLI's own default for
    // current models; it counts thinking as output and FAILS the run on the cap, so this is
    // runaway insurance sized so legitimate work never touches it — never a per-call budget.
    internal const int UtilityOutputTokenFloor = 32_000;

    /// <summary>Claude Code strips credentials from the environment of every subprocess it
    /// starts (Bash, hooks, stdio MCP servers) when this is "1". Set only when an env
    /// credential is passed: it also runs Linux Bash in its own PID namespace.</summary>
    internal const string SubprocessEnvScrubVar = "CLAUDE_CODE_SUBPROCESS_ENV_SCRUB";

    private readonly AgentLimitsOptions _limits = agentLimits.Value;
    private readonly AgentSandboxOptions _sandbox = agentSandbox.Value;

    // When set (local dev with a non-Anthropic backend like Ollama), this FORCES the
    // model for every agent regardless of per-agent configuration. Leave unset in
    // production so each ProjectAgent's own model is used.
    private readonly string? _defaultModelOverride =
        string.IsNullOrWhiteSpace(anthropicOptions.Value.DefaultModel)
            ? null
            : anthropicOptions.Value.DefaultModel;

    private static readonly string[] PlanningTools = ["Read", "Glob", "Grep"];

    /// <summary>Every shell tool the CLI may offer — the command guard's hook matcher.</summary>
    internal const string ShellToolMatcher = "Bash|PowerShell";

    // Research: the read-only trio plus the open web. No Bash/Write/Edit — a phase that
    // cannot run a command needs no command guard, which is what lets briefing point at a
    // customer workspace. WebSearch is executed server-side by the API (no egress from this
    // host); WebFetch fetches from here, so it rides the sandbox's egress proxy when one is
    // configured. Kept separate from PlanningTools: the dev-pipeline Planning lane is a
    // deliberate offline read of the repo, and giving every plan run a web budget is a
    // different decision from giving briefing one.
    private static readonly string[] ResearchTools =
        ["Read", "Glob", "Grep", "WebSearch", "WebFetch"];

    /// <summary>
    /// Sub-agent fan-out, refused for chat/research runs by NAME (the allow-list above only
    /// pre-approves tools; it does not remove the CLI's own).
    ///
    /// A sub-agent runs in the background and reports back by delivering a task notification
    /// INTO the turn that spawned it. Each delivery interrupts that turn, and an interruption
    /// that lands while a platform tool call is in flight kills the call — the CLI answers it
    /// with "The tool call was interrupted before a result was received". A chat turn that had
    /// fanned out to eight sub-agents could then not save a single document, while its own
    /// instant built-in tools kept working (2026-09-08). Chat is a read-and-answer surface:
    /// the fan-out also spends the user's budget invisibly, and the answer arrives sooner
    /// without it.
    ///
    /// Both names are listed because the CLI renamed this tool (Task → Agent) and a runner may
    /// be on either version.
    /// </summary>
    internal static readonly string[] ChatDisallowedTools = ["Task", "Agent"];

    /// <summary>The tools a phase refuses outright, as opposed to the ones it pre-approves.
    /// Exposed for the test that pins chat's refusal of sub-agent fan-out.</summary>
    internal static string[] DisallowedToolsFor(AgentPhase phase) => phase switch
    {
        AgentPhase.Utility => UtilityDisallowedTools,
        AgentPhase.Research => ChatDisallowedTools,
        _ => [],
    };

    /// <summary>The tools a phase pre-approves. In every phase but Developing and Testing the
    /// CLI's own permission gate stays up (<see cref="LiftsPermissionGate"/>), so a headless run
    /// is refused anything not listed here — that, not a deny list, is what keeps planning, PM
    /// review, code review and chat (Research, also a task's Discuss chat) read-only.</summary>
    internal static string[] AllowedToolsFor(AgentPhase phase, bool apiTesting = false) => phase switch
    {
        AgentPhase.Planning => PlanningTools,
        AgentPhase.Research => ResearchTools,
        AgentPhase.Developing => DevelopingTools,
        AgentPhase.Review => PlanningTools,
        AgentPhase.Testing => apiTesting ? TestingApiTools : TestingTools,
        _ => PlanningTools
    };

    /// <summary>Only the phases that write code and run commands bypass the CLI's permission
    /// prompts (Orkka's PreToolUse guards decide instead).</summary>
    internal static bool LiftsPermissionGate(AgentPhase phase)
        => phase is AgentPhase.Developing or AgentPhase.Testing;

    /// <summary>The claude CLI's built-in tool set, stripped for <see cref="AgentPhase.Utility"/> runs.</summary>
    private static readonly string[] UtilityDisallowedTools =
        ["Task", "Bash", "Glob", "Grep", "Read", "Edit", "Write",
         "NotebookEdit", "WebFetch", "WebSearch", "TodoWrite"];
    // Beyond the file tools: TodoWrite (the model plans with it constantly and every denial
    // burns a turn), Task (subagent fan-out for large repos), WebFetch/WebSearch (docs
    // lookups while scaffolding), and BashOutput/KillShell (without them a backgrounded
    // Bash command can be started but never read back or stopped).
    private static readonly string[] DevelopingTools =
        ["Read", "Write", "Edit", "Bash", "Glob", "Grep",
         "TodoWrite", "Task", "WebFetch", "WebSearch", "BashOutput", "KillShell"];
    // QA: Bash to start the app under test, plus every tool of the Playwright MCP server
    // (the server-wide mcp__playwright form avoids chasing upstream tool renames). No
    // Write/Edit — QA reports, it does not fix. BashOutput/KillShell because the app under
    // test IS a long-running background command.
    private static readonly string[] TestingTools =
        ["Read", "Glob", "Grep", "Bash", "mcp__playwright", "TodoWrite", "BashOutput", "KillShell"];

    // API-mode QA (AgentRequest.ApiTesting): identical, minus playwright — a JSON API has no
    // browser to drive, and spawning the server would cost a process and a tool listing for
    // nothing. Still no Write/Edit: writing the tests is the DEV agent's job, and this lane
    // runs and asserts them.
    private static readonly string[] TestingApiTools =
        ["Read", "Glob", "Grep", "Bash", "TodoWrite", "BashOutput", "KillShell"];

    public async IAsyncEnumerable<AgentChunk> InvokeAsync(
        AgentRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (request.Provider != AiProvider.Anthropic)
        {
            yield return new AgentChunk(AgentChunkType.Error,
                $"Only Anthropic provider is supported with the Claude Agent SDK. Got: {request.Provider}");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(request.WorktreePath))
        {
            yield return new AgentChunk(AgentChunkType.Error,
                "WorktreePath is required — the agent must run inside a repository worktree.");
            yield break;
        }

        if (request.SystemPrompt.Length > SystemPromptWarnLength)
        {
            logger.LogWarning(
                "[Agent] System prompt is {Length} chars — approaching the ~32K Windows command-line limit for claude.exe. Phase: {Phase}, Task: {TaskId}, Agent: {AgentName}",
                request.SystemPrompt.Length, request.Phase, request.TaskId, request.AgentName);
        }

        // Capture the CLI's stderr so a non-zero exit surfaces its actual error instead of
        // the SDK's generic "Check stderr output for details".
        var stderrTail = new BoundedLineBuffer(maxChars: 4_000);
        var options = BuildOptions(request, out var costGuard, line =>
        {
            stderrTail.Append(line);
            logger.LogDebug("[Agent stderr] {Line}", line);
        });

        // Per-phase budgets (set by the orchestrator) win; ad-hoc callers that leave them
        // null get the process-wide default. MaxTurns is applied inside BuildOptions.
        var maxToolCalls = request.MaxToolCalls ?? _limits.MaxToolCalls;
        var maxWallClockSeconds = request.MaxWallClockSeconds ?? _limits.MaxWallClockSeconds;

        var model = _defaultModelOverride ?? request.Model ?? DefaultModel;
        logger.LogInformation(
            "[Agent] Starting — Phase: {Phase}, Model: {Model}, MaxTurns: {MaxTurns}, MaxToolCalls: {MaxToolCalls}, MaxWallClock: {MaxWallClock}s, Path: {WorktreePath}",
            request.Phase, model, Cap(request.MaxTurns), Cap(maxToolCalls),
            Cap(maxWallClockSeconds), request.WorktreePath);

        // Hard per-run wall-clock cap: a linked token that self-cancels after the limit guarantees the
        // run terminates even if a single turn hangs. Distinct from the caller's ct so we can report
        // a timeout cleanly rather than surfacing a raw cancellation.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (maxWallClockSeconds > 0)
            runCts.CancelAfter(TimeSpan.FromSeconds(maxWallClockSeconds));

        await AuditAsync(request, AgentAuditEventType.RunStarted,
            $"Run started — phase {request.Phase}, model {model}");

        Message? lastMessage = null;
        var turn = 0;
        var toolCalls = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var enumerator = client
            .QueryAsync(request.UserMessage, options, runCts.Token)
            .GetAsyncEnumerator(runCts.Token);

        var timedOut = false;
        string? processError = null;

        while (true)
        {
            Message message;
            try
            {
                if (!await enumerator.MoveNextAsync())
                    break;
                message = enumerator.Current;
            }
            catch (OperationCanceledException) when (runCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Our wall-clock cap fired (not the caller cancelling). Yield the timeout notice
                // after the loop — a value cannot be yielded from inside a catch clause.
                timedOut = true;
                break;
            }
            catch (ClaudeAgentException ex)
            {
                // The CLI process died (non-zero exit, parse failure, startup failure). Attach the
                // captured stderr — the SDK's own message only says to "check stderr".
                var stderr = stderrTail.ToString();
                if (string.IsNullOrWhiteSpace(stderr) && ex is ProcessException pex)
                    stderr = pex.Stderr;

                // Known failure signatures (e.g. the stream-json initialize handshake timing
                // out) get an actionable message instead of the SDK's raw one.
                var reason = ClaudeCliErrors.Describe(ex.Message)
                             ?? $"Agent process failed: {ex.Message}";

                processError = string.IsNullOrWhiteSpace(stderr)
                    ? reason
                    : $"{reason}\n--- claude CLI stderr ---\n{stderr.Trim()}";

                logger.LogError(ex,
                    "[Agent] CLI process failed — Phase: {Phase}, Task: {TaskId}. Stderr: {Stderr}",
                    request.Phase, request.TaskId, stderr);
                break;
            }

            lastMessage = message;

            switch (message)
            {
                case AssistantMessage assistant:
                    turn++;
                    logger.LogInformation("[Agent] Turn {Turn} — {BlockCount} content block(s)",
                        turn, assistant.Content.Count);

                    foreach (var chunk in ProcessAssistantMessage(assistant))
                    {
                        if (chunk.Type == AgentChunkType.ToolUse)
                        {
                            toolCalls++;
                            var colon = chunk.Content.IndexOf(':');
                            var toolName = colon > 0 ? chunk.Content[..colon].Trim() : chunk.Content;
                            await AuditAsync(request, AgentAuditEventType.ToolUse, chunk.Content, toolName);
                        }
                        yield return chunk;
                    }
                    break;

                case ResultMessage result:
                    foreach (var chunk in ProcessResultMessage(result, request, costGuard))
                        yield return chunk;
                    break;

                case SystemMessage { Subtype: "init" } init:
                    // The CLI's init event is the only host-side signal that an MCP server
                    // failed to come up — otherwise the failure lives solely in the CLI's own
                    // mcp-logs file and the run quietly proceeds without its tools.
                    LogMcpServerStatus(request, init);
                    break;
            }

            // Tool-call cap: stop a runaway loop that keeps invoking tools without converging.
            if (maxToolCalls > 0 && toolCalls >= maxToolCalls)
            {
                logger.LogWarning(
                    "[Agent] Tool-call limit of {Limit} reached after {Turns} turn(s) — stopping run.",
                    maxToolCalls, turn);
                await AuditAsync(request, AgentAuditEventType.LimitReached,
                    $"Tool-call limit {maxToolCalls} reached — run stopped");
                await runCts.CancelAsync();
                yield return new AgentChunk(AgentChunkType.Error,
                    $"Agent run reached the {maxToolCalls} tool-call limit and was stopped.");
                yield return new AgentChunk(AgentChunkType.Done, "");
                yield break;
            }
        }

        if (processError is not null)
        {
            // A 401 / "Invalid authentication credentials" exit is the expired-subscription
            // (or lost refresh-race) state — say so with the login remedy, and tell the
            // runner so it stops routing runs here until the user signs back in.
            if (CliAuthFailure.Classify(ClaudeCliName, 0, processError, null) is { } authFailure)
            {
                processError = authFailure;
                ReportAuthFailure(authFailure);
            }
            await AuditAsync(request, AgentAuditEventType.Error, processError);
            yield return new AgentChunk(AgentChunkType.Error, processError);
            yield return new AgentChunk(AgentChunkType.Done, "");
            yield break;
        }

        if (timedOut)
        {
            logger.LogWarning(
                "[Agent] Wall-clock limit of {Seconds}s reached after {Turns} turn(s), {ToolCalls} tool call(s) — stopping run.",
                maxWallClockSeconds, turn, toolCalls);
            await AuditAsync(request, AgentAuditEventType.LimitReached,
                $"Wall-clock limit {maxWallClockSeconds}s reached — run stopped");
            yield return new AgentChunk(AgentChunkType.Error,
                $"Agent run exceeded the {maxWallClockSeconds}s time limit and was stopped.");
            yield return new AgentChunk(AgentChunkType.Done, "");
            yield break;
        }

        sw.Stop();
        logger.LogInformation(
            "[Agent] Finished — Turns: {Turns}, ToolCalls: {ToolCalls}, Duration: {Duration}s",
            turn, toolCalls, sw.Elapsed.TotalSeconds.ToString("F1"));
        await AuditAsync(request, AgentAuditEventType.RunFinished,
            $"Run finished — {turn} turn(s), {toolCalls} tool call(s), {sw.Elapsed.TotalSeconds:F1}s");

        // A clean result clears any auth failure the runner still holds against claude
        // (the user re-logged in, or a setup-token was added) without waiting for a probe.
        if (lastMessage is ResultMessage { IsError: false } finished
            && !(finished.Subtype is { } fs && fs.StartsWith("error", StringComparison.Ordinal)))
            _authFeedback.ReportSuccess(AiProvider.Anthropic);

        // If we never got a ResultMessage, emit a Done chunk
        if (lastMessage is not ResultMessage)
        {
            yield return new AgentChunk(AgentChunkType.Done, "");
        }
    }

    /// <summary>CLI key for the auth classifier's per-CLI table and login command.</summary>
    private const string ClaudeCliName = "claude";

    private void ReportAuthFailure(string authFailure)
    {
        logger.LogWarning("[Agent] Run failed to authenticate — {Detail}", authFailure);
        _authFeedback.ReportAuthFailure(AiProvider.Anthropic, authFailure);
    }

    // Renders a cap for logs: ≤ 0 is "disabled", not "zero allowed".
    /// <summary>Name the Playwright MCP server is registered under for QA runs.</summary>
    private const string PlaywrightServerName = "playwright";

    /// <summary>Pinned, never <c>@latest</c>: the QA lane fetches it through the developer's own
    /// npm (and so their registry / company mirror), and a pinned version is one a security
    /// review can allowlist. Bump deliberately.</summary>
    internal const string PlaywrightMcpPackage = "@playwright/mcp@0.0.85";

    private static string Cap(int value) => value > 0 ? value.ToString() : "unlimited";

    // Records an audit event when the request carries tenant context. Contextless invocations
    // (e.g. tooling/tests) skip auditing so the log never accrues orphan rows.
    private Task AuditAsync(AgentRequest request, AgentAuditEventType type, string summary, string? tool = null)
    {
        if (request.OrgId == default)
            return Task.CompletedTask;

        return auditLog.RecordAsync(new AgentAuditRecord(
            request.OrgId, request.ProjectId, type, summary,
            request.TaskId, request.AgentId, request.ExecutionId,
            Actor: request.AgentName, Tool: tool));
    }

    /// <param name="costGuard">The run's spend guard when the request carries a ceiling, so the
    /// result can report whether the run ended on the checkpoint rather than on its own.</param>
    private ClaudeAgentOptions BuildOptions(
        AgentRequest request, out RunCostGuard? costGuard, Action<string> onStderr)
    {
        costGuard = null;
        var builder = new ClaudeAgentOptionsBuilder()
            .WithCwd(request.WorktreePath)
            .WithModel(_defaultModelOverride ?? request.Model ?? DefaultModel)
            .WithSystemPrompt(request.SystemPrompt)
            .WithStderr(onStderr);

        // Sibling repositories for reference (a folder-of-repos project's base folder). Reads
        // only: the write boundary hook below still refuses anything outside the worktree, and
        // the read guard still refuses dotenv files outside it.
        foreach (var dir in request.ReadOnlyDirs ?? [])
            builder.AddDir(dir);

        // Spawn exactly what the readiness probe found. Left unset, the SDK runs its own
        // discovery, which misses the native installer's ~/.local/bin — so a runner could
        // report claude connected and still fail every task with "Claude Code not found".
        // Null means we could not resolve it: leave the SDK to its default rather than
        // inventing a path.
        if (Orkka.SharedKernel.Ai.ProviderCliResolver.Resolve("claude") is { } claudeCli)
            builder.WithCliPath(claudeCli);

        // MaxTurns ≤ 0 means "no turn cap" — leave ClaudeAgentOptions.MaxTurns null so the
        // CLI never receives --max-turns. Pipeline phases run this way (see
        // PhaseBudgetsOptions): a turn cap fires on deep-but-legitimate work and burns the
        // whole run's spend for nothing; wall-clock is the hard stop instead.
        if (request.MaxTurns > 0)
            builder.WithMaxTurns(request.MaxTurns);

        // Resume the conversation's existing CLI session (agent chat): the CLI reloads its
        // own transcript from ~/.claude/projects instead of cold-booting, so the caller can
        // skip re-injecting conversation history into the system prompt. If the session file
        // is gone (different machine, cleaned up), the run fails and the caller retries fresh.
        if (!string.IsNullOrWhiteSpace(request.ResumeSessionId))
            builder.WithResume(request.ResumeSessionId);

        // SECURITY (Layer 2): the SDK child process inherits the Worker's environment, which
        // may hold secrets. Blank every sensitive variable; the claude CLI then authenticates
        // with the developer's own login in ~/.claude. No provider key is ever injected.
        foreach (var (name, value) in AgentEnvironment.BuildSanitizedOverridesFromProcess())
            builder.AddEnv(name, value);

        // Subscription auth (local runner): prefer a long-lived `claude setup-token` when
        // one is configured — concurrent children sharing ~/.claude/.credentials.json race
        // its single-use refresh token and log the user out; a setup-token never refreshes.
        // Applied AFTER the sanitizer (whose TOKEN marker blanks this very name). The token
        // is the developer's own, set on their machine. The sanitizer's blank
        // ANTHROPIC_API_KEY override stays in place.
        // On the runner the passthrough below carries the setup token (it is on Claude's
        // allowlist, read from the same runner.env), so this is the no-source fallback only.
        if (ProviderAuthEnvironment.Source is null && ClaudeSetupToken.Resolve() is { } setupToken)
            builder.AddEnv(ClaudeSetupToken.EnvVar, setupToken);

        // Environment-based sign-in (API key, company gateway, Bedrock, Vertex, Foundry): only
        // Claude's allowlisted variables that are actually set for this run's workspace (or the
        // machine), re-applied after the sanitizer blanked them. When any of them authenticates,
        // Claude Code's own subprocess scrub strips credentials from every Bash command, hook
        // and MCP server it starts — the CLI keeps the key for its API calls, the agent's shell
        // never sees it (code.claude.com/docs/en/env-vars, CLAUDE_CODE_SUBPROCESS_ENV_SCRUB).
        var auth = ProviderAuthEnvironment.Resolve(AiProvider.Anthropic, request.Workspace);
        foreach (var (name, value) in auth.Variables)
            builder.AddEnv(name, value);
        if (auth.CarriesSecret)
            builder.AddEnv(SubprocessEnvScrubVar, "1");

        // Per-run extras — the task's own development database. After the sanitizer, which
        // blanks DATABASE_URL among the platform's names, so the run's own value wins.
        if (request.Environment is { Count: > 0 })
        {
            foreach (var (name, value) in request.Environment)
                builder.AddEnv(name, value);
        }

        if (request.Phase == AgentPhase.Utility)
        {
            // Pure text generation: hard-remove the built-in tools from the model's schema
            // (disallowed, not merely un-allowed) so one-shot utility runs can't spend
            // turns or wall-clock wandering a scratch directory — un-allowed tools would
            // still be attempted and denied, burning a turn per refusal.
            builder.AddDisallowedTools(DisallowedToolsFor(AgentPhase.Utility));

            // Bound runaway generations: an uncapped utility run can outlive the host's
            // invoke timeout and bill for a response nobody receives. The SDK has no
            // output-token option, but the CLI honors this env var per request. The CLI
            // FAILS the run outright when a response hits the cap (exit 1, "response
            // exceeded the N output token maximum" — verified, it does not truncate), so
            // the value must be generous runaway insurance, never the caller's tight
            // budget: a title's MaxTokens of 60 applied literally kills every title.
            // Utility-only: dev/chat runs pass MaxTokens without meaning it as a cap.
            //
            // The floor is far above any caller's MaxTokens because the CLI counts the
            // model's thinking against it: a brief synthesis the API served in ~3k output
            // tokens died on the runner at the old 8192 floor, twice (the runner retries
            // once), and the two dead attempts outran the host's 15-minute op ceiling —
            // a 16-minute "stuck" provisioning step for what should have been one minute.
            if (request.MaxTokens > 0)
                builder.AddEnv("CLAUDE_CODE_MAX_OUTPUT_TOKENS",
                    Math.Max(request.MaxTokens, UtilityOutputTokenFloor).ToString());
        }
        else
        {
            var tools = AllowedToolsFor(request.Phase, request.ApiTesting);

            // Server-wide form (mcp__<name>), like mcp__playwright below — the server's own
            // ListTools defines the set, and this avoids chasing tool renames here.
            builder.AddAllowedTools(request.ToolServer is { } toolServer
                ? [.. tools, $"mcp__{toolServer.Name}"]
                : tools);

            // See ChatDisallowedTools: a chat turn's sub-agents interrupt it, and an
            // interrupted turn loses whatever platform tool call was in flight.
            if (DisallowedToolsFor(request.Phase) is { Length: > 0 } refused)
                builder.AddDisallowedTools(refused);
        }

        // In-process project-context tools (documents, repos, board, history) — the pipeline
        // twin of AgentChatService's SdkChatToolServerAdapter registration.
        if (request.ToolServer is { } sdkServer)
        {
            builder.AddMcpServer(sdkServer.Name, new McpSdkServerConfig
            {
                Name = sdkServer.Name,
                Instance = sdkServer,
            });
        }

        // API-mode QA drives HTTP with curl and the repo's own test runner — starting a browser
        // it will never call is pure cost (an npx process plus its tool listing every run).
        if (request.Phase == AgentPhase.Testing && !request.ApiTesting)
        {
            // Playwright MCP gives the QA agent a real (headless) browser. --isolated keeps
            // profile state out of the worktree. Raw CreateProcess cannot spawn npx (a .cmd
            // shim) on Windows, so route through cmd /c there.
            var (command, args) = OperatingSystem.IsWindows()
                ? ("cmd", new List<string> { "/c", "npx", PlaywrightMcpPackage, "--headless", "--isolated" })
                : ("npx", new List<string> { PlaywrightMcpPackage, "--headless", "--isolated" });

            builder.AddMcpServer(PlaywrightServerName, new McpStdioServerConfig
            {
                Command = command,
                Args = args
            });
        }

        // P2/M0: opt-in OS sandbox (fs restriction + egress-via-proxy). Defense-in-depth inside the
        // host/container — NOT a tenant boundary on its own. Default off; enable per environment.
        if (_sandbox.Enabled)
        {
            builder.WithSandbox(new SandboxSettings
            {
                Enabled = true,
                Network = new SandboxNetworkConfig
                {
                    HttpProxyPort = _sandbox.HttpProxyPort,
                    SocksProxyPort = _sandbox.SocksProxyPort,
                    AllowLocalBinding = _sandbox.AllowLocalBinding,
                },
            });
        }

        // Headless runs have nobody at the CLI's own permission prompts — anything a mode
        // doesn't pre-approve is auto-denied ("you haven't granted it yet"), which refused
        // ordinary git/go/npm commands. Developing and Testing run commands, so they lift the
        // CLI's own gate; what may run is decided by Orkka's PreToolUse guards below, which the
        // CLI consults in every permission mode and whose deny always wins.
        if (LiftsPermissionGate(request.Phase))
            builder.WithPermissionMode(PermissionMode.BypassPermissions);

        // Every phase that can Read: a PDF read makes the CLI stream a `document` content
        // block, which the SDK's parser cannot decode — it throws mid-stream and the whole
        // run dies. Task attachments are frequently PDFs, so this fires in PM review and
        // planning too, not just the phases that get Bash.
        builder.AddHook(HookEvent.PreToolUse, new AgentPdfReadGuard(logger).EvaluateRead, "Read", null);

        // Every phase, every target: file tools stay inside the worktree. The agent's output is
        // its branch; a Write that lands elsewhere is invisible to the diff, the review and the
        // PR — and on a user's machine it is a stray file on their disk. Seen in the wild when a
        // Dockerfile's WORKDIR leaked into the agent's idea of where the repo was.
        var boundary = new AgentWorktreeBoundaryGuard(request.WorktreePath, logger,
            onBlocked: (tool, path) => AuditAsync(
                request, AgentAuditEventType.CommandBlocked, $"{tool} outside worktree: {path}", tool: tool));
        builder.AddHook(HookEvent.PreToolUse, boundary.Evaluate, AgentWorktreeBoundaryGuard.ToolMatcher, null);

        // Every phase, every target: reads never reach the developer's credentials, browser
        // data, or another task's / workspace's files under the Orkka home.
        var sensitive = new AgentSensitivePathGuard(request.WorktreePath, logger,
            onBlocked: (tool, path) => AuditAsync(
                request, AgentAuditEventType.CommandBlocked, $"{tool} of a protected path: {path}", tool: tool));
        builder.AddHook(HookEvent.PreToolUse, sensitive.Evaluate, AgentSensitivePathGuard.ToolMatcher, null);

        // Per-run spend ceiling (soft stop — see RunCostGuard). Registered on ALL tools
        // (null matcher): denial composes safely with the matcher-scoped guards below, since
        // the CLI treats any deny as a deny. Utility runs are one-shot and can't usefully
        // wrap up, so they stay outside it.
        if (request.MaxCostUsd > 0 && request.Phase != AgentPhase.Utility)
        {
            costGuard = new RunCostGuard(
                _defaultModelOverride ?? request.Model ?? DefaultModel, request.MaxCostUsd, logger);
            builder.AddHook(HookEvent.PreToolUse, costGuard.EvaluateToolUse, null, null);
        }

        // Only the Developing and Testing phases grant Bash at all.
        var grantsBash = request.Phase is AgentPhase.Developing or AgentPhase.Testing;

        // Exactly one hook, ever: two on the same event would make the outcome depend on how the
        // CLI composes their answers, and getting that wrong runs the command.
        HookCallback? evaluate = null;

        // Interactive sessions get the approval-capable guard: the same command guard, plus asking
        // the human before a command that destroys work the session has not landed.
        if (grantsBash && request.Interactive && request.TaskId is { } interactiveTaskId)
        {
            var interactive = new InteractiveBashGuard(
                interactiveTaskId, request.ExecutionId, request.AgentName, request.AgentRole,
                request.WorktreePath, approvalGate, logger,
                onBlocked: command => AuditAsync(
                    request, AgentAuditEventType.CommandBlocked, command, tool: "Bash"));
            evaluate = interactive.EvaluateCommand;
        }
        else
        {
            // Every phase, whether or not it grants a shell — a phase that should have none
            // still gets screened if the CLI ever offers one. Screens every shell command before it runs: no credential reads, pushes, other
            // hosts, cloud/forge CLIs or registry publishes (see AgentBashGuard).
            var guard = new AgentBashGuard(request.AgentName, request.AgentRole, request.WorktreePath, logger,
                onBlocked: command => AuditAsync(
                    request, AgentAuditEventType.CommandBlocked, command, tool: "Bash"));
            evaluate = guard.EvaluateCommand;
        }

        // PowerShell too: on Windows the CLI can offer a PowerShell tool alongside Bash, and a
        // shell the guard does not see would be a shell the guard does not cover.
        if (evaluate is not null)
            builder.AddHook(HookEvent.PreToolUse, evaluate, ShellToolMatcher, null);

        return builder.Build();
    }

    private IEnumerable<AgentChunk> ProcessAssistantMessage(AssistantMessage assistant)
    {
        foreach (var block in assistant.Content)
        {
            switch (block)
            {
                case TextBlock text:
                    logger.LogDebug("[Agent]   Text: {Preview}",
                        text.Text.Length > 120 ? text.Text[..120] + "..." : text.Text);
                    yield return new AgentChunk(AgentChunkType.Text, text.Text);
                    break;

                case ThinkingBlock thinking:
                    logger.LogDebug("[Agent]   Thinking: {Length} chars", thinking.Thinking.Length);
                    yield return new AgentChunk(AgentChunkType.Thinking, thinking.Thinking);
                    break;

                case ToolUseBlock toolUse:
                    var inputSummary = toolUse.Input.ValueKind == JsonValueKind.Undefined
                        ? ""
                        : toolUse.Input.ToString();
                    logger.LogInformation("[Agent]   Tool: {ToolName} — {Input}",
                        toolUse.Name, inputSummary.Length > 200 ? inputSummary[..200] + "..." : inputSummary);
                    yield return new AgentChunk(AgentChunkType.ToolUse,
                        $"{toolUse.Name}: {inputSummary}");
                    break;

                case ToolResultBlock toolResult:
                    var content = toolResult.Content.HasValue
                        ? toolResult.Content.Value.ToString()
                        : "";
                    logger.LogDebug("[Agent]   ToolResult: {Length} chars", content.Length);
                    yield return new AgentChunk(AgentChunkType.ToolResult, content);
                    break;
            }
        }
    }

    /// <summary>
    /// Surfaces MCP servers the CLI could not bring up, read from its <c>system/init</c> event.
    /// The in-process tool server (<see cref="AgentRequest.ToolServer"/>) is the one that
    /// matters: when it fails the agent has no project tools for the entire run, ToolSearch
    /// finds nothing, and nothing else on the host says why — the CLI writes the connect
    /// error only to its own mcp-logs file. Logged, never fatal: the run still produces work.
    /// </summary>
    private void LogMcpServerStatus(AgentRequest request, SystemMessage init)
    {
        var expected = new List<string>();
        if (request.ToolServer is { } toolServer)
            expected.Add(toolServer.Name);
        if (request.Phase == AgentPhase.Testing && !request.ApiTesting)
            expected.Add(PlaywrightServerName);

        foreach (var server in ClaudeMcpInitStatus.Unavailable(init.Data, expected))
        {
            if (request.ToolServer is { } ts && server.Name == ts.Name)
            {
                logger.LogError(
                    "[Agent] In-process MCP tool server '{Server}' did not connect (status: {Status}) — the agent has no mcp__{Server}__* tools for this run. Phase: {Phase}, Task: {TaskId}. The connect error is in the CLI's mcp-logs; SDK-side handler failures are logged by ih0.Claude.Agent.SDK.Internal.QueryHandler.",
                    server.Name, server.Status, server.Name, request.Phase, request.TaskId);
            }
            else
            {
                logger.LogWarning(
                    "[Agent] MCP server '{Server}' did not connect (status: {Status}) — Phase: {Phase}, Task: {TaskId}",
                    server.Name, server.Status, request.Phase, request.TaskId);
            }
        }
    }

    private IEnumerable<AgentChunk> ProcessResultMessage(
        ResultMessage result, AgentRequest request, RunCostGuard? costGuard)
    {
        var resultText = result.Result ?? "";

        // Limit subtypes (error_max_turns, error_during_execution) carry is_error with an
        // empty result — map them to a legible message instead of a blank error.
        if (result.IsError || result.Subtype is { } st && st.StartsWith("error", StringComparison.Ordinal))
        {
            var errorText = ClaudeCliErrors.DescribeErrorResult(result.Subtype, resultText);
            // The CLI reports an expired/invalid login as an error RESULT ("API Error: 401
            // … Invalid authentication credentials"), not a process failure.
            if (CliAuthFailure.Classify(ClaudeCliName, 0, null, errorText) is { } authFailure)
            {
                errorText = authFailure;
                ReportAuthFailure(authFailure);
            }
            logger.LogError("[Agent] Error result — Subtype: {Subtype}. {Error}",
                result.Subtype, errorText);
            yield return new AgentChunk(AgentChunkType.Error, errorText);
        }

        var promptTokens = 0;
        var completionTokens = 0;
        var cacheReadTokens = 0;
        var cacheWriteTokens = 0;
        if (result.Usage.HasValue)
        {
            var usage = result.Usage.Value;
            if (usage.TryGetProperty("input_tokens", out var input))
                promptTokens = input.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var output))
                completionTokens = output.GetInt32();
            if (usage.TryGetProperty("cache_read_input_tokens", out var cacheRead))
                cacheReadTokens = cacheRead.GetInt32();
            if (usage.TryGetProperty("cache_creation_input_tokens", out var cacheWrite))
                cacheWriteTokens = cacheWrite.GetInt32();
        }

        // RESOLVED 2026-08-16 against production usage rows: `result.usage` is PER-INVOCATION.
        // Every resumed run shows input in the tens-to-hundreds while cache reads run to
        // millions — the cumulative shape would have shown input growing run over run. Resumed
        // re-entries are therefore billed only for their own turn plus the session reload, which
        // is real vendor cost. The comparison below is kept as a live check rather than a
        // one-off: OurCost is now persisted next to CliCost on the usage row
        // (UsageRecord.VendorReportedCostUsd), so this no longer depends on log retention.
        //
        // Original note, retained because it documents how to re-run the check — grep two
        // consecutive "[Agent] Result"
        // lines with Resumed:true on the same task and compare:
        //   per-invocation (correct billing): OurCost ≈ CliCost, and the resumed run's usage
        //   is mostly CacheRead (the session reload) — real vendor cost, correctly billed;
        //   cumulative (over-billing): the second line's Input/Output ≈ first line's totals
        //   plus the new turn, and CliCost keeps growing run over run.
        var ourCost = Orkka.SharedKernel.Ai.CostCalculator.Calculate(
            _defaultModelOverride ?? request.Model ?? DefaultModel,
            promptTokens, completionTokens, cacheReadTokens, cacheWriteTokens);
        logger.LogInformation(
            "[Agent] Result — Input: {InputTokens}, Output: {OutputTokens}, CacheRead: {CacheRead}, "
            + "CacheWrite: {CacheWrite}, Turns: {Turns}, Resumed: {Resumed}, "
            + "OurCost: ${OurCost:F4}, CliCost: ${CliCost:F4}",
            promptTokens, completionTokens, cacheReadTokens, cacheWriteTokens,
            result.NumTurns, !string.IsNullOrWhiteSpace(request.ResumeSessionId),
            ourCost, result.TotalCostUsd ?? 0);

        var spendLimitReached = costGuard?.Exhausted == true;
        if (spendLimitReached)
            logger.LogWarning(
                "[Agent] Run ended on its spend checkpoint (${Cost:F2} of ${Ceiling:F2}) — reporting a pause, not a finish",
                costGuard!.LastCostUsd, request.MaxCostUsd);

        yield return new AgentChunk(AgentChunkType.Done, resultText, promptTokens, completionTokens,
            cacheReadTokens, cacheWriteTokens, result.SessionId, (decimal?)result.TotalCostUsd,
            SpendLimitReached: spendLimitReached);
    }
}
