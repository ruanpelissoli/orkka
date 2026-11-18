namespace Orkka.Modules.Execution.Agents;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

/// <summary>
/// Shared engine for vendor-CLI agent invokers (codex, gemini) running on the local
/// runner with the user's subscription credentials. Spawns the CLI in the worktree with a
/// sanitized environment, feeds the prompt over stdin (Windows caps the whole command line
/// at ~32K chars), parses stdout as JSONL events via the subclass's mapper, and enforces
/// the wall-clock and tool-event caps from <see cref="AgentLimitsOptions"/>. These CLIs
/// have no MaxTurns equivalent — the caps here are the only runaway bound.
/// </summary>
public abstract class CliAgentInvokerBase(
    AgentLimitsOptions limits,
    IAgentAuditLog auditLog,
    ILogger logger,
    // Optional so hosts with no readiness state to update (the hosted Worker) and the
    // existing subclass/test constructors need no change.
    IProviderAuthFeedback? authFeedback = null) : IAiAgentInvoker
{
    private readonly IProviderAuthFeedback _authFeedback =
        authFeedback ?? NoOpProviderAuthFeedback.Instance;

    /// <summary>The provider this invoker serves; other requests are rejected.</summary>
    protected abstract AiProvider Provider { get; }

    // Per-phase budgets from the orchestrator override the process-wide default; ad-hoc
    // callers (chat, utility) leave them null and get the global AgentLimitsOptions value.
    private int EffectiveMaxToolCalls(AgentRequest request) =>
        request.MaxToolCalls ?? limits.MaxToolCalls;
    private int EffectiveMaxWallClockSeconds(AgentRequest request) =>
        request.MaxWallClockSeconds ?? limits.MaxWallClockSeconds;

    // Renders a cap for logs: ≤ 0 is "disabled", not "zero allowed".
    private static string Cap(int value) => value > 0 ? value.ToString() : "unlimited";

    /// <summary>CLI key for logs/errors: "codex" | "gemini".</summary>
    protected abstract string CliName { get; }

    protected abstract string DefaultModel { get; }

    /// <summary>How to get this CLI onto the machine, phrased as an imperative fragment for
    /// the pre-flight failure message ("install it with `npm install -g …`, then sign in
    /// with `… login`"). Override with the vendor's real install command.</summary>
    protected virtual string InstallHint =>
        $"install the {CliName} CLI on that machine and sign in";

    /// <summary>Test seam for the pre-flight CLI check. The default is the same resolver the
    /// readiness probe and <see cref="ShellCommand"/> use — the whole point is that probe and
    /// spawn cannot answer differently.</summary>
    protected internal static Func<string, string?> ResolveCli { get; set; } =
        Orkka.SharedKernel.Ai.ProviderCliResolver.Resolve;

    /// <summary>Mutable per-run accumulator the line mapper fills as events stream in.
    /// protected internal so parser tests (InternalsVisibleTo) can drive MapLine directly.</summary>
    protected internal sealed class CliRunState
    {
        public string FinalText = "";
        public int PromptTokens;
        public int CompletionTokens;
        public int CacheReadTokens;
        /// <summary>Message from an in-stream error event (kept for terminal reporting).</summary>
        public string? StreamError;
        /// <summary>CLI session/thread id of this run (codex thread.started, copilot session
        /// events). Reported on the Done chunk so the next turn can resume instead of
        /// re-sending the full system prompt + history.</summary>
        public string? SessionId;

        /// <summary>Id of the text part currently accumulating into <see cref="FinalText"/>.
        /// Some CLIs (opencode) re-emit one GROWING part as it streams rather than sending
        /// disjoint fragments, so a plain append would repeat every prefix. Null for
        /// append-only streams, which never touch this.</summary>
        public string? OpenTextPartId;

        /// <summary>Length of <see cref="FinalText"/> before <see cref="OpenTextPartId"/>
        /// began, so a re-emission of that part replaces its own tail instead of stacking.</summary>
        public int OpenTextPartStart;

        /// <summary>Folds one text part into <see cref="FinalText"/>: a new part id appends,
        /// a repeat of the open part overwrites what that part contributed. Parts without an
        /// id are treated as disjoint fragments and always append.</summary>
        public void AppendTextPart(string? partId, string text)
        {
            if (partId is not null && partId == OpenTextPartId)
            {
                FinalText = FinalText[..OpenTextPartStart] + text;
                return;
            }

            OpenTextPartStart = FinalText.Length;
            OpenTextPartId = partId;
            FinalText += text;
        }
    }

    /// <summary>Per-attempt terminal state the engine turns into Error/Done chunks.
    /// protected internal, like <see cref="CliRunState"/>, so tests can script an attempt
    /// and exercise the post-run classification without spawning a CLI.</summary>
    protected internal sealed class CliRunOutcome
    {
        public bool TimedOut;
        public bool ToolCapReached;
        public bool ProcessFailed;
        public string? FailureMessage;
        public int ExitCode;
        public bool AnyEventParsed;
        public int ToolCalls;
        /// <summary>The captured stderr tail of the final attempt, kept apart from
        /// <see cref="FailureMessage"/> so the auth classifier reads the CLI's own words
        /// rather than the engine's framing around them.</summary>
        public string? Stderr;
    }

    /// <summary>Builds the process (command, args, cwd, extra env) for one attempt.</summary>
    protected abstract ProcessStartInfo BuildStartInfo(AgentRequest request, string model);

    /// <summary>The prompt text written to the CLI's stdin.</summary>
    protected abstract string BuildStdin(AgentRequest request);

    /// <summary>Maps one stdout JSONL line to zero or more chunks (never Done/Error —
    /// terminal chunks are the engine's job). Unknown lines must be skipped, not thrown.</summary>
    protected internal abstract IEnumerable<AgentChunk> MapLine(string line, CliRunState state);

    /// <summary>Runs the CLI once (subclasses may layer retries/fallbacks around this).</summary>
    protected virtual IAsyncEnumerable<AgentChunk> RunAttemptsAsync(
        AgentRequest request, string model, CliRunState state, CliRunOutcome outcome, CancellationToken ct)
        => RunCliAsync(BuildStartInfo(request, model), BuildStdin(request), request, state, outcome, ct);

    public async IAsyncEnumerable<AgentChunk> InvokeAsync(
        AgentRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (request.Provider != Provider)
        {
            yield return new AgentChunk(AgentChunkType.Error,
                $"Only the {Provider} provider is supported by the {CliName} CLI invoker. Got: {request.Provider}");
            yield return new AgentChunk(AgentChunkType.Done, "");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(request.WorktreePath))
        {
            yield return new AgentChunk(AgentChunkType.Error,
                "WorktreePath is required — the agent must run inside a repository worktree.");
            yield return new AgentChunk(AgentChunkType.Done, "");
            yield break;
        }

        var model = request.Model ?? DefaultModel;

        // Pre-flight, same resolver the readiness probe uses: fail the run with the remedy
        // instead of spawning a bare name and surfacing cmd.exe's "'copilot' is not
        // recognized". Reachable despite probe gating — the CLI can vanish between the
        // runner's last Hello and the run, and older probes counted shims the spawn
        // could not launch.
        if (ResolveCli(CliName) is null)
        {
            var unavailable = CliUnavailableMessage(CliName, InstallHint, request, model);
            logger.LogError(
                "[Agent:{Cli}] CLI not found — failing run pre-flight. Phase: {Phase}, Model: {Model}, Task: {TaskId}",
                CliName, request.Phase, model, request.TaskId);
            await AuditAsync(request, AgentAuditEventType.Error, unavailable);
            yield return new AgentChunk(AgentChunkType.Error, unavailable);
            yield return new AgentChunk(AgentChunkType.Done, "");
            yield break;
        }

        var maxToolCalls = EffectiveMaxToolCalls(request);
        var maxWallClockSeconds = EffectiveMaxWallClockSeconds(request);
        logger.LogInformation(
            "[Agent:{Cli}] Starting — Phase: {Phase}, Model: {Model}, MaxToolCalls: {MaxToolCalls}, MaxWallClock: {MaxWallClock}s, Path: {WorktreePath}",
            CliName, request.Phase, model, Cap(maxToolCalls), Cap(maxWallClockSeconds),
            request.WorktreePath);

        await AuditAsync(request, AgentAuditEventType.RunStarted,
            $"Run started — phase {request.Phase}, model {model} ({CliName} CLI)");

        var state = new CliRunState();
        var outcome = new CliRunOutcome();
        var sw = Stopwatch.StartNew();

        await foreach (var chunk in RunAttemptsAsync(request, model, state, outcome, ct))
        {
            if (chunk.Type == AgentChunkType.ToolUse)
            {
                var colon = chunk.Content.IndexOf(':');
                var toolName = colon > 0 ? chunk.Content[..colon].Trim() : chunk.Content;
                await AuditAsync(request, AgentAuditEventType.ToolUse, chunk.Content, toolName);
            }
            yield return chunk;
        }

        sw.Stop();

        if (outcome.TimedOut)
        {
            logger.LogWarning(
                "[Agent:{Cli}] Wall-clock limit of {Seconds}s reached after {ToolCalls} tool call(s) — run stopped.",
                CliName, maxWallClockSeconds, outcome.ToolCalls);
            await AuditAsync(request, AgentAuditEventType.LimitReached,
                $"Wall-clock limit {maxWallClockSeconds}s reached — run stopped");
            yield return new AgentChunk(AgentChunkType.Error,
                $"Agent run exceeded the {maxWallClockSeconds}s time limit and was stopped.");
            yield return new AgentChunk(AgentChunkType.Done, "");
            yield break;
        }

        if (outcome.ToolCapReached)
        {
            logger.LogWarning(
                "[Agent:{Cli}] Tool-call limit of {Limit} reached — run stopped.",
                CliName, maxToolCalls);
            await AuditAsync(request, AgentAuditEventType.LimitReached,
                $"Tool-call limit {maxToolCalls} reached — run stopped");
            yield return new AgentChunk(AgentChunkType.Error,
                $"Agent run reached the {maxToolCalls} tool-call limit and was stopped.");
            yield return new AgentChunk(AgentChunkType.Done, "");
            yield break;
        }

        if (outcome.ProcessFailed)
        {
            var message = outcome.FailureMessage
                          ?? $"{CliName} CLI exited with code {outcome.ExitCode}.";
            // An auth-shaped exit gets the plain-words message with the login remedy in
            // place of the exit code + stderr dump, and tells the runner so it stops
            // offering this provider until the user signs back in.
            if (CliAuthFailure.Classify(CliName, outcome.ExitCode, outcome.Stderr, state.StreamError) is { } authFailure)
            {
                message = authFailure;
                ReportAuthFailure(authFailure);
            }
            await AuditAsync(request, AgentAuditEventType.Error, message);
            yield return new AgentChunk(AgentChunkType.Error, message);
            yield return new AgentChunk(AgentChunkType.Done, "");
            yield break;
        }

        if (state.StreamError is not null)
        {
            var streamError = state.StreamError;
            if (CliAuthFailure.Classify(CliName, outcome.ExitCode, outcome.Stderr, streamError) is { } authFailure)
            {
                streamError = authFailure;
                ReportAuthFailure(authFailure);
            }
            await AuditAsync(request, AgentAuditEventType.Error, streamError);
            yield return new AgentChunk(AgentChunkType.Error, streamError);
        }
        else
        {
            // A clean completion is the strongest readiness evidence there is — it clears
            // any earlier auth failure the runner is still holding against this provider,
            // and for CLIs whose credentials live in the OS store (copilot, grok) it is
            // the only positive proof of sign-in the runner ever gets.
            _authFeedback.ReportSuccess(Provider);
        }

        logger.LogInformation(
            "[Agent:{Cli}] Finished — ToolCalls: {ToolCalls}, Duration: {Duration}s, Input: {InputTokens}, Output: {OutputTokens}, CacheRead: {CacheRead}",
            CliName, outcome.ToolCalls, sw.Elapsed.TotalSeconds.ToString("F1"),
            state.PromptTokens, state.CompletionTokens, state.CacheReadTokens);
        await AuditAsync(request, AgentAuditEventType.RunFinished,
            $"Run finished — {outcome.ToolCalls} tool call(s), {sw.Elapsed.TotalSeconds:F1}s");

        yield return new AgentChunk(AgentChunkType.Done, state.FinalText,
            state.PromptTokens, state.CompletionTokens, state.CacheReadTokens,
            SessionId: state.SessionId);
    }

    /// <summary>
    /// One CLI process run: spawn, write stdin, stream mapped chunks from stdout JSONL,
    /// enforce caps. Emits only stream chunks — terminal Error/Done handling stays with
    /// <see cref="InvokeAsync"/> so subclasses can retry a failed attempt.
    /// </summary>
    protected async IAsyncEnumerable<AgentChunk> RunCliAsync(
        ProcessStartInfo psi, string stdin, AgentRequest request,
        CliRunState state, CliRunOutcome outcome,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Hard per-run wall-clock cap, distinct from the caller's ct so a timeout is
        // reported cleanly rather than as a raw cancellation.
        var maxWallClockSeconds = EffectiveMaxWallClockSeconds(request);
        var maxToolCalls = EffectiveMaxToolCalls(request);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (maxWallClockSeconds > 0)
            runCts.CancelAfter(TimeSpan.FromSeconds(maxWallClockSeconds));

        var stderrTail = new BoundedLineBuffer(maxChars: 4_000);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            AgentProcesses.Track(process);
        }
        catch (Exception ex)
        {
            outcome.ProcessFailed = true;
            outcome.FailureMessage =
                $"Could not start the {CliName} CLI: {ex.Message}. Is it installed and on PATH?";
            yield break;
        }

        // The caller's token (a host cancel, a runner CancelRun/CancelTaskRuns, the connection
        // dropping) cancels this iterator rather than the wall-clock branches below — and the
        // Process's Dispose does NOT kill the child. Without this the CLI kept running after
        // its stream was abandoned: an agent editing a worktree nobody is watching, or one
        // about to be deleted. Idempotent with the KillTree calls inside (HasExited guard).
        try
        {
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                stderrTail.Append(e.Data);
                logger.LogDebug("[Agent:{Cli} stderr] {Line}", CliName, e.Data);
            };
            process.BeginErrorReadLine();

            try
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), runCts.Token);
                process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The CLI may have died before reading stdin (bad args, missing auth) — fall
                // through to the exit handling below, which carries stderr.
                logger.LogDebug("[Agent:{Cli}] stdin write failed: {Message}", CliName, ex.Message);
            }

            while (true)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(runCts.Token);
                }
                catch (OperationCanceledException) when (runCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    outcome.TimedOut = true;
                    KillTree(process);
                    yield break;
                }

                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                foreach (var chunk in MapLineSafe(line, state))
                {
                    outcome.AnyEventParsed = true;
                    if (chunk.Type == AgentChunkType.ToolUse)
                    {
                        outcome.ToolCalls++;
                        if (maxToolCalls > 0 && outcome.ToolCalls > maxToolCalls)
                        {
                            outcome.ToolCapReached = true;
                            KillTree(process);
                            yield break;
                        }
                    }
                    yield return chunk;
                }
            }

            try
            {
                await process.WaitForExitAsync(runCts.Token);
            }
            catch (OperationCanceledException) when (runCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                outcome.TimedOut = true;
                KillTree(process);
                yield break;
            }

        }
        finally
        {
            KillTree(process);
        }

        outcome.ExitCode = process.ExitCode;
        outcome.Stderr = stderrTail.ToString();
        if (process.ExitCode != 0)
        {
            var stderr = outcome.Stderr;
            outcome.ProcessFailed = true;
            outcome.FailureMessage = string.IsNullOrWhiteSpace(stderr)
                ? $"{CliName} CLI exited with code {process.ExitCode}."
                : $"{CliName} CLI exited with code {process.ExitCode}.\n--- {CliName} CLI stderr ---\n{stderr.Trim()}";
            logger.LogError(
                "[Agent:{Cli}] CLI process failed — ExitCode: {ExitCode}, Phase: {Phase}, Task: {TaskId}. Stderr: {Stderr}",
                CliName, process.ExitCode, request.Phase, request.TaskId, stderr);
        }
    }

    private void ReportAuthFailure(string authFailure)
    {
        logger.LogWarning("[Agent:{Cli}] Run failed to authenticate — {Detail}", CliName, authFailure);
        _authFeedback.ReportAuthFailure(Provider, authFailure);
    }

    private IEnumerable<AgentChunk> MapLineSafe(string line, CliRunState state)
    {
        try
        {
            // Materialize so a mid-iteration parse throw cannot split a line's chunks.
            return MapLine(line, state).ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug("[Agent:{Cli}] Skipping unparseable output line: {Message}", CliName, ex.Message);
            return [];
        }
    }

    private void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[Agent:{Cli}] Could not kill CLI process tree: {Message}", CliName, ex.Message);
        }
    }

    /// <summary>Base ProcessStartInfo: redirected pipes, worktree cwd, sanitized env
    /// (Layer 2 — blanks every sensitive variable). An invoker re-applies its provider's
    /// allowlisted sign-in variables afterwards (<see cref="ProviderAuthEnvironment"/>).</summary>
    protected static ProcessStartInfo CreateStartInfo(
        string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        foreach (var (name, value) in AgentEnvironment.BuildSanitizedOverridesFromProcess())
            psi.Environment[name] = value;
        return psi;
    }

    /// <summary>Why this run needed a CLI the machine doesn't have, with the remedy. Names
    /// the model so the user can see WHY the run routed to this provider — the model is
    /// configured on the agent, the task, or as the project default, and switching it is the
    /// no-install fix.</summary>
    internal static string CliUnavailableMessage(
        string cliName, string installHint, AgentRequest request, string model) =>
        $"The {cliName} CLI is not installed on the machine running this task (or is not " +
        $"launchable there). The run was routed to {cliName} because it uses the model " +
        $"'{model}' (configured on the agent '{request.AgentName}', on the task, or as the " +
        $"project default). To fix it, {installHint} — or switch the agent's model to a " +
        "provider that is connected.";

    /// <summary>Launch shape for a provider CLI, from the SAME resolver the readiness probe
    /// uses — the probe reporting a CLI found means exactly this spawn can start it. Spawns
    /// the resolved absolute path: a native binary directly, an npm .cmd shim through
    /// `cmd /c` (raw CreateProcess cannot drive shims — same reason the Claude SDK path
    /// routes npx through cmd), a .ps1 through `powershell -File`. Resolution failing here
    /// (the CLI vanished after <see cref="InvokeAsync"/>'s pre-flight passed) falls back to
    /// the bare name so the spawn error reports the OS's own message.</summary>
    protected static (string FileName, List<string> Args) ShellCommand(string cli, List<string> args)
    {
        if (Orkka.SharedKernel.Ai.ProviderCliResolver.ResolveLaunch(cli) is { } launch)
            return (launch.FileName, [.. launch.ArgumentPrefix, .. args]);

        return OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/c", cli, .. args])
            : (cli, args);
    }

    // Records an audit event when the request carries tenant context. Contextless
    // invocations (tooling/tests) skip auditing so the log never accrues orphan rows.
    private Task AuditAsync(AgentRequest request, AgentAuditEventType type, string summary, string? tool = null)
    {
        if (request.OrgId == default)
            return Task.CompletedTask;

        return auditLog.RecordAsync(new AgentAuditRecord(
            request.OrgId, request.ProjectId, type, summary,
            request.TaskId, request.AgentId, request.ExecutionId,
            Actor: request.AgentName, Tool: tool));
    }
}
