namespace Orkka.Tests.Features.Organizations;

using FluentAssertions;
using Orkka.Modules.Organizations.Features.Members;

/// <summary>
/// Preferences are a JSON blob, so every field is a compatibility question: rows written
/// before the language preference existed must keep parsing, and adding it must not disturb
/// the flags already in there.
/// </summary>
public sealed class MemberPreferencesLocaleTests
{
    [Fact]
    public void LegacyBlobWithoutALanguage_ParsesAsUnset()
    {
        var parsed = MemberPreferences.Parse(
            """{"showTechnicalDetails":true,"onboardingTourSeen":true}""");

        parsed.Locale.Should().BeNull();
        parsed.ShowTechnicalDetails.Should().BeTrue();
        parsed.OnboardingTourSeen.Should().BeTrue();
    }

    [Fact]
    public void Language_RoundTripsThroughTheBlobWithoutTouchingTheOtherFlags()
    {
        var stored = new MemberPreferences(ShowTechnicalDetails: true, Locale: "pt").ToJson();

        var parsed = MemberPreferences.Parse(stored);

        parsed.Locale.Should().Be("pt");
        parsed.ShowTechnicalDetails.Should().BeTrue();
        parsed.OnboardingTourSeen.Should().BeFalse();
    }

    [Fact]
    public void MalformedBlob_FallsBackToDefaultsRatherThanThrowing()
        => MemberPreferences.Parse("not json").Locale.Should().BeNull();
}
