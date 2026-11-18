namespace Orkka.Tests.Utilities;

using FluentAssertions;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// The response-language directive lands mid-system-prompt, ahead of the rolling summary and
/// the conversation history, so it loses salience as a conversation grows — which is how
/// replies drift back to English on SOME turns while earlier ones were fine. The reminder is
/// the end-of-input restatement that fixes that; these tests pin both halves.
/// </summary>
public sealed class AgentLanguageTests
{
    [Theory]
    [InlineData("pt-BR", "Portuguese (Brazilian)")]
    [InlineData("PT", "Portuguese (Brazilian)")]
    [InlineData("es", "Spanish")]
    [InlineData("fr-FR,fr;q=0.9", "French")]
    public void DisplayName_ResolvesSupportedLocales(string locale, string expected)
        => AgentLanguage.DisplayName(locale).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("en")]
    [InlineData("en-US")]
    [InlineData("ja")]
    public void DisplayName_IsNullWhenNothingNeedsSaying(string? locale)
        => AgentLanguage.DisplayName(locale).Should().BeNull();

    [Fact]
    public void Instruction_TiesTheLanguageToTheProjectSettingNotTheMessage()
    {
        var instruction = AgentLanguage.Instruction("pt-BR");

        instruction.Should().Contain("Portuguese (Brazilian)");
        // The drift this guards against: the user types one English message and the agent
        // mirrors it for the rest of the conversation.
        instruction.Should().Contain("NOT by the language of the");
        instruction.Should().Contain("project's language setting");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("en")]
    [InlineData("ja")]
    public void Reminder_IsEmptyWhenNoLanguageWasSelected(string? locale)
        => AgentLanguage.Reminder(locale).Should().BeEmpty();

    [Fact]
    public void Reminder_NamesTheLanguageAndKeepsCodeInEnglish()
    {
        var reminder = AgentLanguage.Reminder("es");

        reminder.Should().Contain("Spanish");
        reminder.Should().Contain("not part of the user's message");
        reminder.Should().Contain("stay in English");
    }
}
