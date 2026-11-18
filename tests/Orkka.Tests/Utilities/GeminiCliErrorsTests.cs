namespace Orkka.Tests.Utilities;

using FluentAssertions;
using Orkka.SharedKernel.Ai;

public sealed class GeminiCliErrorsTests
{
    [Theory]
    [InlineData("IneligibleTierError: your account is not eligible for this tier")]
    [InlineData("ineligibletiererror")]
    [InlineData("gemini CLI exited with code 1.\n--- gemini CLI stderr ---\nError: [IneligibleTierError] Please migrate to the Antigravity suite.")]
    [InlineData("This account is no longer supported — see https://antigravity.google for details")]
    public void Describe_IneligibleTier_ReturnsActionableMessage(string message)
    {
        var described = GeminiCliErrors.Describe(message);

        described.Should().Be(GeminiCliErrors.IneligibleTierMessage);
        described.Should().Contain("aistudio.google.com/apikey");
        described.Should().Contain("GEMINI_API_KEY");
        described.Should().Contain("gemini-api-key");
    }

    [Theory]
    [InlineData("gemini CLI exited with code 41.\n--- gemini CLI stderr ---\nError: must specify GEMINI_API_KEY")]
    [InlineData("Please set an Auth method in your settings.json or specify one of the auth env variables")]
    public void Describe_MissingApiKey_ReturnsApiKeySetupGuidance(string message)
    {
        var described = GeminiCliErrors.Describe(message);

        described.Should().Be(GeminiCliErrors.MissingApiKeyMessage);
        described.Should().Contain("aistudio.google.com/apikey");
        described.Should().Contain("Settings");
        described.Should().Contain("~/.gemini/.env");
        // The pre-fix workaround file must never be recommended again: ~/.orkka/.env
        // sat inside the CLI's dotenv walk and leaked stack secrets to agents.
        described.Should().NotContain("~/.orkka/.env");
        described.Should().Contain("runner.env");
    }

    [Theory]
    // Verbatim live failure (2026-07-13): catalog ids that were Code Assist tier
    // aliases 404 on the API-key backend.
    [InlineData("gemini CLI exited with code 1.\n--- gemini CLI stderr ---\nError: [ModelNotFoundError] models/gemini-3-pro is not found for API version v1beta, or is not supported for generateContent")]
    [InlineData("modelnotfounderror")]
    [InlineData("models/gemini-9000 is not found for API version v1beta")]
    public void Describe_ModelNotFound_ReturnsModelPickerGuidance(string message)
    {
        var described = GeminiCliErrors.Describe(message);

        described.Should().Be(GeminiCliErrors.ModelNotFoundMessage);
        described.Should().Contain("model settings");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gemini CLI exited with code 1.")]
    [InlineData("429 RESOURCE_EXHAUSTED: quota exceeded")]
    public void Describe_UnrecognizedMessage_ReturnsNull(string? message)
    {
        GeminiCliErrors.Describe(message).Should().BeNull();
    }
}
