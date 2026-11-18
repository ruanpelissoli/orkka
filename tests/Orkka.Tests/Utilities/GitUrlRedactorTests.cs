namespace Orkka.Tests.Utilities;

using FluentAssertions;
using Orkka.Git;

public sealed class GitUrlRedactorTests
{
    [Fact]
    public void Redact_RemovesAccessTokenFromRemoteUrl()
    {
        const string secret = "ghs_supersecrettoken1234567890";
        var input = $"remote set-url origin \"https://x-access-token:{secret}@github.com/acme/app.git\"";

        var result = GitUrlRedactor.Redact(input);

        result.Should().NotContain(secret);
        result.Should().NotContain("x-access-token");
        result.Should().Contain("https://***@github.com/acme/app.git");
    }

    [Fact]
    public void Redact_RemovesUserPasswordCredentials()
    {
        var result = GitUrlRedactor.Redact("clone https://user:p4ssw0rd@example.com/repo.git bare");

        result.Should().NotContain("p4ssw0rd");
        result.Should().Contain("https://***@example.com/repo.git");
    }

    [Fact]
    public void Redact_LeavesCredentialFreeTextUnchanged()
    {
        const string input = "fetch origin";

        GitUrlRedactor.Redact(input).Should().Be(input);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Redact_HandlesNullAndEmpty(string? input)
    {
        GitUrlRedactor.Redact(input).Should().Be(string.Empty);
    }
}
