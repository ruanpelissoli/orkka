namespace Orkka.Modules.Execution.Agents;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Runs agents on the user's local Ollama models by driving the codex CLI against
/// Ollama's OpenAI-compatible API (`-c model_provider=ollama` + a base_url override).
/// Ollama alone is a bare LLM server — it cannot edit files or run commands — so the
/// codex CLI supplies the agentic harness (tools, sandbox, JSONL event stream), exactly
/// the machinery <see cref="CodexAgentInvoker"/> already parses. Local edition only:
/// no login and no API key anywhere (a custom codex model provider with no env_key
/// needs no auth), and usage costs nothing. Catalog ids are namespaced
/// "ollama/&lt;model:tag&gt;" because bare Ollama ids ("gpt-oss:20b", "qwen3-coder")
/// would collide with the vendor prefixes in ModelProviders.
/// </summary>
public sealed class OllamaAgentInvoker(
    IOptions<AgentLimitsOptions> agentLimits,
    IAgentAuditLog auditLog,
    ILogger<OllamaAgentInvoker> logger,
    IProviderAuthFeedback? authFeedback = null)
    : CodexAgentInvoker(agentLimits.Value, auditLog, logger, authFeedback)
{
    protected override AiProvider Provider => AiProvider.Ollama;
    protected override string DefaultModel => "ollama/gpt-oss:20b";

    // The missing CLI is codex, not ollama — say why codex is involved at all.
    protected override string InstallHint =>
        "install the codex CLI with `npm install -g @openai/codex` (Ollama models are "
        + "driven through it — free, no OpenAI account needed)";

    /// <summary>Strips the "ollama/" catalog namespace; the CLI gets the bare Ollama model
    /// id ("gpt-oss:20b", "qwen3-coder"). Ids without the namespace pass through.</summary>
    internal static string ToCliModel(string model)
    {
        var bare = model.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase)
            ? model["ollama/".Length..]
            : model;
        return string.IsNullOrWhiteSpace(bare) || bare.Equals("ollama", StringComparison.OrdinalIgnoreCase)
            ? "gpt-oss:20b"
            : bare;
    }

    private protected override string ToCodexCliModel(string model) => ToCliModel(model);

    private protected override IReadOnlyList<string> ProviderConfigArgs()
        => BuildProviderConfigArgs(OllamaEnvironment.BaseUrl);

    /// <summary>The `-c` overrides that point codex at the Ollama server. `wire_api=chat`
    /// selects the OpenAI chat-completions protocol Ollama serves under /v1. Values that
    /// fail codex's TOML parse (like the URL) are treated as plain strings by the CLI.</summary>
    internal static string[] BuildProviderConfigArgs(string baseUrl) =>
    [
        "-c", "model_provider=ollama",
        "-c", "model_providers.ollama.name=Ollama",
        "-c", $"model_providers.ollama.base_url={baseUrl.TrimEnd('/')}/v1",
        "-c", "model_providers.ollama.wire_api=chat",
    ];
}
