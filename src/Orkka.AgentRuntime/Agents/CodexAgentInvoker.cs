namespace Orkka.Modules.Execution.Agents;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Runs OpenAI agents through the `codex` CLI. Sign-in is the CLI's own (`codex login`:
/// ChatGPT plan or a stored API key), or an environment-based one the developer set for this
/// machine/workspace (<see cref="ProviderAuthEnvironment"/>), which never reaches the agent's
/// shell (<see cref="ShellEnvironmentPolicyArgs"/>). Uses
/// `codex exec --json`,
/// which streams JSONL thread events. Known gaps versus the Claude SDK path: no system
/// prompt flag (sent as a delimited preamble in the user turn), no MaxTurns (wall-clock +
/// tool-event caps only), and tool restriction is the CLI's sandbox (read-only vs
/// workspace-write) plus Orkka's command guard as execpolicy rules (<see cref="CodexExecPolicy"/>)
/// rather than a per-call hook.
/// </summary>
public class CodexAgentInvoker : CliAgentInvokerBase
{
    /// <summary>Phase → Codex sandbox: Developing/Testing write inside the worktree (network on
    /// for installs); every other phase — planning, review, chat, a task's Discuss chat — runs
    /// <c>--sandbox read-only</c>.</summary>
    internal static string[] SandboxArgs(AgentPhase phase)
        => phase is AgentPhase.Developing or AgentPhase.Testing
            ? ["--sandbox", "workspace-write", "-c", "sandbox_workspace_write.network_access=true"]
            : ["--sandbox", "read-only"];

    public CodexAgentInvoker(
        IOptions<AgentLimitsOptions> agentLimits,
        IAgentAuditLog auditLog,
        ILogger<CodexAgentInvoker> logger,
        IProviderAuthFeedback? authFeedback = null)
        : base(agentLimits.Value, auditLog, logger, authFeedback) { }

    /// <summary>Subclass seam (Ollama drives the same codex CLI at a different backend).</summary>
    private protected CodexAgentInvoker(
        AgentLimitsOptions limits, IAgentAuditLog auditLog, ILogger logger,
        IProviderAuthFeedback? authFeedback = null)
        : base(limits, auditLog, logger, authFeedback) { }

    protected override AiProvider Provider => AiProvider.OpenAI;
    protected override string CliName => "codex";
    protected override string DefaultModel => "gpt-5.6-sol";

    protected override string InstallHint =>
        "install it with `npm install -g @openai/codex`, then sign in with `codex login`";

    /// <summary>Catalog model id → the id the codex CLI expects (subclasses strip their
    /// namespace prefix here).</summary>
    private protected virtual string ToCodexCliModel(string model) => model;

    /// <summary>Extra `-c key=value` config overrides prepended to every attempt —
    /// how a subclass points the CLI at a non-OpenAI backend.</summary>
    private protected virtual IReadOnlyList<string> ProviderConfigArgs() => [];

    protected override ProcessStartInfo BuildStartInfo(AgentRequest request, string model)
    {
        // Every option belongs to `exec` and goes BEFORE the `resume` subcommand, which accepts
        // almost none of its own (`codex exec resume <id> --cd …` is "unexpected argument").
        List<string> args = ["exec", "--json"];
        args.AddRange(ProviderConfigArgs());
        args.AddRange(
        [
            "-m", ToCodexCliModel(model),
            "--cd", request.WorktreePath,
            "--skip-git-repo-check",
        ]);

        // Phase → sandbox: Planning/Review must not mutate the worktree; Developing and Testing
        // get Codex's OS sandbox in workspace-write mode — writes confined to the worktree,
        // network on for package installs — with approvals off (exec is non-interactive; there
        // is nobody to ask). `--full-auto` was the old spelling and codex 0.16x rejects it.
        args.AddRange(SandboxArgs(request.Phase));

        args.AddRange(["-c", "approval_policy=never"]);

        // Environment-based sign-in (CODEX_API_KEY, an Azure OpenAI env_key, …) for this run's
        // workspace: passed to the CLI, kept out of every command it runs.
        var auth = ProviderAuthEnvironment.Resolve(Provider, request.Workspace);
        args.AddRange(ShellEnvironmentPolicyArgs(auth));

        // Orkka's command guard as execpolicy rules the CLI loads from the worktree.
        CodexExecPolicy.WriteInto(request.WorktreePath);

        // Resume continues the previous run's thread: the CLI reloads its own transcript
        // (~/.codex/sessions), so the caller sends only the new message — no system
        // preamble, no re-injected history. A rejected resume errors out and the caller
        // retries fresh with full context.
        if (!string.IsNullOrWhiteSpace(request.ResumeSessionId))
            args.AddRange(["resume", request.ResumeSessionId]);

        // "-" = read the prompt from stdin (Windows caps the whole command line at ~32K chars).
        args.Add("-");

        var (fileName, shellArgs) = ShellCommand("codex", args);
        var psi = CreateStartInfo(fileName, shellArgs, request.WorktreePath);
        foreach (var (name, value) in auth.Variables)
            psi.Environment[name] = value;
        return psi;
    }

    /// <summary>
    /// Codex's <c>shell_environment_policy</c> for a run that carries an env credential: the
    /// CLI's default secret-name excludes switched on (<c>*KEY*</c>, <c>*SECRET*</c>,
    /// <c>*TOKEN*</c>) plus an explicit exclude of every variable passed, so the agent's own
    /// commands never inherit them while the CLI still authenticates with them
    /// (codex-rs/protocol/src/shell_environment.rs populate_env; learn.chatgpt.com
    /// config-reference). Single-quoted TOML literal strings: no double quote or space has to
    /// survive a <c>cmd /c</c> shim on Windows. Nothing when no variable is passed — a
    /// subscription run keeps today's environment exactly.
    /// </summary>
    internal static string[] ShellEnvironmentPolicyArgs(ProviderAuthResolution auth)
    {
        if (auth.Variables.Count == 0)
            return [];

        var names = string.Join(",", auth.Variables.Keys.Order(StringComparer.Ordinal).Select(n => $"'{n}'"));
        return
        [
            "-c", "shell_environment_policy.ignore_default_excludes=false",
            "-c", $"shell_environment_policy.exclude=[{names}]",
        ];
    }

    protected override string BuildStdin(AgentRequest request) =>
        // A resumed thread already holds the system preamble and conversation — only the
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

    /// <summary>
    /// Maps `codex exec --json` thread events. Tolerant by design: the schema is not a
    /// stable contract, so unknown event/item types are skipped and property lookups are
    /// defensive. Shapes handled (item payloads carry item_type or type):
    ///   item.completed  + agent_message      → Text (also becomes the final result text)
    ///   item.completed  + reasoning          → Thinking
    ///   item.started    + command_execution / mcp_tool_call / web_search / file_change → ToolUse
    ///   item.completed  + command_execution / mcp_tool_call / web_search / file_change → ToolResult
    ///   turn.completed                       → usage accumulation (cached_input_tokens → CacheRead)
    ///   error / turn.failed                  → captured as the stream error
    /// </summary>
    protected internal override IEnumerable<AgentChunk> MapLine(string line, CliRunState state)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
            yield break;

        var type = typeProp.GetString();
        switch (type)
        {
            case "thread.started":
            {
                // Thread id = resumable session handle for `codex exec resume <id>`.
                state.SessionId = GetString(root, "thread_id") ?? state.SessionId;
                break;
            }

            case "item.started" or "item.updated" or "item.completed":
            {
                if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                    yield break;

                var itemType = GetString(item, "item_type") ?? GetString(item, "type");
                var completed = type == "item.completed";
                switch (itemType)
                {
                    case "agent_message" when completed:
                        var text = GetString(item, "text") ?? "";
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

                    case "command_execution" or "mcp_tool_call" or "web_search" or "file_change":
                        var summary = GetString(item, "command")
                                      ?? GetString(item, "query")
                                      ?? DescribeChanges(item)
                                      ?? GetString(item, "tool")
                                      ?? "";
                        if (type == "item.started")
                            yield return new AgentChunk(AgentChunkType.ToolUse, $"{itemType}: {summary}");
                        else if (completed)
                            yield return new AgentChunk(AgentChunkType.ToolResult,
                                GetString(item, "aggregated_output") ?? summary);
                        break;
                }
                break;
            }

            case "turn.completed":
            {
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    state.PromptTokens += GetInt(usage, "input_tokens");
                    state.CompletionTokens += GetInt(usage, "output_tokens");
                    state.CacheReadTokens += GetInt(usage, "cached_input_tokens");
                }
                break;
            }

            case "turn.failed" or "error":
            {
                var message = GetString(root, "message")
                              ?? (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                                  ? GetString(err, "message")
                                  : null)
                              ?? "The codex CLI reported an error.";
                state.StreamError = message;
                break;
            }
        }
    }

    private static string? DescribeChanges(JsonElement item)
    {
        if (!item.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
            return null;
        var parts = changes.EnumerateArray()
            .Select(c => $"{GetString(c, "kind") ?? "edit"} {GetString(c, "path") ?? "?"}")
            .ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
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
