namespace Orkka.Tests.Utilities;

using FluentAssertions;
using Orkka.Modules.Projects.Contracts;

/// <summary>
/// The project's language setting is what agents write in; the requester's UI locale is only
/// the legacy answer for projects that predate the setting. These tests pin that precedence —
/// the bug they guard against is the navbar language quietly steering agent output again.
/// </summary>
public sealed class ProjectLanguageTests
{
    private static ProjectSettingsDto Settings(string? language) =>
        new ProjectSettingsDto(MaxParallelTasks: 1, SameRepoParallelism: false) with { Language = language };

    [Fact]
    public void ProjectSettingWinsOverRequesterLocale()
        => ProjectLanguage.Resolve(Settings("pt"), "de").Should().Be("pt");

    [Fact]
    public void ExplicitEnglishPinsEnglishEvenForNonEnglishRequesters()
        // "en" resolves to "en", and AgentLanguage.Instruction("en") is "" — the project
        // stays English no matter what navbar language the requester picked.
        => ProjectLanguage.Resolve(Settings("en"), "pt").Should().Be("en");

    [Theory]
    [InlineData(null)]      // project predates the setting
    [InlineData("")]        // defensive: empty stored value
    [InlineData("klingon")] // defensive: unsupported stored value
    public void FallsBackToRequesterLocaleWhenTheProjectHasNoUsableLanguage(string? stored)
        => ProjectLanguage.Resolve(Settings(stored), "es").Should().Be("es");

    [Fact]
    public void NullSettingsFallBackToRequesterLocale()
        => ProjectLanguage.Resolve((ProjectSettingsDto?)null, "fr").Should().Be("fr");

    [Fact]
    public void StoredRegionalVariantIsNormalized()
        => ProjectLanguage.Resolve(Settings("pt-BR"), null).Should().Be("pt");

    [Fact]
    public void SummaryOverloadReadsTheSummarySettings()
    {
        var project = new ProjectSummary(
            Ulid.NewUlid(), Ulid.NewUlid(), "Tapago", "tapago", "TAP", Settings("de"));

        ProjectLanguage.Resolve(project, "en").Should().Be("de");
        ProjectLanguage.Resolve((ProjectSummary?)null, "en").Should().Be("en");
    }
}
