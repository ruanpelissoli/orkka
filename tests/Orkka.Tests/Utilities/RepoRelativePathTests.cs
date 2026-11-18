namespace Orkka.Tests.Utilities;

using FluentAssertions;
using Orkka.Git;

public sealed class RepoRelativePathTests
{
    [Theory]
    [InlineData("src/index.ts")]
    [InlineData("prisma/schema.prisma")]
    [InlineData("a/b/c/file.cs")]
    [InlineData("README.md")]
    public void IsSafeRelative_AcceptsRepoRelativePaths(string path)
    {
        RepoRelativePath.IsSafeRelative(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("../../.env")]
    [InlineData("../secrets.json")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/system32/config")]
    [InlineData("~/secret")]
    [InlineData("//server/share/x")]
    [InlineData("src/../../escape")]
    [InlineData("..\\..\\windows\\path")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsSafeRelative_RejectsTraversalAndRootedPaths(string? path)
    {
        RepoRelativePath.IsSafeRelative(path).Should().BeFalse();
    }

    [Fact]
    public void TryResolveWithin_ResolvesSafePathInsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "orkka-root-test");

        var ok = RepoRelativePath.TryResolveWithin(root, "src/app.ts", out var resolved);

        ok.Should().BeTrue();
        resolved.Should().NotBeNull();
        resolved!.Should().StartWith(Path.GetFullPath(root));
    }

    [Fact]
    public void TryResolveWithin_RejectsEscapingPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "orkka-root-test");

        var ok = RepoRelativePath.TryResolveWithin(root, "../../etc/passwd", out var resolved);

        ok.Should().BeFalse();
        resolved.Should().BeNull();
    }
}
