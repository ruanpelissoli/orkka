namespace Orkka.Modules.Execution.Agents;

using Orkka.Modules.Agents.Contracts;

/// <summary>
/// Routes each agent run to the invoker matching its provider. Shared by both editions:
/// the hosted Worker (Claude via the Agent SDK, GPT/Gemini via their CLIs with Orkka's
/// API keys) and the local runner (all three via the user's subscription credentials).
/// Unknown providers fall back to Claude, matching provider-resolution fallback everywhere else.
/// </summary>
public sealed class ProviderDispatchingAgentInvoker(
    ClaudeAgentInvoker claude,
    CodexAgentInvoker codex,
    GeminiAgentInvoker gemini,
    CopilotAgentInvoker copilot,
    OllamaAgentInvoker ollama,
    GrokAgentInvoker grok,
    OpenCodeAgentInvoker openCode) : IAiAgentInvoker
{
    public IAsyncEnumerable<AgentChunk> InvokeAsync(
        AgentRequest request, CancellationToken ct = default)
        => (request.Provider switch
        {
            AiProvider.OpenAI => (IAiAgentInvoker)codex,
            AiProvider.Google => gemini,
            AiProvider.GitHubCopilot => copilot,
            AiProvider.Ollama => ollama,
            AiProvider.XAI => grok,
            AiProvider.OpenCode => openCode,
            _ => claude,
        }).InvokeAsync(request, ct);

    // In-process by construction (it wraps in-process invokers). Only the Claude invoker
    // honours a tool server, so callers gate on the provider as well.
    public bool SupportsInProcessToolServer => true;
}
