namespace Orkka.Tests.Services;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;

/// <summary>
/// Copilot and Grok have no credential file the runner can read with certainty, so their
/// readiness is assembled from several weaker legs plus what the runs themselves report.
/// Each test stages a machine through the probe seams and a temp home (COPILOT_HOME /
/// GROK_HOME pinned to it, so a developer's real ~/.copilot never leaks in) — the confirmed
/// false positive that motivated this was exactly that: the VS Code extension's
/// ~/.copilot/{ide,logs,config.json} reading as "copilot connected" on a machine with no
/// copilot CLI.
/// </summary>
[Collection("ClaudeSetupTokenSeam")]
public sealed class ProviderDetectionCopilotGrokTests : IDisposable
{
    private readonly string _home;
    private readonly string _copilotHome;
    private readonly string _grokHome;
    private readonly string? _copilotHomeBefore;
    private readonly string? _grokHomeBefore;

    public ProviderDetectionCopilotGrokTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "orkka-detect-" + Guid.NewGuid().ToString("N"));
        _copilotHome = Path.Combine(_home, ".copilot");
        _grokHome = Path.Combine(_home, ".grok");
        Directory.CreateDirectory(_home);

        _copilotHomeBefore = Environment.GetEnvironmentVariable("COPILOT_HOME");
        _grokHomeBefore = Environment.GetEnvironmentVariable("GROK_HOME");
        Environment.SetEnvironmentVariable("COPILOT_HOME", _copilotHome);
        Environment.SetEnvironmentVariable("GROK_HOME", _grokHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("COPILOT_HOME", _copilotHomeBefore);
        Environment.SetEnvironmentVariable("GROK_HOME", _grokHomeBefore);
        try { Directory.Delete(_home, recursive: true); } catch { /* temp — best effort */ }
    }

    /// <summary>A staged machine: every leg false unless the test says otherwise.</summary>
    private ProviderDetection Detection(
        bool cliFound = true,
        bool ghLoggedIn = false,
        Func<string, string?>? stackEnv = null,
        ProviderAuthOverrides? overrides = null,
        Func<bool>? ghProbe = null) =>
        new(NullLogger<ProviderDetection>.Instance, overrides, new ProviderDetectionProbes
        {
            CliOnPath = _ => cliFound,
            // Hermetic: never spawn the machine's real CLIs (`--version`, `codex login status`).
            CliIsStub = _ => false,
            CliVersion = _ => null,
            CodexLoginStatus = () => null,
            GhLoggedIn = ghProbe ?? (() => ghLoggedIn),
            // These tests are about how readiness is DETECTED; the guard policy that holds
            // unguarded CLIs back until the developer opts in has its own tests
            // (DeveloperMachineGuardTests), so the opt-in is given here.
            ReadRunnerEnv = key => key == ProviderDetection.AllowUnguardedKey
                ? "gemini,grok,opencode"
                : (stackEnv ?? (_ => null))(key),
            Home = () => _home,
        });

    private static ProviderAuthOverrides Overrides(
        DateTimeOffset? now = null, Func<string, DateTimeOffset?>? writtenAt = null) =>
        new()
        {
            Now = () => now ?? new DateTimeOffset(2026, 9, 7, 15, 30, 0, TimeSpan.Zero),
            CredentialsWrittenAt = writtenAt ?? (_ => null),
        };

    private void WriteExtensionOnlyCopilotDir()
    {
        Directory.CreateDirectory(Path.Combine(_copilotHome, "ide"));
        Directory.CreateDirectory(Path.Combine(_copilotHome, "logs"));
        File.WriteAllText(Path.Combine(_copilotHome, "ide", "f5e473e5.lock"), "{}");
        File.WriteAllText(Path.Combine(_copilotHome, "logs", "process-1788572766426.log"), "started");
        File.WriteAllText(Path.Combine(_copilotHome, "config.json"), "{\"theme\":\"auto\"}");
    }

    // ── never authenticated without the CLI ────────────────────────────────────────

    [Theory]
    [InlineData(RunnerProviders.Copilot)]
    [InlineData(RunnerProviders.Grok)]
    public void NoCli_IsNeverAuthenticated_WhateverTheCredentialsSay(string provider)
    {
        File.WriteAllText(Path.Combine(_home, "auth.json"), "{}");
        Directory.CreateDirectory(_copilotHome);
        Directory.CreateDirectory(_grokHome);
        File.WriteAllText(Path.Combine(_copilotHome, "auth-token.json"), "{}");
        File.WriteAllText(Path.Combine(_grokHome, "credentials.json"), "{}");
        var overrides = Overrides();
        overrides.ReportSuccess(provider == RunnerProviders.Copilot ? AiProvider.GitHubCopilot : AiProvider.XAI);

        var status = Detection(
            cliFound: false, ghLoggedIn: true, stackEnv: _ => "ghp_or_xai_key", overrides: overrides)
            .Detect(provider);

        status.CliFound.Should().BeFalse();
        status.Authenticated.Should().BeFalse(
            "no login artifact, token or recorded run can vouch for a CLI that is not installed");
    }

    // ── copilot ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Copilot_ExtensionOnlyConfigDir_ReadsAsNotSignedIn()
    {
        WriteExtensionOnlyCopilotDir();

        var status = Detection().Detect(RunnerProviders.Copilot);

        status.CliFound.Should().BeTrue();
        status.Authenticated.Should().BeFalse(
            "ide/, logs/ and config.json are the VS Code extension's, not `copilot login`'s");
        status.Detail.Should().Contain("copilot login").And.Contain("gh auth login");
    }

    [Fact]
    public void Copilot_MissingConfigDir_ReadsAsNotSignedIn()
    {
        Detection().Detect(RunnerProviders.Copilot).Authenticated.Should().BeFalse();
    }

    [Theory]
    [InlineData("COPILOT_GITHUB_TOKEN")]
    [InlineData("GH_TOKEN")]
    [InlineData("GITHUB_TOKEN")]
    public void Copilot_StackEnvToken_Authenticates(string variable)
    {
        WriteExtensionOnlyCopilotDir();

        var status = Detection(stackEnv: key => key == variable ? "ghp_example" : null)
            .Detect(RunnerProviders.Copilot);

        status.Authenticated.Should().BeTrue();
        status.Detail.Should().BeNull();
    }

    [Fact]
    public void Copilot_GhLogin_Authenticates()
    {
        Detection(ghLoggedIn: true).Detect(RunnerProviders.Copilot).Authenticated.Should().BeTrue(
            "the copilot CLI falls back to the gh CLI's token");
    }

    [Fact]
    public void Copilot_GhCheck_RunsOncePerDetectionPass()
    {
        var calls = 0;
        // cliFound false keeps the opencode leg from shelling out; the copilot leg still
        // consults gh before the CLI check settles the verdict.
        var detection = Detection(cliFound: false, ghProbe: () => { calls++; return false; });

        detection.Detect(RunnerProviders.Copilot);
        detection.Detect(RunnerProviders.Copilot);
        calls.Should().Be(1, "run pre-flights within the TTL share one answer");

        detection.DetectAll();
        detection.DetectAll();
        calls.Should().Be(3, "one gh spawn per pass, never one per provider or per Detect");
    }

    [Theory]
    [InlineData("auth.json")]
    [InlineData("github-token")]
    [InlineData("credentials")]
    [InlineData("session.json")]
    [InlineData("state/oauth-token.json")]
    public void Copilot_LoginArtifact_Authenticates(string relativePath)
    {
        WriteExtensionOnlyCopilotDir();
        var path = Path.Combine(_copilotHome, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");

        Detection().Detect(RunnerProviders.Copilot).Authenticated.Should().BeTrue();
    }

    [Theory]
    [InlineData("ide/session.lock")]
    [InlineData("logs/auth-debug.log")]
    public void Copilot_CredentialLookingNamesInsideNoiseDirs_DoNotCount(string relativePath)
    {
        WriteExtensionOnlyCopilotDir();
        var path = Path.Combine(_copilotHome, relativePath);
        File.WriteAllText(path, "{}");

        Detection().Detect(RunnerProviders.Copilot).Authenticated.Should().BeFalse(
            "the extension and the CLI's logger write there on every run");
    }

    [Fact]
    public void Copilot_HasCredentials_IsStructuralOnly_ForTheLoginWatcher()
    {
        var overrides = Overrides();
        overrides.ReportSuccess(AiProvider.GitHubCopilot);

        Detection(overrides: overrides).HasCredentials(RunnerProviders.Copilot).Should().BeFalse(
            "StartProviderLogin polls this to see a login LAND — a recorded run is not that");
    }

    // ── grok ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Grok_StackEnvKey_Authenticates_WithoutCaveat()
    {
        var status = Detection(stackEnv: key => key == "XAI_API_KEY" ? "xai-example" : null)
            .Detect(RunnerProviders.Grok);

        status.Authenticated.Should().BeTrue();
        status.Detail.Should().BeNull();
    }

    [Fact]
    public void Grok_CredentialsFile_Authenticates_WithoutCaveat()
    {
        Directory.CreateDirectory(_grokHome);
        File.WriteAllText(Path.Combine(_grokHome, "credentials.json"), "{}");

        var status = Detection().Detect(RunnerProviders.Grok);

        status.Authenticated.Should().BeTrue();
        status.Detail.Should().BeNull();
    }

    [Fact]
    public void Grok_CliWithoutEvidence_FailsOpen_WithTheCaveatInDetail()
    {
        Directory.CreateDirectory(_grokHome);
        File.WriteAllText(Path.Combine(_grokHome, "settings.json"), "{}");

        var status = Detection().Detect(RunnerProviders.Grok);

        status.Authenticated.Should().BeTrue("grok may keep its token in the OS store; a run failing corrects this");
        status.Detail.Should().Be(ProviderDetection.GrokUnverifiedHint);
    }

    // ── run-time overrides ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RunnerProviders.Copilot, AiProvider.GitHubCopilot)]
    [InlineData(RunnerProviders.Grok, AiProvider.XAI)]
    [InlineData(RunnerProviders.Claude, AiProvider.Anthropic)]
    public void AuthFailure_ForcesUnauthenticated_KeepsCliFound_NamesTheFailure(string provider, AiProvider aiProvider)
    {
        var at = new DateTimeOffset(2026, 9, 7, 15, 30, 0, TimeSpan.Zero);
        var overrides = Overrides(now: at);
        overrides.ReportAuthFailure(aiProvider, "copilot refused this run because it isn't signed in on this machine (Bad credentials). Run `copilot login` there, then retry.");
        // Every structural leg says yes — the run's word still wins.
        var original = ProviderDetection.ReadClaudeSetupToken;
        ProviderDetection.ReadClaudeSetupToken = () => "sk-ant-oat01-example";
        try
        {
            var status = Detection(ghLoggedIn: true, stackEnv: _ => "token", overrides: overrides)
                .Detect(provider);

            status.CliFound.Should().BeTrue("the CLI is there — it is the login that is not");
            status.Authenticated.Should().BeFalse();
            status.Detail.Should().StartWith($"last run failed to authenticate ({at:t}) — ")
                .And.Contain("Bad credentials")
                .And.Contain("Run `copilot login` there");
        }
        finally
        {
            ProviderDetection.ReadClaudeSetupToken = original;
        }
    }

    [Fact]
    public void RecordedSuccess_MakesAnInconclusiveCopilotProbeReadAuthenticated()
    {
        WriteExtensionOnlyCopilotDir();
        var overrides = Overrides();
        overrides.ReportSuccess(AiProvider.GitHubCopilot);

        var status = Detection(overrides: overrides).Detect(RunnerProviders.Copilot);

        status.Authenticated.Should().BeTrue("a clean run is the one positive proof the probe cannot produce");
        status.Detail.Should().BeNull();
    }

    [Fact]
    public void ReportSuccess_ClearsAnEarlierFailure()
    {
        var overrides = Overrides();
        overrides.ReportAuthFailure(AiProvider.GitHubCopilot, "not signed in");
        overrides.ReportSuccess(AiProvider.GitHubCopilot);

        Detection(ghLoggedIn: true, overrides: overrides).Detect(RunnerProviders.Copilot)
            .Authenticated.Should().BeTrue();
        overrides.FailureDetail(RunnerProviders.Copilot).Should().BeNull();
    }

    [Fact]
    public void ClearFailure_DropsTheOverride_ForTheLoginWatcher()
    {
        var overrides = Overrides();
        overrides.ReportAuthFailure(AiProvider.OpenAI, "run `codex login`");

        overrides.ClearFailure(RunnerProviders.Codex);

        overrides.FailureDetail(RunnerProviders.Codex).Should().BeNull();
    }

    [Fact]
    public void CredentialsWrittenAfterTheFailure_ClearIt()
    {
        var failedAt = new DateTimeOffset(2026, 9, 7, 15, 30, 0, TimeSpan.Zero);
        var overrides = Overrides(now: failedAt, writtenAt: _ => failedAt.AddMinutes(2));
        overrides.ReportAuthFailure(AiProvider.GitHubCopilot, "Bad credentials");

        var status = Detection(ghLoggedIn: true, overrides: overrides).Detect(RunnerProviders.Copilot);

        status.Authenticated.Should().BeTrue("the user re-logged in from their own terminal after the failure");
        overrides.FailureDetail(RunnerProviders.Copilot).Should().BeNull("the stale failure is dropped, not just hidden");
    }

    [Fact]
    public void CredentialsWrittenBeforeTheFailure_KeepIt()
    {
        var failedAt = new DateTimeOffset(2026, 9, 7, 15, 30, 0, TimeSpan.Zero);
        var overrides = Overrides(now: failedAt, writtenAt: _ => failedAt.AddMinutes(-2));
        overrides.ReportAuthFailure(AiProvider.GitHubCopilot, "Bad credentials");

        var status = Detection(ghLoggedIn: true, overrides: overrides).Detect(RunnerProviders.Copilot);

        status.Authenticated.Should().BeFalse("the credentials the failed run used are still the ones on disk");
    }

    [Fact]
    public void Changed_FiresOnFailureAndSuccess_NotOnReads()
    {
        var overrides = Overrides();
        var fired = 0;
        overrides.Changed += () => fired++;

        overrides.ReportAuthFailure(AiProvider.Google, "UNAUTHENTICATED");
        fired.Should().Be(1);

        Detection(overrides: overrides).Detect(RunnerProviders.Gemini);
        overrides.FailureDetail(RunnerProviders.Gemini);
        overrides.HasRecordedSuccess(RunnerProviders.Gemini);
        fired.Should().Be(1, "reads never announce — a Hello would re-trigger itself");

        overrides.ReportSuccess(AiProvider.Google);
        fired.Should().Be(2);

        overrides.ReportSuccess(AiProvider.Google);
        fired.Should().Be(2, "an unchanged state has nothing to announce");
    }

    [Fact]
    public void OtherProviders_KeepTheirProbeVerdict_WithoutOverrides()
    {
        // Gemini with nothing configured under a temp home: CLI found, not authenticated,
        // API-key remedy — exactly what it said before copilot/grok grew their new legs.
        var status = Detection().Detect(RunnerProviders.Gemini);

        status.CliFound.Should().BeTrue();
        status.Authenticated.Should().BeFalse();
        status.Detail.Should().Be(ProviderDetection.GeminiApiKeyHint);
    }

    [Fact]
    public void CopilotNoiseDirs_AreSkippedWhenLookingForANewerWrite()
    {
        WriteExtensionOnlyCopilotDir();

        ProviderCredentialFiles.NewestFileUnder(_copilotHome, ProviderCredentialFiles.CopilotNoiseDirs)
            .Should().NotBeNull("config.json is at the top level")
            .And.Be(File.GetLastWriteTimeUtc(Path.Combine(_copilotHome, "config.json")));

        File.Delete(Path.Combine(_copilotHome, "config.json"));
        ProviderCredentialFiles.NewestFileUnder(_copilotHome, ProviderCredentialFiles.CopilotNoiseDirs)
            .Should().BeNull("ide/ and logs/ are written on every run and would fake a re-login");
    }
}
