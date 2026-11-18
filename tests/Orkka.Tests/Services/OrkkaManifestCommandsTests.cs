namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.Modules.Projects.Contracts;

/// <summary>
/// orkka.json's setup / check / build are the repository's own commands — the fallback when the
/// repo's settings leave them empty, and the source the runner trusts without asking.
/// </summary>
public sealed class OrkkaManifestCommandsTests
{
    [Fact]
    public void DeclaredCommandsAreRead_CommentsAndTrailingCommasAllowed()
    {
        var commands = OrkkaManifest.TryReadCommands("""
            {
              // the team's own commands
              "stack": "node",
              "setup": "npm ci",
              "check": "  npm run lint && npm test  ",
              "build": "npm run build",
            }
            """);

        commands.Should().Be(new RepoCommands("npm ci", "npm run lint && npm test", "npm run build"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ broken")]
    [InlineData("[]")]
    [InlineData("""{ "setup": 3, "check": "", "build": "   " }""")]
    public void NothingUsable_DeclaresNothing(string? json)
    {
        OrkkaManifest.TryReadCommands(json).Should().Be(RepoCommands.None);
    }
}
