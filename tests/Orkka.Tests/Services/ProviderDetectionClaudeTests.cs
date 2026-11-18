namespace Orkka.Tests.Services;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// Claude auth detection around the long-lived setup-token path. A configured
/// CLAUDE_CODE_OAUTH_TOKEN (from `claude setup-token`, stored in ~/.orkka/runner.env)
/// must read as authenticated no matter what ~/.claude/.credentials.json says — the
/// credentials file being expired after a lost refresh race is exactly the state the
/// token exists to survive. The stack-env read is seamed (ReadClaudeSetupToken) so
/// these tests never touch the machine's real ~/.orkka.
/// </summary>
[Collection("ClaudeSetupTokenSeam")]
public sealed class ProviderDetectionClaudeTests
{
    [Fact]
    public void SetupToken_Configured_ReadsAsAuthenticated()
    {
        var original = ProviderDetection.ReadClaudeSetupToken;
        try
        {
            ProviderDetection.ReadClaudeSetupToken = () => "sk-ant-oat01-example";
            var detection = new ProviderDetection(NullLogger<ProviderDetection>.Instance);

            detection.HasCredentials(RunnerProviders.Claude).Should().BeTrue(
                "a long-lived setup token authenticates children regardless of the credentials file");
        }
        finally
        {
            ProviderDetection.ReadClaudeSetupToken = original;
        }
    }

    [Fact]
    public void SetupToken_Configured_DetectCarriesNoRemedyDetail()
    {
        var original = ProviderDetection.ReadClaudeSetupToken;
        try
        {
            ProviderDetection.ReadClaudeSetupToken = () => "sk-ant-oat01-example";
            var detection = new ProviderDetection(NullLogger<ProviderDetection>.Instance);

            var status = detection.Detect(RunnerProviders.Claude);

            status.Authenticated.Should().BeTrue();
            status.Detail.Should().BeNull("remedy hints only accompany an unauthenticated status");
        }
        finally
        {
            ProviderDetection.ReadClaudeSetupToken = original;
        }
    }

    [Fact]
    public void ClaudeSetupToken_NoSource_ResolvesNull()
    {
        var original = ClaudeSetupToken.Source;
        try
        {
            ClaudeSetupToken.Source = null;
            ClaudeSetupToken.Resolve().Should().BeNull();
        }
        finally
        {
            ClaudeSetupToken.Source = original;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ClaudeSetupToken_BlankValues_ResolveNull(string? value)
    {
        var original = ClaudeSetupToken.Source;
        try
        {
            ClaudeSetupToken.Source = () => value;
            ClaudeSetupToken.Resolve().Should().BeNull();
        }
        finally
        {
            ClaudeSetupToken.Source = original;
        }
    }

    [Fact]
    public void ClaudeSetupToken_PaddedValue_ResolvesTrimmed()
    {
        var original = ClaudeSetupToken.Source;
        try
        {
            ClaudeSetupToken.Source = () => "  sk-ant-oat01-example \n";
            ClaudeSetupToken.Resolve().Should().Be("sk-ant-oat01-example");
        }
        finally
        {
            ClaudeSetupToken.Source = original;
        }
    }
}
