namespace Orkka.Modules.Execution.Agents;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Runs agents through the `opencode` CLI (sst/opencode — an open-source terminal coding
/// agent). OpenCode is the MIRROR IMAGE of <see cref="OllamaAgentInvoker"/>: Ollama serves
/// models but cannot act, so it borrows the codex CLI as a harness; OpenCode is a harness
/// that ships no models of its own and drives whichever of the ~75 models.dev providers the
/// user authenticated with `opencode auth login` (credentials live in
/// ~/.local/share/opencode/auth.json — Orkka never reads or holds them).
///
/// That is why catalog ids are DOUBLY namespaced, "opencode/&lt;provider&gt;/&lt;model&gt;":
/// the harness owns the id and the vendor rides inside it. ModelProviders.FromModelId
/// therefore tests the "opencode" prefix before every vendor prefix, or
/// "opencode/anthropic/…" would route to the claude CLI. The prefix is stripped here and
/// the CLI sees the `provider/model` pair it expects.
///
/// The static catalog carries three anchors only. The selectable set is whatever the runner
/// reports from `opencode models`, because which vendors are reachable is a property of the
/// user's logins, not of Orkka — see RunnerProviders.DiscoversModels.
///
/// Local edition only: there is no key Orkka could inject (the user's provider logins are
/// OpenCode's own), so hosted requests never dispatch here.
///
/// Like Grok, the prompt travels in a temp file: `opencode run` takes the message as
/// positional arguments and Windows caps the whole command line at ~32K chars, which an
/// Orkka system prompt plus task context clears easily.
/// </summary>
public sealed class OpenCodeAgentInvoker(
    IOptions<AgentLimitsOptions> agentLimits,
    IAgentAuditLog auditLog,
    ILogger<OpenCodeAgentInvoker> logger,
    IProviderAuthFeedback? authFeedback = null)
    : CliAgentInvokerBase(agentLimits.Value, auditLog, logger, authFeedback)
{
    protected override AiProvider Provider => AiProvider.OpenCode;
    protected override string CliName => "opencode";
    protected override string DefaultModel => "opencode/anthropic/claude-sonnet-4-6";

    protected override string InstallHint =>
        "install it with `npm install -g opencode-ai`, then sign in to a provider with "
        + "`opencode auth login`";

    private const string DefaultCliModel = "anthropic/claude-sonnet-4-6";

    /// <summary>Strips the "opencode/" catalog namespace, leaving the `provider/model` pair
    /// the CLI's `-m` expects ("anthropic/claude-sonnet-4-6"). Ids without the namespace pass
    /// through — a bare pair is already in CLI form.</summary>
    internal static string ToCliModel(string model)
    {
        var bare = model.StartsWith("opencode/", StringComparison.OrdinalIgnoreCase)
            ? model["opencode/".Length..]
            : model;
        // A namespace with nothing after it carries no provider, and OpenCode cannot guess
        // one — fall back to the anchor rather than shipping an unusable "-m".
        return string.IsNullOrWhiteSpace(bare) || bare.Equals("opencode", StringComparison.OrdinalIgnoreCase)
            ? DefaultCliModel
            : bare;
    }

    protected override string BuildStdin(AgentRequest request) =>
        // A resumed session already holds the system preamble and conversation — only the
        // new message travels.
        string.IsNullOrWhiteSpace(request.ResumeSessionId)
            ? $"""
               <system-instructions>
               {request.SystemPrompt}
               </system-instructions>

               <task>
               {request.UserMessage}
               </task>
               """
            : request.UserMessage;

    // Never reached: RunAttemptsAsync always builds the psi around a temp prompt file, whose
    // path is not known until the run starts. Implemented because the base declares it.
    protected override ProcessStartInfo BuildStartInfo(AgentRequest request, string model)
        => BuildPsi(request, model, promptFile: Path.Combine(Path.GetTempPath(), "orkka-opencode-prompt.md"));

    protected override async IAsyncEnumerable<AgentChunk> RunAttemptsAsync(
        AgentRequest request, string model, CliRunState state, CliRunOutcome outcome,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var promptFile = Path.Combine(Path.GetTempPath(), $"orkka-opencode-prompt-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(promptFile, BuildStdin(request), ct);
        try
        {
            await foreach (var chunk in RunCliAsync(
                               BuildPsi(request, model, promptFile), stdin: "",
                               request, state, outcome, ct))
                yield return chunk;
        }
        finally
        {
            try { File.Delete(promptFile); }
            catch { /* temp file — best effort */ }
        }
    }

    private static ProcessStartInfo BuildPsi(AgentRequest request, string model, string promptFile)
    {
        List<string> args =
        [
            "run",
            "--format", "json",
            "-m", ToCliModel(model),
        ];

        // Phase → agent. OpenCode ships two primary agents: `build` (full tool access) and
        // `plan`, which is read-only by design. Selecting `plan` is how Planning and Review
        // are kept from mutating the worktree — `run` has no per-tool deny flag, so the agent
        // choice IS the sandbox here. Developing and Testing get `build` plus --auto so tool
        // permission prompts never block a non-interactive run (QA must start the app and
        // write E2E tests).
        if (request.Phase is AgentPhase.Developing or AgentPhase.Testing)
            args.AddRange(["--agent", "build", "--auto"]);
        else
            args.AddRange(["--agent", "plan"]);

        // Resume continues the previous run's session, so the caller sends only the new
        // message — no system preamble, no re-injected history.
        if (!string.IsNullOrWhiteSpace(request.ResumeSessionId))
            args.AddRange(["--session", request.ResumeSessionId]);

        // Positional message, last. Short by construction — the real prompt is in the file.
        args.Add($"Read the file at {promptFile} and execute the instructions in it.");

        var (fileName, shellArgs) = ShellCommand("opencode", args);
        // OpenCode authenticates against the user's own provider logins (opencode auth login).
        return CreateStartInfo(fileName, shellArgs, request.WorktreePath);
    }

    /// <summary>
    /// Maps `opencode run --format json` JSONL events. Every event carries `type`,
    /// `sessionID` and a `part` payload:
    ///   step_start                                → skipped (no timeline value on its own)
    ///   text        + part.text                   → Text (and the run's final text)
    ///   tool_use    + part.tool / part.state.*    → ToolUse, plus ToolResult once completed
    ///   step_finish + part.tokens.*               → usage accumulation
    ///   error       + error.data.message          → captured as the stream error
    ///
    /// Text parts STREAM AS ONE GROWING PART — the same part.id is re-emitted with more text
    /// rather than a fresh fragment each time — so the final text is folded through
    /// <see cref="CliRunState.AppendTextPart"/>, which overwrites the open part instead of
    /// stacking every prefix on top of itself.
    ///
    /// Known upstream gap: `run --format json` has open reports of exiting before it emits
    /// the assistant text and step_finish in containerized and some non-interactive setups
    /// (sst/opencode issues #26855, #31365, #31435). That surfaces here as a run whose Done
    /// chunk carries empty text rather than as a crash, and it is an upstream fix — nothing
    /// in this mapper can invent events the CLI never wrote.
    /// </summary>
    protected internal override IEnumerable<AgentChunk> MapLine(string line, CliRunState state)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        state.SessionId ??= GetString(root, "sessionID") ?? GetString(root, "sessionId");

        var type = GetString(root, "type");
        var hasPart = root.TryGetProperty("part", out var part) && part.ValueKind == JsonValueKind.Object;

        switch (type)
        {
            case "text" when hasPart:
            {
                var text = GetString(part, "text") ?? "";
                if (text.Length > 0)
                {
                    state.AppendTextPart(GetString(part, "id"), text);
                    yield return new AgentChunk(AgentChunkType.Text, text);
                }
                break;
            }

            case "reasoning" when hasPart:
            {
                var thought = GetString(part, "text") ?? "";
                if (thought.Length > 0)
                    yield return new AgentChunk(AgentChunkType.Thinking, thought);
                break;
            }

            case "tool_use" when hasPart:
            {
                var tool = GetString(part, "tool") ?? "tool";
                var hasState = part.TryGetProperty("state", out var toolState)
                    && toolState.ValueKind == JsonValueKind.Object;
                var status = hasState ? GetString(toolState, "status") : null;

                // The event is documented as firing when the tool COMPLETES, so one event
                // must produce both the call and its result — a ToolUse alone would leave
                // the timeline showing a tool that never returned. (Tool-call capping in the
                // base counts the ToolUse, which stays one-per-tool either way.)
                var summary = hasState
                    ? GetString(toolState, "title")
                      ?? (toolState.TryGetProperty("input", out var input) ? input.ToString() : null)
                      ?? ""
                    : "";
                yield return new AgentChunk(AgentChunkType.ToolUse, $"{tool}: {summary}");

                if (status is null or "completed" or "error")
                {
                    var output = hasState
                        ? GetString(toolState, "output") ?? GetString(toolState, "title") ?? ""
                        : "";
                    yield return new AgentChunk(AgentChunkType.ToolResult, output);
                }
                break;
            }

            case "step_finish" when hasPart:
            {
                if (part.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
                    AccumulateUsage(tokens, state);
                break;
            }

            case "error":
            {
                state.StreamError = ExtractError(root) ?? "The opencode CLI reported an error.";
                break;
            }
        }
    }

    /// <summary>step_finish tokens: { input, output, reasoning, cache: { read, write } }.
    /// Steps ACCUMULATE — a multi-step run emits one of these per step, and each reports only
    /// its own tokens, so these add rather than replace (unlike the cumulative usage totals
    /// codex and copilot report).</summary>
    private static void AccumulateUsage(JsonElement tokens, CliRunState state)
    {
        state.PromptTokens += GetInt(tokens, "input");
        state.CompletionTokens += GetInt(tokens, "output") + GetInt(tokens, "reasoning");
        if (tokens.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
            state.CacheReadTokens += GetInt(cache, "read");
    }

    /// <summary>Error events nest the message under error.data.message, with error.name as
    /// the type. Falls back through the flatter spellings.</summary>
    private static string? ExtractError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error))
            return null;
        if (error.ValueKind == JsonValueKind.String)
            return error.GetString();
        if (error.ValueKind != JsonValueKind.Object)
            return null;

        var message = error.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? GetString(data, "message")
            : null;
        message ??= GetString(error, "message");
        var name = GetString(error, "name");

        return (name, message) switch
        {
            (not null, not null) => $"{name}: {message}",
            (not null, null) => name,
            _ => message,
        };
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt32()
            : 0;
}
