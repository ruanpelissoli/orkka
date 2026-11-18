namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Orkka.Modules.Agents.Contracts;

public class ModelProvidersTests
{
    [Theory]
    [InlineData("claude-sonnet-4-6")]
    [InlineData("claude-fable-5")]
    [InlineData("CLAUDE-OPUS-4-8")]
    public void Claude_models_resolve_to_anthropic(string modelId) =>
        ModelProviders.FromModelId(modelId).Should().Be(AiProvider.Anthropic);

    [Theory]
    [InlineData("gemini-3-pro")]
    [InlineData("gemini-3-flash")]
    [InlineData("GEMINI-3-PRO")]
    public void Gemini_models_resolve_to_google(string modelId) =>
        ModelProviders.FromModelId(modelId).Should().Be(AiProvider.Google);

    [Theory]
    [InlineData("gpt-5.6-sol")]
    [InlineData("gpt-5.5")]
    [InlineData("gpt-5.5-codex")]
    [InlineData("codex-mini")]
    [InlineData("o3")]
    [InlineData("o4-mini")]
    public void OpenAi_models_resolve_to_openai(string modelId) =>
        ModelProviders.FromModelId(modelId).Should().Be(AiProvider.OpenAI);

    // The copilot/ namespace must win over the vendor prefixes the bare ids would match:
    // "copilot/gpt-5" is served by the Copilot CLI, not the Codex CLI.
    [Theory]
    [InlineData("copilot/auto")]
    [InlineData("copilot/gpt-5-mini")]
    [InlineData("copilot/gpt-5.3-codex")]
    [InlineData("copilot/gpt-5.4")]
    [InlineData("copilot/gpt-5.4-mini")]
    [InlineData("copilot/gpt-5.5")]
    [InlineData("copilot/gpt-5.6-luna")]
    [InlineData("copilot/gpt-5.6-sol")]
    [InlineData("copilot/gpt-5.6-terra")]
    [InlineData("copilot/claude-haiku-4.5")]
    [InlineData("copilot/claude-opus-4.8")]
    [InlineData("copilot/claude-opus-5")]
    [InlineData("copilot/claude-sonnet-4.6")]
    [InlineData("copilot/claude-sonnet-5")]
    [InlineData("copilot/gemini-3.6-flash")] // would hit the gemini prefix without the namespace
    [InlineData("copilot/gemini-3.7-flash")]
    [InlineData("copilot/gpt-5")]            // retired, still routes
    [InlineData("copilot/claude-sonnet-4.5")] // retired, still routes
    [InlineData("COPILOT/GPT-5.6-SOL")]
    public void Copilot_namespaced_models_resolve_to_github_copilot(string modelId) =>
        ModelProviders.FromModelId(modelId).Should().Be(AiProvider.GitHubCopilot);

    [Fact]
    public void Every_catalog_copilot_row_resolves_to_github_copilot()
    {
        // The catalog is the list that matters: a new Copilot row whose id lost the
        // namespace would silently route to the vendor CLI the bare id looks like.
        var copilotRows = ModelCatalog.Models.Where(m => m.Provider == AiProvider.GitHubCopilot).ToList();

        copilotRows.Should().HaveCount(16);
        copilotRows.Should().AllSatisfy(m =>
        {
            m.Id.Should().StartWith("copilot/");
            ModelProviders.FromModelId(m.Id).Should().Be(AiProvider.GitHubCopilot, m.Id);
        });
    }

    // Same namespacing rationale as copilot: "ollama/gpt-oss" and "ollama/qwen3-coder"
    // carry bare ids that would otherwise hit the vendor prefix rules.
    [Theory]
    [InlineData("ollama/gpt-oss:20b")]
    [InlineData("ollama/qwen3-coder")]
    [InlineData("OLLAMA/LLAMA3.1:8B")]
    [InlineData("ollama")]
    public void Ollama_namespaced_models_resolve_to_ollama(string modelId) =>
        ModelProviders.FromModelId(modelId).Should().Be(AiProvider.Ollama);

    // Bare ids, no namespace: xAI is a conventional vendor and nothing else in the catalog
    // starts with "grok".
    [Theory]
    [InlineData("grok-4.6")]
    [InlineData("GROK-4.6")]
    [InlineData("grok")]
    public void Grok_models_resolve_to_xai(string modelId) =>
        ModelProviders.FromModelId(modelId).Should().Be(AiProvider.XAI);

    // The critical case for the harness: an OpenCode id EMBEDS a vendor id, so the opencode
    // prefix has to be tested before every vendor prefix. "opencode/anthropic/…" routing to
    // Anthropic would spawn the claude CLI for a run that must go through opencode.
    [Theory]
    [InlineData("opencode/anthropic/claude-sonnet-4-6")]
    [InlineData("opencode/openai/gpt-5.6-sol")]
    [InlineData("opencode/xai/grok-4.6")]
    [InlineData("opencode/google/gemini-3-pro-preview")]
    [InlineData("opencode/ollama/qwen3-coder")]
    [InlineData("OPENCODE/ANTHROPIC/CLAUDE-OPUS-5")]
    [InlineData("opencode")]
    public void OpenCode_namespaced_models_resolve_to_opencode(string modelId) =>
        ModelProviders.FromModelId(modelId).Should().Be(AiProvider.OpenCode);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("llama-3-70b")]
    [InlineData("opus")] // 'o' followed by non-digit must not match the OpenAI o-series rule
    public void Unknown_or_empty_ids_fall_back_to_anthropic(string? modelId) =>
        ModelProviders.FromModelId(modelId).Should().Be(AiProvider.Anthropic);

    [Fact]
    public void Ids_are_trimmed_before_matching() =>
        ModelProviders.FromModelId("  gemini-3-flash  ").Should().Be(AiProvider.Google);
}
