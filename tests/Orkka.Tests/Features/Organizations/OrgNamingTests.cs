namespace Orkka.Tests.Features.Organizations;

using FluentAssertions;
using Orkka.Modules.Organizations.Features.Settings;

public sealed class OrgNamingTests
{
    [Theory]
    [InlineData("Acme", "Acme")]
    [InlineData("  Acme  ", "Acme")]
    [InlineData("Acme   Corp", "Acme Corp")]
    [InlineData("Acme\tCorp", "Acme Corp")]
    public void Normalize_TrimsAndCollapsesWhitespace(string input, string expected)
        => OrgNaming.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_RejectsEmpty(string? input)
        => OrgNaming.Normalize(input).Should().BeNull();

    [Fact]
    public void Normalize_RejectsNamesLongerThanTheColumn()
    {
        OrgNaming.Normalize(new string('a', OrgNaming.MaxLength)).Should().NotBeNull();
        OrgNaming.Normalize(new string('a', OrgNaming.MaxLength + 1)).Should().BeNull();
    }
}
