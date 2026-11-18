namespace Orkka.Tests.Services;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Contracts;

/// <summary>
/// Gemini readiness is API-key only. A Google sign-in (~/.gemini/oauth_creds.json) proves an
/// account, not a Gemini plan — the runner on LAPTOP-B reported "gemini connected" from a
/// login made only to manage an API key, on a machine with no Gemini plan (prod,
/// 2026-09-15). Each test stages a machine through the probe seams and a temp home, so a
/// developer's real ~/.gemini never leaks in.
/// </summary>
public sealed class ProviderDetectionGeminiReadinessTests : IDisposable
{
    private readonly string _home;
    private readonly string _geminiDir;

    public ProviderDetectionGeminiReadinessTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "orkka-gemini-" + Guid.NewGuid().ToString("N"));
        _geminiDir = Path.Combine(_home, ".gemini");
        Directory.CreateDirectory(_geminiDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* temp — best effort */ }
    }

    private ProviderDetection Detection(
        bool cliFound = true,
        Func<string, string?>? stackEnv = null,
        ProviderAuthOverrides? overrides = null) =>
        new(NullLogger<ProviderDetection>.Instance, overrides, new ProviderDetectionProbes
        {
            CliOnPath = _ => cliFound,
            // Hermetic: never spawn the machine's real CLIs (`--version`, `codex login status`).
            CliIsStub = _ => false,
            CliVersion = _ => null,
            CodexLoginStatus = () => null,
            GhLoggedIn = () => false,
            // These tests are about how readiness is DETECTED; the guard policy that holds
            // unguarded CLIs back until the developer opts in has its own tests
            // (DeveloperMachineGuardTests), so the opt-in is given here.
            ReadRunnerEnv = key => key == ProviderDetection.AllowUnguardedKey
                ? "gemini,grok,opencode"
                : (stackEnv ?? (_ => null))(key),
            Home = () => _home,
        });

    private void WriteOAuthCreds(bool withRefreshToken = true, long? expiryUnixMs = null)
    {
        var expiry = expiryUnixMs ?? DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        var refresh = withRefreshToken ? "\"refresh_token\":\"1//0refresh\"," : "";
        File.WriteAllText(
            Path.Combine(_geminiDir, "oauth_creds.json"),
            $$"""{"access_token":"ya29.access",{{refresh}}"scope":"openid","token_type":"Bearer","expiry_date":{{expiry}}}""");
    }

    // ── a Google sign-in alone is not ready ────────────────────────────────────────

    [Fact]
    public void OAuthSignIn_WithRefreshToken_IsNotReady_AndSaysWhy()
    {
        WriteOAuthCreds();

        var status = Detection().Detect(RunnerProviders.Gemini);

        status.CliFound.Should().BeTrue();
        status.Authenticated.Should().BeFalse();
        status.Detail.Should().Be(ProviderDetection.GeminiOAuthUnverifiedHint);
    }

    [Fact]
    public void OAuthSignIn_WithLiveAccessToken_IsNotReady()
    {
        WriteOAuthCreds(withRefreshToken: false);

        var status = Detection().Detect(RunnerProviders.Gemini);

        status.Authenticated.Should().BeFalse();
        status.Detail.Should().Be(ProviderDetection.GeminiOAuthUnverifiedHint);
    }

    [Fact]
    public void OAuthSignIn_Expired_GetsThePlainApiKeyHint()
    {
        WriteOAuthCreds(withRefreshToken: false, expiryUnixMs: 1_000);

        var status = Detection().Detect(RunnerProviders.Gemini);

        status.Authenticated.Should().BeFalse();
        status.Detail.Should().Be(ProviderDetection.GeminiApiKeyHint);
    }

    [Fact]
    public void OAuthSignIn_WithoutTheCli_IsNeitherFoundNorReady()
    {
        WriteOAuthCreds();

        var status = Detection(cliFound: false).Detect(RunnerProviders.Gemini);

        status.CliFound.Should().BeFalse();
        status.Authenticated.Should().BeFalse();
        status.Detail.Should().BeNull("an uninstalled CLI has nothing to sign in to");
    }

    [Fact]
    public void NothingConfigured_IsNotReady_WithTheApiKeyHint()
    {
        var status = Detection().Detect(RunnerProviders.Gemini);

        status.CliFound.Should().BeTrue();
        status.Authenticated.Should().BeFalse();
        status.Detail.Should().Be(ProviderDetection.GeminiApiKeyHint);
    }

    [Fact]
    public void OAuthSignIn_IsNotReady_EvenAfterARecordedSuccess()
    {
        // A clean run vouches for copilot/grok, whose probes are inconclusive; for gemini the
        // success would have come from a host-sent API key, not the sign-in, so it must not
        // promote the sign-in itself.
        WriteOAuthCreds();
        var overrides = new ProviderAuthOverrides { CredentialsWrittenAt = _ => null };
        overrides.ReportSuccess(Orkka.Modules.Agents.Contracts.AiProvider.Google);

        var status = Detection(overrides: overrides).Detect(RunnerProviders.Gemini);

        status.Authenticated.Should().BeFalse();
    }

    // ── an API key is ready ────────────────────────────────────────────────────────

    [Fact]
    public void ApiKeyInEnvFile_IsReady()
    {
        File.WriteAllText(Path.Combine(_geminiDir, ".env"), "GEMINI_API_KEY=AIzaSyExample123\n");

        var status = Detection().Detect(RunnerProviders.Gemini);

        status.CliFound.Should().BeTrue();
        status.Authenticated.Should().BeTrue();
        status.Detail.Should().BeNull();
    }

    [Fact]
    public void ApiKeyInEnvFile_NextToAnOAuthSignIn_IsReady()
    {
        WriteOAuthCreds();
        File.WriteAllText(Path.Combine(_geminiDir, ".env"), "GEMINI_API_KEY=AIzaSyExample123\n");

        Detection().Detect(RunnerProviders.Gemini).Authenticated.Should().BeTrue();
    }

    [Fact]
    public void SettingsSelectingApiKeyAuth_IsReady()
    {
        File.WriteAllText(
            Path.Combine(_geminiDir, "settings.json"),
            """{"security":{"auth":{"selectedType":"gemini-api-key"}}}""");

        Detection().Detect(RunnerProviders.Gemini).Authenticated.Should().BeTrue();
    }

    [Fact]
    public void ApiKeyInStackEnv_IsReady()
    {
        var status = Detection(stackEnv: k => k == "ORKKA_GEMINI_API_KEY" ? "AIzaSyExample123" : null)
            .Detect(RunnerProviders.Gemini);

        status.Authenticated.Should().BeTrue();
    }

    [Fact]
    public void ApiKey_WithoutTheCli_IsNotReady()
    {
        File.WriteAllText(Path.Combine(_geminiDir, ".env"), "GEMINI_API_KEY=AIzaSyExample123\n");

        var status = Detection(cliFound: false).Detect(RunnerProviders.Gemini);

        status.CliFound.Should().BeFalse();
        status.Authenticated.Should().BeTrue("the key is configured; only the install is missing");
    }

    // ── a held auth failure still outranks a configured key ────────────────────────

    [Fact]
    public void ApiKey_WithARecordedAuthFailure_IsNotReady_UntilTheKeyChanges()
    {
        File.WriteAllText(Path.Combine(_geminiDir, ".env"), "GEMINI_API_KEY=AIzaSyBroken\n");
        var failedAt = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var overrides = new ProviderAuthOverrides
        {
            Now = () => failedAt,
            CredentialsWrittenAt = _ => failedAt.AddMinutes(-5),
        };
        overrides.ReportAuthFailure(
            Orkka.Modules.Agents.Contracts.AiProvider.Google, "API key not valid");

        var status = Detection(overrides: overrides).Detect(RunnerProviders.Gemini);

        status.Authenticated.Should().BeFalse();
        status.Detail.Should().Contain("API key not valid");
    }
}
