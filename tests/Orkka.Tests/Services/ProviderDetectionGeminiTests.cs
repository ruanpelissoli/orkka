namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.LocalRunner;

/// <summary>
/// Drives the pure parsing legs of gemini API-key detection (internal statics via
/// InternalsVisibleTo). The file-system plumbing around them is trivial exists+read;
/// these cover the formats the gemini CLI actually writes: dotenv ~/.gemini/.env and
/// settings.json in both auth-selection schemas (root selectedAuthType and nested
/// security.auth.selectedType).
/// </summary>
public sealed class ProviderDetectionGeminiTests
{
    [Theory]
    [InlineData("GEMINI_API_KEY=AIzaSyExample123")]
    [InlineData("GEMINI_API_KEY = AIzaSyExample123")]
    [InlineData("GEMINI_API_KEY=\"AIzaSyExample123\"")]
    [InlineData("GEMINI_API_KEY='AIzaSyExample123'")]
    [InlineData("export GEMINI_API_KEY=AIzaSyExample123")]
    [InlineData("GOOGLE_API_KEY=AIzaSyExample123")]
    [InlineData("# comment\nOTHER=1\r\nGEMINI_API_KEY=AIzaSyExample123\n")]
    public void EnvFile_WithApiKey_IsDetected(string envText)
    {
        ProviderDetection.GeminiEnvHasApiKey(envText).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("GEMINI_API_KEY=")]
    [InlineData("GEMINI_API_KEY=\"\"")]
    [InlineData("# GEMINI_API_KEY=AIzaSyExample123")]
    [InlineData("GEMINI_API_KEY_BACKUP=AIzaSyExample123")]
    [InlineData("SOME_OTHER_KEY=value")]
    public void EnvFile_WithoutUsableApiKey_IsNotDetected(string envText)
    {
        ProviderDetection.GeminiEnvHasApiKey(envText).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"selectedAuthType":"gemini-api-key"}""")]
    [InlineData("""{"theme":"Default","selectedAuthType":"gemini-api-key",}""")]
    [InlineData("""
        {
          // settings schema v2
          "security": { "auth": { "selectedType": "gemini-api-key" } }
        }
        """)]
    public void Settings_SelectingApiKeyAuth_IsDetected(string json)
    {
        ProviderDetection.GeminiSettingsSelectApiKey(json).Should().BeTrue();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"selectedAuthType":"oauth-personal"}""")]
    [InlineData("""{"security":{"auth":{"selectedType":"oauth-personal"}}}""")]
    [InlineData("""[1,2,3]""")]
    public void Settings_NotSelectingApiKeyAuth_IsNotDetected(string json)
    {
        ProviderDetection.GeminiSettingsSelectApiKey(json).Should().BeFalse();
    }
}
