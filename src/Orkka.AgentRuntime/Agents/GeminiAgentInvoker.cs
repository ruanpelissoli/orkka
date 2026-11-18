namespace Orkka.Modules.Execution.Agents;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Runs Google agents through the `gemini` CLI. Local edition: the user's own auth —
/// ~/.gemini/oauth_creds.json (legacy OAuth; Google closed the free personal tier
/// 2026-06-18) or an API key the CLI dotenv-loads itself from the GeminiDotenvGuard
/// file at the workspace root (a controlled mirror of ~/.gemini/.env — see that class
/// for why the walk must be stopped there). The runner never injects a local key into
/// the process env: the sanitizer blanks GEMINI_API_KEY/GOOGLE_API_KEY by design.
/// Uses headless mode with `--output-format stream-json`
/// (JSONL events), falling back to a single `--output-format json` document on CLI
/// versions that predate stream-json. The system prompt travels via a GEMINI_SYSTEM_MD
/// temp file, which REPLACES the CLI's built-in system prompt. Known gaps versus the
/// Claude SDK path: no MaxTurns, and Planning/Review restriction relies on the CLI's
/// non-interactive approval behavior (write/shell tools need approval and are refused)
/// rather than per-tool allow-lists; Developing runs with --approval-mode yolo.
/// </summary>
public sealed class GeminiAgentInvoker(
    IOptions<AgentLimitsOptions> agentLimits,
    IAgentAuditLog auditLog,
    ILogger<GeminiAgentInvoker> logger,
    IProviderAuthFeedback? authFeedback = null)
    : CliAgentInvokerBase(agentLimits.Value, auditLog, logger, authFeedback)
{
    protected override AiProvider Provider => AiProvider.Google;
    protected override string CliName => "gemini";
    protected override string DefaultModel => "gemini-3.6-flash";

    protected override string InstallHint =>
        "install it with `npm install -g @google/gemini-cli` and configure API-key auth "
        + "(GEMINI_API_KEY in ~/.gemini/.env)";

    /// <summary>Catalog/legacy model id → an id the generateContent API actually serves.
    /// Two generations of dead ids land here. Plain "gemini-3-pro"/"gemini-3-flash" were
    /// Code Assist tier aliases that the API-key backend (the only auth path since the
    /// 2026-06-18 OAuth shutdown) 404s with ModelNotFoundError. The "-preview" ids that
    /// replaced them are dead too: gemini-3-pro-preview was SHUT DOWN 2026-03-09 and
    /// gemini-3-flash-preview is deprecated behind it (ai.google.dev/gemini-api/docs/
    /// deprecations, verified 2026-08-15), so both now resolve to Google's own named
    /// replacements. Mapped unconditionally so agents saved against any of the four keep
    /// running instead of failing with model-not-found.
    ///
    /// Usage still bills under the id the AGENT is pinned to, not the id resolved here —
    /// which is why CostCalculator prices all four at their replacement's rate.</summary>
    private static readonly Dictionary<string, string> ApiModelAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini-3-pro"] = "gemini-3.1-pro-preview",
            ["gemini-3-pro-preview"] = "gemini-3.1-pro-preview",
            ["gemini-3-flash"] = "gemini-3.6-flash",
            ["gemini-3-flash-preview"] = "gemini-3.6-flash",
        };

    // `internal`, not `protected internal`: the type is sealed, so the protected half can
    // never apply — it only produced CS0628. The internal half is what tests actually use.
    internal static string ToGeminiCliModel(string model) =>
        ApiModelAliases.GetValueOrDefault(model, model);

    protected override ProcessStartInfo BuildStartInfo(AgentRequest request, string model)
        => BuildPsi(request, model, "stream-json", systemFile: null);

    // The prompt arrives on stdin (headless gemini reads piped stdin as the prompt; -p has
    // the same ~32K Windows command-line problem the Claude path documents).
    protected override string BuildStdin(AgentRequest request) => request.UserMessage;

    protected override async IAsyncEnumerable<AgentChunk> RunAttemptsAsync(
        AgentRequest request, string model, CliRunState state, CliRunOutcome outcome,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var systemFile = Path.Combine(Path.GetTempPath(), $"orkka-gemini-system-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(systemFile, request.SystemPrompt, ct);
        try
        {
            var psi = BuildPsi(request, model, "stream-json", systemFile);
            await foreach (var chunk in RunCliAsync(psi, BuildStdin(request), request, state, outcome, ct))
                yield return chunk;

            // Older CLIs know `json` but not `stream-json`: a fast argument-error exit with
            // no parsed events. Retry once, non-streaming.
            if (outcome.ProcessFailed && !outcome.AnyEventParsed
                && LooksLikeUnsupportedFormat(outcome.FailureMessage))
            {
                logger.LogWarning(
                    "[Agent:gemini] stream-json appears unsupported by the installed CLI — retrying with --output-format json (no incremental streaming)");
                outcome.ProcessFailed = false;
                outcome.FailureMessage = null;
                outcome.ExitCode = 0;

                await foreach (var chunk in RunJsonFallbackAsync(request, model, systemFile, state, outcome, ct))
                    yield return chunk;
            }

            // Personal OAuth sign-ins are dead (Google closed the free Code Assist tier,
            // 2026-06-18): affected runs fail with IneligibleTierError in stderr or the
            // error event. Swap that raw noise for guidance the user can act on.
            outcome.FailureMessage = GeminiCliErrors.Describe(outcome.FailureMessage) ?? outcome.FailureMessage;
            state.StreamError = GeminiCliErrors.Describe(state.StreamError) ?? state.StreamError;
        }
        finally
        {
            try { File.Delete(systemFile); }
            catch { /* temp file — best effort */ }
        }
    }

    private ProcessStartInfo BuildPsi(
        AgentRequest request, string model, string outputFormat, string? systemFile)
    {
        List<string> args =
        [
            "-m", ToGeminiCliModel(model),
            "--output-format", outputFormat,
        ];

        // Developing and Testing need writes + shell without prompts (QA starts the app and
        // writes E2E tests). Other phases stay on the default approval mode: in
        // non-interactive runs, tools that require approval (writes, shell) are refused,
        // which approximates a read-only profile.
        if (request.Phase is AgentPhase.Developing or AgentPhase.Testing)
            args.AddRange(["--approval-mode", "yolo"]);

        var (fileName, shellArgs) = ShellCommand("gemini", args);
        var psi = CreateStartInfo(fileName, shellArgs, request.WorktreePath);

        // Set AFTER the sanitizer (which blanks GEMINI*-marked vars): the system-prompt
        // override file is ours, not an inherited secret.
        if (systemFile is not null)
            psi.Environment["GEMINI_SYSTEM_MD"] = systemFile;
        return psi;
    }

    private static bool LooksLikeUnsupportedFormat(string? failureMessage) =>
        failureMessage is not null
        && (failureMessage.Contains("stream-json", StringComparison.OrdinalIgnoreCase)
            || failureMessage.Contains("output-format", StringComparison.OrdinalIgnoreCase)
            || failureMessage.Contains("unknown option", StringComparison.OrdinalIgnoreCase)
            || failureMessage.Contains("invalid", StringComparison.OrdinalIgnoreCase));

    /// <summary>Non-streaming fallback: one JSON document on stdout —
    /// { "response": "...", "stats": { "models": { "<model>": { "tokens": {...} } } } }.</summary>
    private async IAsyncEnumerable<AgentChunk> RunJsonFallbackAsync(
        AgentRequest request, string model, string systemFile,
        CliRunState state, CliRunOutcome outcome,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var psi = BuildPsi(request, model, "json", systemFile);
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            outcome.ProcessFailed = true;
            outcome.FailureMessage = $"Could not start the gemini CLI: {ex.Message}. Is it installed and on PATH?";
            yield break;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.StandardInput.WriteAsync(BuildStdin(request).AsMemory(), ct);
        process.StandardInput.Close();
        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            outcome.ExitCode = process.ExitCode;
            outcome.ProcessFailed = true;
            outcome.FailureMessage = string.IsNullOrWhiteSpace(stderr)
                ? $"gemini CLI exited with code {process.ExitCode}."
                : $"gemini CLI exited with code {process.ExitCode}.\n--- gemini CLI stderr ---\n{stderr.Trim()}";
            yield break;
        }

        AgentChunk? textChunk = null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.TryGetProperty("response", out var response)
                && response.ValueKind == JsonValueKind.String)
            {
                state.FinalText = response.GetString() ?? "";
                if (state.FinalText.Length > 0)
                    textChunk = new AgentChunk(AgentChunkType.Text, state.FinalText);
            }
            if (root.TryGetProperty("stats", out var stats))
                AccumulateStats(stats, state);
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                state.StreamError = msg.GetString();
            outcome.AnyEventParsed = true;
        }
        catch (JsonException ex)
        {
            outcome.ProcessFailed = true;
            outcome.FailureMessage = $"gemini CLI produced unparseable JSON output: {ex.Message}";
            yield break;
        }

        if (textChunk is not null)
            yield return textChunk;
    }

    /// <summary>
    /// Maps stream-json events. The gemini CLI's event schema is not a stable contract, so
    /// this accepts several plausible shapes and skips anything unrecognized:
    ///   assistant/message text (flat "text"/"content" or nested message.content[] parts) → Text
    ///   thought / thinking / reasoning → Thinking
    ///   tool_call / tool_use → ToolUse;  tool_result / tool_response → ToolResult
    ///   result / stats (or any event carrying "stats") → final text + token accumulation
    ///   error → captured as the stream error
    /// </summary>
    protected internal override IEnumerable<AgentChunk> MapLine(string line, CliRunState state)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        // Any event carrying stats contributes usage (typically the final one).
        if (root.TryGetProperty("stats", out var stats))
            AccumulateStats(stats, state);
        if (root.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(resp.GetString()))
            state.FinalText = resp.GetString()!;

        var type = root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
            ? typeProp.GetString()
            : null;

        switch (type)
        {
            case "assistant" or "message" or "content" or "text":
            {
                var text = ExtractText(root);
                if (!string.IsNullOrEmpty(text))
                {
                    state.FinalText = text;
                    yield return new AgentChunk(AgentChunkType.Text, text);
                }
                break;
            }

            case "thought" or "thinking" or "reasoning":
            {
                var thought = GetString(root, "text") ?? GetString(root, "content") ?? GetString(root, "thought");
                if (!string.IsNullOrEmpty(thought))
                    yield return new AgentChunk(AgentChunkType.Thinking, thought);
                break;
            }

            case "tool_call" or "tool_use" or "tool_code":
            {
                var name = GetString(root, "name") ?? GetString(root, "tool_name") ?? "tool";
                var argsText = root.TryGetProperty("args", out var args) ? args.ToString()
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

            case "error":
            {
                state.StreamError = GetString(root, "message") ?? GetString(root, "error")
                    ?? "The gemini CLI reported an error.";
                break;
            }
        }
    }

    private static string? ExtractText(JsonElement root)
    {
        var flat = GetString(root, "text") ?? GetString(root, "content");
        if (flat is not null)
            return flat;

        // Nested message shape: { message: { content: [ { type: "text", text: "..." } ] } }
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

    /// <summary>stats.models.&lt;model-id&gt;.tokens.{prompt,candidates,cached} summed across models.</summary>
    private static void AccumulateStats(JsonElement stats, CliRunState state)
    {
        if (stats.ValueKind != JsonValueKind.Object
            || !stats.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Object)
            return;

        int prompt = 0, candidates = 0, cached = 0;
        foreach (var model in models.EnumerateObject())
        {
            if (model.Value.ValueKind != JsonValueKind.Object
                || !model.Value.TryGetProperty("tokens", out var tokens)
                || tokens.ValueKind != JsonValueKind.Object)
                continue;
            prompt += GetInt(tokens, "prompt");
            candidates += GetInt(tokens, "candidates");
            cached += GetInt(tokens, "cached");
        }

        // Stats are cumulative totals for the run — replace, don't add.
        if (prompt > 0 || candidates > 0 || cached > 0)
        {
            state.PromptTokens = prompt;
            state.CompletionTokens = candidates;
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
