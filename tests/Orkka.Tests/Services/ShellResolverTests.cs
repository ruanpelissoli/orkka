namespace Orkka.Tests.Services;

using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Orkka.LocalRunner.Contracts;
using Orkka.LocalRunner.Terminal;

public sealed class RunnerTokensUserRepoTests
{
    [Fact]
    public void UserRepo_RoundTrips()
    {
        var token = RunnerTokens.UserRepo("org-1", "repo-2");

        RunnerTokens.TryParseUserRepo(token, out var orgId, out var repoId).Should().BeTrue();
        orgId.Should().Be("org-1");
        repoId.Should().Be("repo-2");
    }

    [Theory]
    [InlineData("runner-worktree:org:repo:task")] // different token kind
    [InlineData("runner-userrepo:only-one-part")]
    [InlineData("runner-userrepo:org:repo:extra")]
    [InlineData("runner-userrepo::repo")] // empty org
    [InlineData("runner-userrepo:org:")] // empty repo
    [InlineData("garbage")]
    public void TryParseUserRepo_RejectsMalformedTokens(string token)
    {
        RunnerTokens.TryParseUserRepo(token, out _, out _).Should().BeFalse();
    }
}

public sealed class ShellResolverTests
{
    [Fact]
    public void Resolve_Default_ReturnsAnExistingShell()
    {
        var shell = ShellResolver.Resolve(null);

        File.Exists(shell.Path).Should().BeTrue("the resolved default shell must exist on this machine");
        shell.Name.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Resolve_UnknownName_FallsBackToDefault()
    {
        // Anything outside the allowlist (including path-shaped injections) is ignored.
        var requested = ShellResolver.Resolve("C:\\evil\\payload.exe");
        var fallback = ShellResolver.Resolve(null);

        requested.Path.Should().Be(fallback.Path);
    }

    [Fact]
    public void Resolve_AllowlistedName_ResolvesWhenPresent()
    {
        if (OperatingSystem.IsWindows())
        {
            var shell = ShellResolver.Resolve("cmd");
            shell.Name.Should().Be("cmd");
            File.Exists(shell.Path).Should().BeTrue();
        }
        else
        {
            var shell = ShellResolver.Resolve("sh");
            shell.Name.Should().Be("sh");
            File.Exists(shell.Path).Should().BeTrue();
        }
    }
}
