namespace Orkka.Modules.Agents.Contracts;

/// <summary>
/// Designated model per provider per tier, used wherever the system (not the user) picks a
/// model: utility fallback, merge resolution, agent seeding. Ids must exist in
/// <see cref="ModelCatalog"/> — a unit test enforces the pairing.
/// </summary>
public static class ProviderModelDefaults
{
    /// <summary>Cheapest/fastest tier: titles, rolling summaries, dependency analysis.</summary>
    public static string FastModel(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => "gpt-5.6-sol",
        AiProvider.Google => "gemini-3.6-flash",
        AiProvider.GitHubCopilot => "copilot/auto",
        AiProvider.Ollama => "ollama/gpt-oss:20b",
        AiProvider.XAI => "grok-4.6",
        AiProvider.OpenCode => OpenCodeDefault,
        _ => "claude-haiku-4-5",
    };

    /// <summary>Standard utility tier: task decomposition and other structured one-shots.</summary>
    public static string UtilityModel(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => "gpt-5.6-sol",
        AiProvider.Google => "gemini-3.1-pro-preview",
        AiProvider.GitHubCopilot => "copilot/auto",
        AiProvider.Ollama => "ollama/gpt-oss:20b",
        AiProvider.XAI => "grok-4.6",
        AiProvider.OpenCode => OpenCodeDefault,
        _ => "claude-sonnet-5",
    };

    /// <summary>Capable tier: merge-conflict resolution, dev/reviewer agent seeds. On Anthropic
    /// this is Sonnet 5, not Opus: Opus 5 was the seed from 2026-08-21 and a single bootstrap
    /// task on it cost $8 in the cloud (ESF-1, 2026-09-04) — most of it cache reads, which
    /// scale with turns regardless of model quality. Sonnet 5 is near-Opus on coding at well
    /// under half the price; users who want Opus pick it per agent on the Team page or per
    /// task. Cost decided this, and it is the user's rule ("Opus on cloud is too expensive").</summary>
    // Ollama gets gpt-oss:20b on every tier: the runner only knows which models are
    // actually pulled at Hello time, and 20b is the model most Ollama coding setups have
    // (it is the codex --oss default). A missing model fails with an actionable
    // "ollama pull" message rather than silently picking something wrong.
    public static string CapableModel(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => "gpt-5.6-sol",
        AiProvider.Google => "gemini-3.1-pro-preview",
        AiProvider.GitHubCopilot => "copilot/auto",
        AiProvider.Ollama => "ollama/gpt-oss:20b",
        AiProvider.XAI => "grok-4.6",
        AiProvider.OpenCode => OpenCodeDefault,
        _ => "claude-sonnet-5",
    };

    // Copilot gets "copilot/auto" on every tier (user decision 2026-11-18): Auto lets Copilot
    // pick the model and bills DISCOUNTED premium requests on the user's Copilot plan. Nothing
    // is lost against a pinned Copilot model: every copilot/* row is SupportsMcp=false alike
    // (the QA role stays on the provider-independent QaModel either way), and the Copilot CLI
    // invocation is identical apart from the --model value. Users who want a specific Copilot
    // model pick it per agent, or for the whole team at once, on the Team page.

    /// <summary>Every OpenCode tier resolves to the same anchor, for the same reason Ollama
    /// does: OpenCode ships no models, and which vendors are actually reachable depends on
    /// what the user ran `opencode auth login` for — only the runner knows, and only at Hello
    /// time. Picking a cheaper id per tier would just be guessing at a login that may not
    /// exist. A model the user is not authenticated for fails with the CLI's own actionable
    /// auth error rather than silently running something else.</summary>
    private const string OpenCodeDefault = "opencode/anthropic/claude-sonnet-4-6";

    /// <summary>QA tier — deliberately provider-independent. The QA phase drives Playwright
    /// MCP through the Claude Agent SDK, so a QA agent stays on a Claude model even when the
    /// rest of the pod is switched to another vendor.</summary>
    public const string QaModel = "claude-sonnet-5";

    /// <summary>Tier lookup by enum, for callers that resolve a tier dynamically.</summary>
    public static string ForTier(AiProvider provider, AiModelTier tier) => tier switch
    {
        AiModelTier.Fast => FastModel(provider),
        AiModelTier.Utility => UtilityModel(provider),
        AiModelTier.Capable => CapableModel(provider),
        AiModelTier.Qa => QaModel,
        _ => CapableModel(provider),
    };
}
