namespace Orkka.Modules.Agents.Contracts;

/// <summary>
/// The models users can pick — for an agent's default (Team page) or for a single task
/// (task create form). SupportsMcp gates the QA role: driving Playwright MCP browser tools
/// needs a model with reliable tool calling. Unknown model ids fail closed (treated as no
/// MCP support). Lives in Contracts because the Tasks module validates per-task model
/// overrides against it.
/// </summary>
public static class ModelCatalog
{
    public sealed record ModelInfo(
        string Id, string Name, AiProvider Provider, string Description, bool SupportsMcp);

    public static readonly IReadOnlyList<ModelInfo> Models =
    [
        new("claude-haiku-4-5", "Claude Haiku 4.5", AiProvider.Anthropic,
            "Fastest and most cost-effective model for quick tasks", SupportsMcp: true),
        new("claude-sonnet-4-6", "Claude Sonnet 4.6", AiProvider.Anthropic,
            "Fast, intelligent model for everyday development tasks", SupportsMcp: true),
        new("claude-sonnet-5", "Claude Sonnet 5", AiProvider.Anthropic,
            "Latest Sonnet — near-Opus quality on coding and agentic work", SupportsMcp: true),
        new("claude-opus-4-6", "Claude Opus 4.6", AiProvider.Anthropic,
            "Capable model for complex reasoning and coding", SupportsMcp: true),
        new("claude-opus-4-8", "Claude Opus 4.8", AiProvider.Anthropic,
            "Long-horizon agentic work and knowledge tasks", SupportsMcp: true),
        new("claude-opus-5", "Claude Opus 5", AiProvider.Anthropic,
            "Latest Opus — deep reasoning and complex agentic coding", SupportsMcp: true),
        new("claude-fable-5", "Claude Fable 5", AiProvider.Anthropic,
            "Anthropic's most capable model for the hardest reasoning and agentic work", SupportsMcp: true),
        // Non-Anthropic models run through vendor CLIs on the local runner (subscription
        // auth). SupportsMcp stays false: the QA phase drives Playwright MCP through the
        // Claude Agent SDK, so QA agents must remain on Claude models.
        // Google ids must be valid for generateContent on the Gemini API-key backend —
        // the only auth path since Google closed the free Code Assist OAuth tier
        // (2026-06-18) — and must not be retired there. Both conditions have bitten:
        // "gemini-3-pro"/"gemini-3-flash" were Code Assist aliases that 404 on the API-key
        // backend, and gemini-3-pro-preview was SHUT DOWN on 2026-03-09 (verified
        // 2026-08-15 at ai.google.dev/gemini-api/docs/deprecations) with
        // gemini-3-flash-preview deprecated behind it. The ids below are Google's own
        // named replacements; GeminiAgentInvoker maps all four dead ids onto them so
        // agents saved earlier keep running, and CostCalculator keeps pricing them.
        new("gemini-3.1-pro-preview", "Gemini 3.1 Pro", AiProvider.Google,
            "Google's most capable model, via the Gemini CLI", SupportsMcp: false),
        new("gemini-3.6-flash", "Gemini 3.6 Flash", AiProvider.Google,
            "Fast Google model for everyday tasks, via the Gemini CLI", SupportsMcp: false),
        // gpt-5.6-sol supersedes both gpt-5.5 and gpt-5.5-codex: Sol is OpenAI's frontier
        // model AND its strongest agentic-coding model, so the split no longer buys
        // anything. The retired ids stay priced (ModelCatalogSeed.RetiredDisplayNames) for
        // agents saved before this change.
        new("gpt-5.6-sol", "GPT-5.6 Sol", AiProvider.OpenAI,
            "OpenAI's frontier model for coding and agentic work, via the Codex CLI subscription", SupportsMcp: false),
        // Copilot models are namespaced "copilot/<model>" because their bare ids collide
        // with the vendor prefixes in ModelProviders.FromModelId. Usage bills the user's
        // Copilot premium-request quota (never Orkka — CostCalculator.IsSubscriptionOnly
        // prices the whole namespace at 0/0); "auto" lets Copilot pick and is discounted.
        //
        // Source: docs.github.com/copilot/reference/ai-models/supported-models (2026-09). Ids
        // follow Copilot's dotted convention ("claude-sonnet-4.6", not Anthropic's
        // "claude-sonnet-4-6") and MUST be verified against the CLI's `/model` picker — the
        // docs list what the Copilot product offers, and the CLI can lag or rename. The
        // previous rows ("copilot/claude-sonnet-4.5", "copilot/gpt-5") were retired by the
        // vendor and live on in ModelCatalogSeed.RetiredDisplayNames so agents saved on them
        // still resolve.
        new("copilot/auto", "Copilot Auto", AiProvider.GitHubCopilot,
            "Copilot picks the model (discounted premium requests on your Copilot plan)", SupportsMcp: false),
        new("copilot/gpt-5-mini", "Copilot GPT-5 mini", AiProvider.GitHubCopilot,
            "GPT-5 mini through GitHub Copilot (premium requests on your Copilot plan)", SupportsMcp: false),
        new("copilot/gpt-5.3-codex", "Copilot GPT-5.3 Codex", AiProvider.GitHubCopilot,
            "GPT-5.3 Codex through GitHub Copilot (premium requests on your Copilot plan)", SupportsMcp: false),
        new("copilot/gpt-5.4", "Copilot GPT-5.4", AiProvider.GitHubCopilot,
            "GPT-5.4 through GitHub Copilot (premium requests on your Copilot plan)", SupportsMcp: false),
        new("copilot/gpt-5.4-mini", "Copilot GPT-5.4 mini", AiProvider.GitHubCopilot,
            "GPT-5.4 mini through GitHub Copilot (premium requests on your Copilot plan)", SupportsMcp: false),
        new("copilot/gpt-5.5", "Copilot GPT-5.5", AiProvider.GitHubCopilot,
            "GPT-5.5 through GitHub Copilot (premium requests on your Copilot plan)", SupportsMcp: false),
        new("copilot/gpt-5.6-luna", "Copilot GPT-5.6 Luna", AiProvider.GitHubCopilot,
            "GPT-5.6 Luna through GitHub Copilot (premium requests on your Copilot plan)", SupportsMcp: false),
        new("copilot/gpt-5.6-sol", "Copilot GPT-5.6 Sol", AiProvider.GitHubCopilot,
            "GPT-5.6 Sol through GitHub Copilot (premium requests on your Copilot plan)", SupportsMcp: false),
        new("copilot/gpt-5.6-terra", "Copilot GPT-5.6 Terra", AiProvider.GitHubCopilot,
            "GPT-5.6 Terra through GitHub Copilot (premium requests on your Copilot plan)", SupportsMcp: false),
        new("copilot/claude-haiku-4.5", "Copilot Claude Haiku 4.5", AiProvider.GitHubCopilot,
            "Claude Haiku 4.5 through GitHub Copilot (premium requests on your Copilot plan)", SupportsMcp: false),
        new("copilot/claude-opus-4.8", "Copilot Claude Opus 4.8", AiProvider.GitHubCopilot,
            "Claude Opus 4.8 through GitHub Copilot (premium requests on your Copilot plan)", SupportsMcp: false),
        new("copilot/claude-opus-5", "Copilot Claude Opus 5", AiProvider.GitHubCopilot,
            "Claude Opus 5 through GitHub Copilot (premium requests on your Copilot plan)", SupportsMcp: false),
        new("copilot/claude-sonnet-4.6", "Copilot Claude Sonnet 4.6", AiProvider.GitHubCopilot,
            "Claude Sonnet 4.6 through GitHub Copilot (premium requests on your Copilot plan)", SupportsMcp: false),
        new("copilot/claude-sonnet-5", "Copilot Claude Sonnet 5", AiProvider.GitHubCopilot,
            "Claude Sonnet 5 through GitHub Copilot (premium requests on your Copilot plan)", SupportsMcp: false),
        new("copilot/gemini-3.6-flash", "Copilot Gemini 3.6 Flash", AiProvider.GitHubCopilot,
            "Gemini 3.6 Flash through GitHub Copilot (premium requests on your Copilot plan)", SupportsMcp: false),
        new("copilot/gemini-3.7-flash", "Copilot Gemini 3.7 Flash", AiProvider.GitHubCopilot,
            "Gemini 3.7 Flash through GitHub Copilot (premium requests on your Copilot plan)", SupportsMcp: false),
        // Ollama models run entirely on the user's machine (local server at :11434),
        // driven agentically through the free Codex CLI pointed at Ollama's OpenAI-compatible
        // API. These static entries anchor ProviderModelDefaults; the local UI additionally
        // merges in whatever models the runner reports as actually pulled.
        new("ollama/gpt-oss:20b", "GPT-OSS 20B (Ollama)", AiProvider.Ollama,
            "OpenAI's open-weight model running locally via Ollama — free, private, no subscription", SupportsMcp: false),
        new("ollama/gpt-oss:120b", "GPT-OSS 120B (Ollama)", AiProvider.Ollama,
            "Larger open-weight model via Ollama — needs a high-memory machine", SupportsMcp: false),
        new("ollama/qwen3-coder", "Qwen3 Coder (Ollama)", AiProvider.Ollama,
            "Strong open-weight coding model running locally via Ollama", SupportsMcp: false),
        // xAI through the `grok` CLI (Grok Build). Bare ids — nothing else starts with "grok",
        // so no namespace is needed. Auth is a SuperGrok / X Premium Plus subscription or the
        // user's own XAI_API_KEY saved on the Settings page.
        new("grok-4.6", "Grok 4.6", AiProvider.XAI,
            "xAI's frontier coding model, via the Grok Build CLI", SupportsMcp: false),
        // OpenCode brings NO models — these anchors only exist so ProviderModelDefaults has
        // something valid to point at and so the picker is not empty before a runner connects.
        // The real selectable set is whatever `opencode models` reports on the runner (the user
        // must have run `opencode auth login` for that provider), which is why IsSelectable
        // accepts any "opencode/" id. Ids are "opencode/<provider>/<model>" — the harness first,
        // then the vendor it drives.
        new("opencode/anthropic/claude-sonnet-4-6", "Claude Sonnet 4.6 (OpenCode)", AiProvider.OpenCode,
            "Claude Sonnet 4.6 driven by the open-source OpenCode CLI", SupportsMcp: false),
        new("opencode/openai/gpt-5.6-sol", "GPT-5.6 Sol (OpenCode)", AiProvider.OpenCode,
            "OpenAI's frontier model driven by the open-source OpenCode CLI", SupportsMcp: false),
        new("opencode/xai/grok-4.6", "Grok 4.6 (OpenCode)", AiProvider.OpenCode,
            "xAI's Grok 4.6 driven by the open-source OpenCode CLI", SupportsMcp: false)
    ];

    public static bool SupportsMcp(string modelId) =>
        Models.Any(m => m.SupportsMcp && string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Namespaces whose members are discovered on the runner rather than listed here.
    /// Ollama reports whatever tags are pulled; OpenCode reports whatever providers the user
    /// authenticated with `opencode auth login` (~75 via models.dev). Enumerating either in
    /// code would be stale the moment a user pulls a model or logs into a new vendor.</summary>
    private static readonly string[] RunnerDiscoveredNamespaces = ["ollama/", "opencode/"];

    /// <summary>Whether a model id may be pinned by a user. Static catalog ids are accepted
    /// outright; ids under a runner-discovered namespace are accepted on the prefix alone,
    /// because the catalog only anchors a few of them.</summary>
    public static bool IsSelectable(string modelId) =>
        Models.Any(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))
        || RunnerDiscoveredNamespaces.Any(ns => modelId.StartsWith(ns, StringComparison.OrdinalIgnoreCase));
}
