namespace Orkka.Modules.Execution.Agents;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Runs xAI agents through the `grok` CLI (Grok Build — xAI's own terminal coding agent,
/// installed from https://x.ai/cli/install.sh). A conventional vendor CLI, so this looks
/// much like <see cref="CodexAgentInvoker"/>: catalog ids are bare ("grok-4.6") because
/// nothing else in the catalog starts with "grok", and no namespace stripping is needed.
///
/// Auth is the CLI's own login (a SuperGrok / X Premium Plus subscription). The env
/// sanitizer blanks XAI_API_KEY; no key is ever injected.
///
/// The prompt travels in a TEMP FILE rather than inline after `-p`. `grok` needs `-p` to
/// enter headless mode at all (a bare `grok` opens the fullscreen TUI and would hang a
/// non-interactive run), and stdin-as-prompt is not documented for it — but Windows caps
/// the whole command line at ~32K chars, which an Orkka system prompt plus task context
/// clears easily. A short `-p` pointing at a file costs one extra Read tool call and works
/// at every prompt size; inlining would fail unpredictably on exactly the largest runs.
///
/// Known gaps versus the Claude SDK path: no system prompt flag (sent as a delimited
/// preamble), no MaxTurns (wall-clock + tool-event caps are the only runaway bound), and
/// tool restriction is whole-session approval granularity rather than per-tool allow-lists.
/// The streaming-json event schema is documented only by example and is not a stable
/// contract, so <see cref="MapLine"/> accepts both shapes seen in the wild and needs
/// real-CLI verification.
/// </summary>
public sealed class GrokAgentInvoker(
    IOptions<AgentLimitsOptions> agentLimits,
    IAgentAuditLog auditLog,
    ILogger<GrokAgentInvoker> logger,
    IProviderAuthFeedback? authFeedback = null)
    : CliAgentInvokerBase(agentLimits.Value, auditLog, logger, authFeedback)
{
    protected override AiProvider Provider => AiProvider.XAI;
    protected override string CliName => "grok";
    protected override string DefaultModel => "grok-4.6";

    protected override string InstallHint =>
        "install the grok CLI on that machine, then run `grok` to complete the browser login";

    /// <summary>Catalog ids are already the CLI's ids; only an empty/degenerate value is
    /// corrected. Kept as a named seam so the model mapping stays visible next to its
    /// siblings, which all have a namespace to strip.</summary>
    internal static string ToCliModel(string model)
        => string.IsNullOrWhiteSpace(model) || model.Equals("grok", StringComparison.OrdinalIgnoreCase)
            ? "grok-4.6"
            : model;

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
        => BuildPsi(request, model, promptFile: Path.Combine(Path.GetTempPath(), "orkka-grok-prompt.md"));

    protected override async IAsyncEnumerable<AgentChunk> RunAttemptsAsync(
        AgentRequest request, string model, CliRunState state, CliRunOutcome outcome,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var promptFile = Path.Combine(Path.GetTempPath(), $"orkka-grok-prompt-{Guid.NewGuid():N}.md");
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
            "-p", $"Read the file at {promptFile} and execute the instructions in it.",
            "-m", ToCliModel(model),
            "--cwd", request.WorktreePath,
            "--output-format", "streaming-json",
            // Headless hygiene: no fullscreen TUI to drive, no background update check to
            // race the run (the flag exists precisely for scripts and CI).
            "--no-alt-screen",
            "--no-auto-update",
        ];

        // Resume continues the previous run's session, so the caller sends only the new
        // message — no system preamble, no re-injected history.
        if (!string.IsNullOrWhiteSpace(request.ResumeSessionId))
            args.AddRange(["--resume", request.ResumeSessionId]);

        // Developing and Testing need writes and shell without prompts (the QA lane's whole
        // job is starting the app and writing E2E tests). Everything else runs without
        // blanket approval, so a mutating tool stops at the CLI's own refusal instead of
        // editing the worktree during Planning or Review.
        if (request.Phase is AgentPhase.Developing or AgentPhase.Testing)
            args.Add("--always-approve");

        var (fileName, shellArgs) = ShellCommand("grok", args);
        return CreateStartInfo(fileName, shellArgs, request.WorktreePath);
    }

    /// <summary>
    /// Maps `grok -p ... --output-format streaming-json` events. Two shapes are documented
    /// for this flag and both are accepted, because picking one and being wrong yields a run
    /// that streams nothing and reports no error:
    ///
    ///   1. Type-tagged NDJSON — { "type": "text" | "thought" | "tool_call" |
    ///      "tool_call_update" | "usage" | "plan" | "error" | "end", ... }
    ///   2. Agent Client Protocol JSON-RPC — { "method": "session/update", "params": {
    ///      "update": { "sessionUpdate": "agent_message_chunk", "content": { "text": … } } } }
    ///      with { "id": N, "result": { "sessionId": … } } / { "id": N, "error": { … } }
    ///
    /// Text is APPENDED rather than replaced: both shapes stream partial chunks, and
    /// replacing would leave FinalText holding only the last fragment. A single whole-message
    /// event appends exactly once, so the accumulate-always rule is correct for both.
    /// Unknown types are skipped, never thrown.
    /// </summary>
    protected internal override IEnumerable<AgentChunk> MapLine(string line, CliRunState state)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        // ACP envelope — the JSON-RPC shape carries no top-level "type".
        if (GetString(root, "method") == "session/update")
        {
            foreach (var chunk in MapAcpUpdate(root, state))
                yield return chunk;
            yield break;
        }

        // ACP terminal frames: the prompt's result (carries the resumable session id) or a
        // JSON-RPC error.
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            state.SessionId ??= GetString(result, "sessionId") ?? GetString(result, "session_id");
            yield break;
        }
        if (root.TryGetProperty("error", out var rpcError) && rpcError.ValueKind == JsonValueKind.Object)
        {
            state.StreamError = GetString(rpcError, "message") ?? "The grok CLI reported an error.";
            yield break;
        }

        state.SessionId ??= GetString(root, "session_id") ?? GetString(root, "sessionId");

        // Usage can ride on its own event or on the terminal one. Totals are cumulative when
        // repeated — replace, don't add.
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            AccumulateUsage(usage, state);

        switch (GetString(root, "type"))
        {
            case "text" or "message" or "agent_message":
            {
                var text = GetString(root, "text") ?? GetString(root, "content") ?? "";
                if (text.Length > 0)
                {
                    state.FinalText += text;
                    yield return new AgentChunk(AgentChunkType.Text, text);
                }
                break;
            }

            case "thought" or "thinking" or "reasoning":
            {
                var thought = GetString(root, "text") ?? GetString(root, "content") ?? "";
                if (thought.Length > 0)
                    yield return new AgentChunk(AgentChunkType.Thinking, thought);
                break;
            }

            case "tool_call":
            {
                var name = GetString(root, "title") ?? GetString(root, "name")
                    ?? GetString(root, "tool") ?? "tool";
                yield return new AgentChunk(AgentChunkType.ToolUse, $"{name}: {DescribeInput(root)}");
                break;
            }

            // The update carries the outcome; anything not yet finished is progress noise the
            // timeline does not need.
            case "tool_call_update" when IsFinished(GetString(root, "status")):
            {
                yield return new AgentChunk(AgentChunkType.ToolResult, DescribeOutput(root));
                break;
            }

            case "usage":
            {
                // Already handled above when nested; also accept a flat usage event.
                AccumulateUsage(root, state);
                break;
            }

            case "error":
            {
                state.StreamError = GetString(root, "message")
                    ?? GetString(root, "error")
                    ?? "The grok CLI reported an error.";
                break;
            }
        }
    }

    /// <summary>ACP session/update frames. `sessionUpdate` names the payload kind; text lives
    /// under content.text (a content block) or content as a bare string.</summary>
    private static IEnumerable<AgentChunk> MapAcpUpdate(JsonElement root, CliRunState state)
    {
        if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
            yield break;

        state.SessionId ??= GetString(parameters, "sessionId") ?? GetString(parameters, "session_id");

        if (!parameters.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object)
            yield break;

        var kind = GetString(update, "sessionUpdate");
        var text = ExtractContentText(update);

        switch (kind)
        {
            case "agent_message_chunk":
                if (text.Length > 0)
                {
                    state.FinalText += text;
                    yield return new AgentChunk(AgentChunkType.Text, text);
                }
                break;

            case "agent_thought_chunk":
                if (text.Length > 0)
                    yield return new AgentChunk(AgentChunkType.Thinking, text);
                break;

            case "tool_call":
                var name = GetString(update, "title") ?? GetString(update, "kind") ?? "tool";
                yield return new AgentChunk(AgentChunkType.ToolUse, $"{name}: {DescribeInput(update)}");
                break;

            case "tool_call_update" when IsFinished(GetString(update, "status")):
                yield return new AgentChunk(AgentChunkType.ToolResult, DescribeOutput(update));
                break;
        }
    }

    /// <summary>ACP content: either { "content": { "text": … } }, a bare string, or an array
    /// of content blocks. Returns "" when there is no text to show.</summary>
    private static string ExtractContentText(JsonElement update)
    {
        if (!update.TryGetProperty("content", out var content))
            return "";

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? "",
            JsonValueKind.Object => GetString(content, "text") ?? "",
            JsonValueKind.Array => string.Concat(content.EnumerateArray()
                .Select(part => part.ValueKind == JsonValueKind.String
                    ? part.GetString() ?? ""
                    : GetString(part, "text") ?? "")),
            _ => "",
        };
    }

    /// <summary>A tool call's arguments, however this build spells them. Rendered into the
    /// timeline summary, so a miss costs a vaguer line and nothing more.</summary>
    private static string DescribeInput(JsonElement element)
        => GetString(element, "command")
           ?? GetString(element, "query")
           ?? (element.TryGetProperty("rawInput", out var raw) ? raw.ToString() : null)
           ?? (element.TryGetProperty("input", out var input) ? input.ToString() : null)
           ?? (element.TryGetProperty("arguments", out var args) ? args.ToString() : null)
           ?? "";

    private static string DescribeOutput(JsonElement element)
        => GetString(element, "output")
           ?? GetString(element, "aggregated_output")
           // Empty must fall THROUGH to the next candidate, not stop the chain: a content
           // block that carries no text would otherwise mask rawOutput and title and leave
           // the timeline showing a tool that returned nothing.
           ?? NullIfEmpty(ExtractContentText(element))
           ?? (element.TryGetProperty("rawOutput", out var raw) ? raw.ToString() : null)
           ?? GetString(element, "title")
           ?? "";

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    // ACP statuses are "pending" | "in_progress" | "completed" | "failed"; only the terminal
    // ones produce a result line. A missing status is treated as finished, since a build that
    // omits it emits the update once, at the end.
    private static bool IsFinished(string? status)
        => status is null or "completed" or "failed" or "error" or "done";

    private static void AccumulateUsage(JsonElement usage, CliRunState state)
    {
        var prompt = GetInt(usage, "input_tokens") + GetInt(usage, "prompt_tokens");
        var completion = GetInt(usage, "output_tokens") + GetInt(usage, "completion_tokens");
        var cached = GetInt(usage, "cached_input_tokens") + GetInt(usage, "cache_read_input_tokens");
        if (prompt > 0 || completion > 0 || cached > 0)
        {
            state.PromptTokens = prompt;
            state.CompletionTokens = completion;
            state.CacheReadTokens = cached;
        }
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
