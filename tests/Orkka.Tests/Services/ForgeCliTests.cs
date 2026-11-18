namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.LocalRunner;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// What the runner reads back from the developer's forge CLIs. The CLIs disagree on words —
/// Azure DevOps says "completed" and "abandoned", GitLab "opened" — and the host only ever
/// sees the normalized four.
/// </summary>
public sealed class ForgeCliTests
{
    [Theory]
    [InlineData("MERGED", "merged")]
    [InlineData("completed", "merged")]
    [InlineData("CLOSED", "closed")]
    [InlineData("abandoned", "closed")]
    [InlineData("OPEN", "open")]
    [InlineData("opened", "open")]
    [InlineData("active\n", "open")]
    [InlineData("draft?", "unknown")]
    [InlineData(null, "unknown")]
    public void States_AreNormalized(string? raw, string expected)
        => ForgeCli.NormalizeState(raw).Should().Be(expected);

    [Fact]
    public void TheFirstUrlLine_IsThePullRequest()
        => ForgeCli.FirstUrl("Creating pull request for feature/x into main\n\nhttps://github.com/acme/app/pull/7\n")
            .Should().Be("https://github.com/acme/app/pull/7");

    [Fact]
    public void NoUrl_IsNull()
        => ForgeCli.FirstUrl("warning: nothing here").Should().BeNull();

    [Theory]
    [InlineData(ForgeKind.GitHub, "gh")]
    [InlineData(ForgeKind.AzureDevOps, "az")]
    [InlineData(ForgeKind.GitLab, "glab")]
    [InlineData(ForgeKind.Bitbucket, null)]
    [InlineData(ForgeKind.Other, null)]
    public void EachForge_HasItsCli(ForgeKind forge, string? expected)
        => ForgeCli.ExecutableFor(forge).Should().Be(expected);

    [Fact]
    public void SignedInAccounts_ReadsEveryAccountGhReports()
    {
        const string status = """
            github.com
              ✓ Logged in to github.com account dana-acme (keyring)
              - Active account: true
              ✓ Logged in to github.com account dana (keyring)
              - Active account: false
            ghe.acme.io
              ✓ Logged in to ghe.acme.io account dsmith (GH_ENTERPRISE_TOKEN)
            """;

        ForgeCli.SignedInAccounts(status).Should().Equal(
            "dana-acme@github.com", "dana@github.com", "dsmith@ghe.acme.io");
    }

    [Fact]
    public void SignedInAccounts_ReadsOlderGhAndGlabWording()
    {
        ForgeCli.SignedInAccounts("✓ Logged in to github.com as dana (oauth_token)")
            .Should().Equal("dana@github.com");
        ForgeCli.SignedInAccounts("gitlab.com\n  ✓ Logged in to gitlab.com as dana (/home/dana/.config/glab-cli/config.yml)")
            .Should().Equal("dana@gitlab.com");
        ForgeCli.SignedInAccounts("You are not logged into any GitHub hosts.").Should().BeEmpty();
    }
}
