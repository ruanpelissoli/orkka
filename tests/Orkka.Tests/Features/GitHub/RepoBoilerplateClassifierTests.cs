namespace Orkka.Tests.Features.GitHub;

using FluentAssertions;
using Orkka.Modules.SourceControl.Infrastructure.Services;

/// <summary>
/// The boilerplate classifier decides whether a briefed repo counts as "already has code" —
/// which gates the design briefing. Too strict and a fresh .gitignore-only repo hides the
/// briefing (the reported bug); too loose and a real codebase gets offered a redesign.
/// </summary>
public sealed class RepoBoilerplateClassifierTests
{
    [Theory]
    [InlineData(".gitignore")]
    [InlineData(".gitattributes")]
    [InlineData(".editorconfig")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".claude/CLAUDE.md")]
    [InlineData("README.md")]
    [InlineData("docs/setup.md")]
    [InlineData("notes.txt")]
    [InlineData("LICENSE")]
    [InlineData("LICENSE.txt")]
    [InlineData("NOTICE")]
    [InlineData("CODEOWNERS")]
    public void ScaffoldingFiles_AreBoilerplate(string path)
    {
        RepoContextBuilder.IsBoilerplatePath(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("package.json")]
    [InlineData("src/index.ts")]
    [InlineData("Program.cs")]
    [InlineData("app/globals.css")]
    [InlineData("Dockerfile")]
    [InlineData("go.mod")]
    public void RealProjectFiles_AreNot(string path)
    {
        RepoContextBuilder.IsBoilerplatePath(path).Should().BeFalse();
    }
}
