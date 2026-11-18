namespace Orkka.Modules.Execution.Agents;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Runs GitHub Copilot agents through the `copilot` CLI (npm @github/copilot — the agentic
/// CLI, not the deprecated gh-copilot extension). Sign-in is the user's `copilot login` (OS
/// credential store / ~/.copilot, an org-managed Business/Enterprise seat included), the gh
/// CLI's login, or an environment-based one the developer set for this machine/workspace — a
/// GitHub token, a GitHub Enterprise host (<see cref="ProviderAuthEnvironment.Copilot"/>). Catalog model ids are namespaced "copilot/&lt;model&gt;"
/// because Copilot serves models whose bare ids ("gpt-5", "claude-sonnet-4.5") collide with
/// the vendor prefixes in ModelProviders — the prefix is stripped before the CLI sees it.
/// Uses `--output-format json` (JSONL events) with the prompt piped over stdin (the CLI
/// treats piped stdin as the prompt when -p is absent; -p would hit the ~32K Windows
/// command-line cap). If the stdin path fails before any event parses (github/copilot-cli
/// issue #1181 reports non-interactive stdin runs exiting immediately in some setups), one
/// fallback attempt passes a short -p pointing at a temp instructions file. Known gaps
/// versus the Claude SDK path: no system prompt flag (sent as a delimited preamble in the
/// user turn), no MaxTurns (wall-clock + tool-event caps only), and read-only phases rely
/// on --deny-tool write/shell rather than per-tool allow-lists. Event schema and exit codes
/// are underdocumented — the mapper is deliberately tolerant and needs real-CLI verification.
/// </summary>
public sealed class CopilotAgentInvoker(
    IOptions<AgentLimitsOptions> agentLimits,
    IAgentAuditLog auditLog,
    ILogger<CopilotAgentInvoker> logger,
    IProviderAuthFeedback? authFeedback = null)
    : CliAgentInvokerBase(agentLimits.Value, auditLog, logger, authFeedback)
{
    protected override AiProvider Provider => AiProvider.GitHubCopilot;
    protected override string CliName => "copilot";
    protected override string DefaultModel => "copilot/auto";

    protected override string InstallHint =>
        "install it with `npm install -g @github/copilot`, then sign in with `copilot login`";

    /// <summary>Strips the "copilot/" catalog namespace; the CLI expects the bare model id
    /// ("auto", "claude-sonnet-4.5", "gpt-5"). Ids without the namespace pass through.</summary>
    /// <summary><c>--add-dir</c> for each sibling repository, only in phases that cannot write.</summary>
    /// <summary>Developing and Testing need writes + shell without prompts (QA starts the app and
    /// writes E2E tests), screened by Orkka's command guard as deny rules (a denied shell(...)
    /// prefix wins over --allow-all-tools; paths stay confined to the working directory because
    /// --allow-all-paths is never passed). Every other phase — planning, PM, review, chat and a
    /// task's Discuss chat — denies the mutating tools outright.</summary>
    internal static List<string> ToolPermissionArgs(AgentPhase phase)
    {
        if (phase is not (AgentPhase.Developing or AgentPhase.Testing))
            return ["--allow-all-tools", "--deny-tool", "write", "--deny-tool", "shell"];

        List<string> args = ["--allow-all-tools"];
        foreach (var rule in AgentBashGuard.CommandPrefixRules)
            args.AddRange(["--deny-tool", $"shell({string.Join(' ', rule)})"]);
        return args;
    }

    internal static IEnumerable<string> SiblingDirArgs(AgentPhase phase, IReadOnlyList<string>? dirs)
    {
        if (phase is AgentPhase.Developing or AgentPhase.Testing)
            yield break;
        foreach (var dir in dirs ?? [])
        {
            yield return "--add-dir";
            yield return dir;
        }
    }

    internal static string ToCliModel(string model)
    {
        var bare = model.StartsWith("copilot/", StringComparison.OrdinalIgnoreCase)
            ? model["copilot/".Length..]
            : model;
        return string.IsNullOrWhiteSpace(bare) || bare.Equals("copilot", StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : bare;
    }

    protected override ProcessStartInfo BuildStartInfo(AgentRequest request, string model)
        => BuildPsi(request, model, promptFile: null);

    protected override string BuildStdin(AgentRequest request) =>
        // A resumed session already holds the system preamble and conversation — only the
        // new message travels. (Scaffolded: resume ids are only handed to this invoker once
        // copilot joins the chat-resume allowlist after real-CLI verification.)
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

    protected override async IAsyncEnumerable<AgentChunk> RunAttemptsAsync(
        AgentRequest request, string model, CliRunState state, CliRunOutcome outcome,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var chunk in RunCliAsync(
                           BuildPsi(request, model, promptFile: null), BuildStdin(request),
                           request, state, outcome, ct))
            yield return chunk;

        // Piped-stdin-as-prompt is the documented behavior but has reported failures
        // (issue #1181). A fast failure with nothing parsed gets one retry with the prompt
        // in a temp file and a short -p that points the agent at it.
        if (!outcome.ProcessFailed || outcome.AnyEventParsed)
            yield break;

        logger.LogWarning(
            "[Agent:copilot] stdin prompt attempt failed with no parsed events — retrying with a temp prompt file. Original failure: {Message}",
            outcome.FailureMessage);
        outcome.ProcessFailed = false;
        outcome.FailureMessage = null;
        outcome.ExitCode = 0;

        var promptFile = Path.Combine(Path.GetTempPath(), $"orkka-copilot-prompt-{Guid.NewGuid():N}.md");
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

    private ProcessStartInfo BuildPsi(AgentRequest request, string model, string? promptFile)
    {
        List<string> args =
        [
            "--output-format", "json",
            "--model", ToCliModel(model),
            "--no-ask-user",
            "--no-color",
            "--no-auto-update",
        ];

        // Scaffolded resume (needs real-CLI verification before copilot joins the
        // chat-resume allowlist): continue the previous session instead of cold-booting.
        if (!string.IsNullOrWhiteSpace(request.ResumeSessionId))
            args.AddRange(["--resume", request.ResumeSessionId]);

        // Developing and Testing need writes + shell without prompts (QA starts the app and
        // writes E2E tests). Other phases keep --no-ask-user's refusal behavior for anything
        // outside the allow set by denying the mutating tools.
        args.AddRange(ToolPermissionArgs(request.Phase));

        // Sibling repositories for reference. Copilot's path confinement is the working
        // directory plus these, and --add-dir has no read-only form: in a phase that writes,
        // it would let Copilot edit the developer's own checkouts. So siblings are granted
        // only where writes and shell are denied (planning, PM, review, chat).
        args.AddRange(SiblingDirArgs(request.Phase, request.ReadOnlyDirs));

        if (promptFile is not null)
            args.AddRange(["-p", $"Read the file at {promptFile} and execute the instructions in it."]);

        // Environment-based sign-in: a GitHub token (COPILOT_GITHUB_TOKEN, then GH_TOKEN /
        // GITHUB_TOKEN — the CLI's own precedence), a GitHub Enterprise host, a BYOK key. The
        // sanitizer blanked them; only Copilot's allowlisted names that are set for this run's
        // workspace (or the machine) come back, read per spawn so a value added while the
        // runner is up serves the very next run. Every credential among them is also named in
        // --secret-env-vars, which redacts it from the shell and MCP server environments the
        // CLI starts (docs.github.com copilot-cli-reference).
        var auth = ProviderAuthEnvironment.Resolve(Provider, request.Workspace);
        args.AddRange(SecretEnvVarArgs(auth));

        var (fileName, shellArgs) = ShellCommand("copilot", args);
        var psi = CreateStartInfo(fileName, shellArgs, request.WorktreePath);
        foreach (var (name, value) in auth.Variables)
            psi.Environment[name] = value;
        return psi;
    }

    /// <summary><c>--secret-env-vars=A,B</c> for every credential the run passes; nothing when
    /// it passes none.</summary>
    internal static string[] SecretEnvVarArgs(ProviderAuthResolution auth)
    {
        var secrets = auth.Variables.Keys.Where(ProviderAuthEnvironment.IsSecret).Order(StringComparer.Ordinal).ToList();
        return secrets.Count == 0 ? [] : [$"--secret-env-vars={string.Join(',', secrets)}"];
    }

    /// <summary>
    /// Maps `copilot --output-format json` JSONL events. The schema is underdocumented and
    /// not a stable contract, so this accepts several plausible shapes (the CLI is built on
    /// the Claude Agent SDK, so both Claude-style and item-style events are handled) and
    /// skips anything unrecognized:
    ///   assistant/message/text events (flat "text"/"content" or nested content[] parts) → Text
    ///   reasoning / thinking / thought → Thinking
    ///   tool_call / tool_use / item.started tool items → ToolUse
    ///   tool_result / tool_response / item.completed tool items → ToolResult
    ///   any event carrying "usage" (input_tokens/output_tokens/cache_read_input_tokens) → usage
    ///   error / turn.failed → captured as the stream error
    /// </summary>
    protected internal override IEnumerable<AgentChunk> MapLine(string line, CliRunState state)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        // Usage can ride on any event (typically the final one). Totals are cumulative
        // when repeated — replace, don't add.
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            AccumulateUsage(usage, state);

        // Session handle for --resume, whichever field name the CLI uses (underdocumented
        // schema — accept the plausible spellings, keep the first one seen).
        state.SessionId ??= GetString(root, "session_id")
            ?? GetString(root, "sessionId")
            ?? GetString(root, "thread_id");

        var type = GetString(root, "type");
        switch (type)
        {
            case "assistant" or "message" or "text" or "agent_message" or "response":
            {
                var text = ExtractText(root);
                if (!string.IsNullOrEmpty(text))
                {
                    state.FinalText = text;
                    yield return new AgentChunk(AgentChunkType.Text, text);
                }
                break;
            }

            case "reasoning" or "thinking" or "thought":
            {
                var thought = GetString(root, "text") ?? GetString(root, "content") ?? ExtractText(root);
                if (!string.IsNullOrEmpty(thought))
                    yield return new AgentChunk(AgentChunkType.Thinking, thought);
                break;
            }

            case "tool_call" or "tool_use":
            {
                var name = GetString(root, "name") ?? GetString(root, "tool_name") ?? GetString(root, "tool") ?? "tool";
                var argsText = root.TryGetProperty("arguments", out var arguments) ? arguments.ToString()
                    : root.TryGetProperty("args", out var a) ? a.ToString()
                    : root.TryGetProperty("input", out var input) ? input.ToString()
                    : "";
                yield return new AgentChunk(AgentChunkType.ToolUse, $"{name}: {argsText}");
                break;
            }

            case "tool_result" or "tool_response":
            {
                var content = GetString(root, "output") ?? GetString(root, "content")
                    ?? (root.TryGetProperty("result", out var result) ? result.ToString() : "");
                yield return new AgentChunk(AgentChunkType.ToolResult, content ?? "");
                break;
            }

            case "item.started" or "item.updated" or "item.completed":
            {
                foreach (var chunk in MapItemEvent(root, type!, state))
                    yield return chunk;
                break;
            }

            case "error" or "turn.failed":
            {
                state.StreamError = GetString(root, "message")
                    ?? (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                        ? GetString(err, "message")
                        : GetString(root, "error"))
                    ?? "The copilot CLI reported an error.";
                break;
            }
        }
    }

    /// <summary>Codex-style envelope, plausible given shared JSONL conventions:
    /// { type: "item.completed", item: { type: "agent_message" | tool item, ... } }.</summary>
    private static IEnumerable<AgentChunk> MapItemEvent(JsonElement root, string type, CliRunState state)
    {
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            yield break;

        var itemType = GetString(item, "item_type") ?? GetString(item, "type");
        var completed = type == "item.completed";
        switch (itemType)
        {
            case "agent_message" or "message" or "text" when completed:
                var text = GetString(item, "text") ?? GetString(item, "content") ?? "";
                if (text.Length > 0)
                {
                    state.FinalText = text;
                    yield return new AgentChunk(AgentChunkType.Text, text);
                }
                break;

            case "reasoning" when completed:
                var thought = GetString(item, "text") ?? GetString(item, "summary") ?? "";
                if (thought.Length > 0)
                    yield return new AgentChunk(AgentChunkType.Thinking, thought);
                break;

            case "tool_call" or "command_execution" or "file_change" or "mcp_tool_call":
                var summary = GetString(item, "command")
                              ?? GetString(item, "name")
                              ?? GetString(item, "tool")
                              ?? "";
                if (type == "item.started")
                    yield return new AgentChunk(AgentChunkType.ToolUse, $"{itemType}: {summary}");
                else if (completed)
                    yield return new AgentChunk(AgentChunkType.ToolResult,
                        GetString(item, "output") ?? GetString(item, "aggregated_output") ?? summary);
                break;
        }
    }

    private static string? ExtractText(JsonElement root)
    {
        var flat = GetString(root, "text") ?? GetString(root, "content");
        if (flat is not null)
            return flat;

        // Claude-SDK-style nesting: { message: { content: [ { type: "text", text: "..." } ] } }
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
                return content.GetString();
            if (content.ValueKind == JsonValueKind.Array)
            {
                var parts = content.EnumerateArray()
                    .Select(p => GetString(p, "text"))
                    .Where(t => !string.IsNullOrEmpty(t));
                var joined = string.Join("", parts);
                return joined.Length > 0 ? joined : null;
            }
        }

        return null;
    }

    private static void AccumulateUsage(JsonElement usage, CliRunState state)
    {
        var prompt = GetInt(usage, "input_tokens") + GetInt(usage, "prompt_tokens");
        var completion = GetInt(usage, "output_tokens") + GetInt(usage, "completion_tokens");
        var cached = GetInt(usage, "cache_read_input_tokens") + GetInt(usage, "cached_input_tokens");
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
