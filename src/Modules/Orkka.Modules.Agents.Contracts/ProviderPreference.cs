namespace Orkka.Modules.Agents.Contracts;

/// <summary>
/// The order a runner-backed host auto-picks a provider when the configured one can't run.
///
/// One constant because three readiness implementations (the Local edition's, the hosted
/// Worker's per-link one, and the utility link's) used to each carry a copy with a
/// "keep them in step" comment — and nothing pinned the agreement. The rationale:
/// unmetered subscription CLIs first; Copilot late because its usage is metered (premium
/// requests on the user's Copilot plan); OpenCode second-to-last because it is a harness,
/// so auto-picking it silently picks whichever vendor its default points at; Ollama last
/// because local models are the weakest auto-pick.
/// </summary>
public static class ProviderPreference
{
    public static readonly AiProvider[] Order =
    [
        AiProvider.Anthropic, AiProvider.OpenAI, AiProvider.Google,
        AiProvider.XAI, AiProvider.GitHubCopilot, AiProvider.OpenCode, AiProvider.Ollama,
    ];
}

/// <summary>User-facing vendor names, shared by run blockers, routing explanations and
/// creation-time rejections so the same provider is never "Codex" in one sentence and
/// "OpenAI" in the next.</summary>
public static class ProviderDisplayNames
{
    public static string Of(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => "OpenAI Codex",
        AiProvider.Google => "Gemini",
        AiProvider.GitHubCopilot => "GitHub Copilot",
        AiProvider.Ollama => "Ollama",
        AiProvider.XAI => "Grok",
        AiProvider.OpenCode => "OpenCode",
        AiProvider.PixelLab => "PixelLab",
        _ => "Claude",
    };
}
