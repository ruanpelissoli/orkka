namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// AgentLanguageTests pins the helper; these pin the SEAM — that the locale the endpoint
/// captured actually reaches the prompt the CLI is spawned with. The helper being correct
/// was never the question when replies came back in English: the ways this breaks silently
/// are a locale that never leaves the request, and a Response Language section that the
/// command-line budget quietly truncates away.
/// </summary>
public sealed class ChatPromptLanguageTests
{
    private static ProjectAgent MakeAgent(string? persona = null) => new()
    {
        OrgId = Ulid.NewUlid(),
        ProjectId = Ulid.NewUlid(),
        Name = "Luna",
        Role = AgentRole.PM,
        SystemPrompt = persona ?? "You are a product manager.",
    };

    [Theory]
    [InlineData("pt-BR", "Portuguese (Brazilian)")]
    [InlineData("es", "Spanish")]
    [InlineData("fr-FR,fr;q=0.9", "French")]
    public void SystemPrompt_CarriesTheResponseLanguageSection(string locale, string expected)
    {
        var prompt = SendMessageEndpoint.BuildSystemPrompt(
            MakeAgent(), memories: [], history: [], repos: [],
            contextSummary: null, locale: locale,
            designAssetContext: null);

        prompt.Should().Contain("## Response Language");
        prompt.Should().Contain(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("en-US")]
    public void SystemPrompt_SaysNothingWhenEnglishIsTheInterfaceLanguage(string? locale)
    {
        var prompt = SendMessageEndpoint.BuildSystemPrompt(
            MakeAgent(), memories: [], history: [], repos: [],
            contextSummary: null, locale: locale,
            designAssetContext: null);

        prompt.Should().NotContain("## Response Language");
    }

    /// <summary>
    /// The section is appended BEFORE the rolling summary and the conversation history, and
    /// the whole prompt is hard-capped to survive the Windows command-line limit. A long
    /// conversation must not be able to push the language directive past that cap — that
    /// failure mode looks exactly like "the agent answers in English on older chats".
    /// </summary>
    [Fact]
    public void SystemPrompt_KeepsTheLanguageSectionEvenWhenTheTurnIsCapped()
    {
        var history = Enumerable.Range(0, 400)
            .Select(i => (dynamic)new ChatPromptGuardrailTests.HistoryMessage(
                i % 2 == 0 ? "user" : "assistant", new string('x', 900)))
            .ToList();

        var prompt = SendMessageEndpoint.BuildSystemPrompt(
            MakeAgent(DefaultPrompts.PM), memories: [], history: history, repos: [],
            contextSummary: new string('s', 4_000), locale: "pt-BR",
            designAssetContext: null,
            projectName: "Orkka", toolsEnabled: true);

        prompt.Should().Contain("## Response Language");
        prompt.Should().Contain("Portuguese (Brazilian)");
    }

    /// <summary>
    /// The end-of-input restatement is what holds the language on resumed sessions, where
    /// the CLI replays the ORIGINAL system prompt and a freshly built one never lands.
    /// </summary>
    [Fact]
    public void Reminder_IsWhatSurvivesASessionResume()
    {
        AgentLanguage.Reminder("pt-BR").Should().Contain("Portuguese (Brazilian)");
        AgentLanguage.Reminder(null).Should().BeEmpty();
    }
}
