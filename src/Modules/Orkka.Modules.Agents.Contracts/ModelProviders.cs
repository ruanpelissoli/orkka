namespace Orkka.Modules.Agents.Contracts;

/// <summary>
/// Derives the AI vendor from a model id prefix. Single source of truth for provider
/// routing — the Agents module's model catalog, the Execution orchestrator, and the
/// local runner all resolve providers through this. Unknown or empty ids fall back to
/// Anthropic, which matches the platform's historical Anthropic-only behavior.
/// </summary>
public static class ModelProviders
{
    public static AiProvider FromModelId(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return AiProvider.Anthropic;

        var id = modelId.Trim();

        // Checked before everything else: OpenCode ids EMBED another vendor's id
        // ("opencode/anthropic/claude-sonnet-4-6"), so any later prefix test would match the
        // inner vendor and route the run to the wrong CLI. The harness owns the id, not the
        // vendor named inside it.
        if (id.StartsWith("opencode", StringComparison.OrdinalIgnoreCase))
            return AiProvider.OpenCode;

        // Checked first: Copilot serves models whose bare ids ("gpt-5", "claude-sonnet-4.5")
        // would otherwise match the vendor prefixes below, so catalog ids are namespaced
        // "copilot/<model>" and the invoker strips the prefix before the CLI sees it.
        if (id.StartsWith("copilot", StringComparison.OrdinalIgnoreCase))
            return AiProvider.GitHubCopilot;

        // Same namespacing rule as Copilot: Ollama serves models whose bare ids ("gpt-oss",
        // "qwen3-coder") would match the vendor prefixes below, so catalog ids are
        // "ollama/<model:tag>" and the invoker strips the prefix before the CLI sees it.
        if (id.StartsWith("ollama", StringComparison.OrdinalIgnoreCase))
            return AiProvider.Ollama;

        // Retired image-only vendor, matched before the generic prefixes so legacy image
        // catalog rows ("pixellab-pixflux") still derive their provider instead of defaulting
        // to Anthropic. No chat model id starts with "pixellab".
        if (id.StartsWith("pixellab", StringComparison.OrdinalIgnoreCase))
            return AiProvider.PixelLab;

        if (id.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
            return AiProvider.Google;

        // xAI ships bare ids ("grok-4.6"), so no namespace is needed — unlike Copilot and
        // Ollama, nothing else in the catalog starts with "grok".
        if (id.StartsWith("grok", StringComparison.OrdinalIgnoreCase))
            return AiProvider.XAI;

        if (id.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("codex", StringComparison.OrdinalIgnoreCase)
            || (id.Length > 1 && (id[0] == 'o' || id[0] == 'O') && char.IsAsciiDigit(id[1])))
            return AiProvider.OpenAI;

        return AiProvider.Anthropic;
    }
}
