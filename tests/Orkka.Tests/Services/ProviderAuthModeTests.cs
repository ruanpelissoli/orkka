namespace Orkka.Tests.Services;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// How each CLI signs in, as the runner reports it in the Hello — from names, plain settings and
/// config files only. Every detail must be safe to show: a region, a host, a variable name.
/// </summary>
[Collection("ClaudeSetupTokenSeam")]
public sealed class ProviderAuthModeTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "orkka-authmode-" + Guid.NewGuid().ToString("N"));

    private readonly Func<string?> _setupTokenBefore = ProviderDetection.ReadClaudeSetupToken;

    public ProviderAuthModeTests()
    {
        Directory.CreateDirectory(_home);
        // The developer's real ~/.orkka/runner.env may hold a setup token.
        ProviderDetection.ReadClaudeSetupToken = () => null;
    }

    public void Dispose()
    {
        ProviderDetection.ReadClaudeSetupToken = _setupTokenBefore;
        try { Directory.Delete(_home, recursive: true); } catch { /* temp */ }
    }

    private sealed class StagedSource(Dictionary<string, string> machine, Dictionary<string, Dictionary<string, string>>? workspaces = null)
        : IProviderAuthSource
    {
        public IReadOnlyDictionary<string, string> WorkspaceValues(string? workspace, IReadOnlyCollection<string> names)
            => workspace is not null && workspaces?.TryGetValue(workspace, out var v) == true
                ? v.Where(kv => names.Contains(kv.Key)).ToDictionary()
                : new Dictionary<string, string>();

        public IReadOnlyDictionary<string, string> MachineValues(IReadOnlyCollection<string> names)
            => machine.Where(kv => names.Contains(kv.Key)).ToDictionary();

        public IReadOnlyCollection<string> CodexConfigEnvKeys() => ["ACME_AZURE_KEY"];
    }

    private ProviderDetection Detection(IProviderAuthSource source, string? codexToml = null, string? codexLogin = null) =>
        new(NullLogger<ProviderDetection>.Instance, null, new ProviderDetectionProbes
        {
            CliOnPath = _ => true,
            GhLoggedIn = () => false,
            ReadRunnerEnv = _ => null,
            Home = () => _home,
            ClaudeConfigDir = () => Path.Combine(_home, ".claude"),
            CodexHome = () => Path.Combine(_home, ".codex"),
            CodexConfigToml = () => codexToml,
            CodexLoginStatus = () => codexLogin,
            GitHubHostFromEnvironment = () => null,
            CliVersion = _ => "1.2.3",
            CliIsStub = _ => false,
            AuthSource = () => source,
        });

    [Fact]
    public void Claude_Bedrock_IsReady_WithTheRegion()
    {
        var status = Detection(new StagedSource(new()
        {
            ["CLAUDE_CODE_USE_BEDROCK"] = "1",
            ["AWS_REGION"] = "us-east-1",
            ["AWS_SECRET_ACCESS_KEY"] = "very-secret",
        })).Detect(RunnerProviders.Claude);

        status.Authenticated.Should().BeTrue("an env-key mode counts as ready");
        status.AuthMode.Should().Be(ProviderAuthModes.Bedrock);
        status.AuthDetail.Should().Be("us-east-1");
        status.CliVersion.Should().Be("1.2.3");
        status.ToString().Should().NotContain("very-secret");
    }

    [Fact]
    public void Claude_Vertex_ShowsRegionAndProject()
    {
        var status = Detection(new StagedSource(new()
        {
            ["CLAUDE_CODE_USE_VERTEX"] = "1",
            ["CLOUD_ML_REGION"] = "global",
            ["ANTHROPIC_VERTEX_PROJECT_ID"] = "acme-ai",
        })).Detect(RunnerProviders.Claude);

        status.AuthMode.Should().Be(ProviderAuthModes.Vertex);
        status.AuthDetail.Should().Be("global · acme-ai");
        status.Authenticated.Should().BeTrue();
    }

    [Fact]
    public void Claude_Gateway_ShowsTheHostOnly()
    {
        var status = Detection(new StagedSource(new()
        {
            ["ANTHROPIC_BASE_URL"] = "https://user:pw@llm.acme.internal:8443/v1?key=abc",
            ["ANTHROPIC_AUTH_TOKEN"] = "gw",
        })).Detect(RunnerProviders.Claude);

        status.AuthMode.Should().Be(ProviderAuthModes.Gateway);
        status.AuthDetail.Should().Be("llm.acme.internal:8443");
    }

    [Fact]
    public void Claude_ApiKey_And_SettingsApiKeyHelper()
    {
        Detection(new StagedSource(new() { ["ANTHROPIC_API_KEY"] = "k" })).Detect(RunnerProviders.Claude)
            .AuthMode.Should().Be(ProviderAuthModes.ApiKey);

        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        File.WriteAllText(Path.Combine(_home, ".claude", "settings.json"), """{ "apiKeyHelper": "~/bin/get-key.sh" }""");
        var status = Detection(new StagedSource([])).Detect(RunnerProviders.Claude);
        status.AuthMode.Should().Be(ProviderAuthModes.ApiKey);
        status.AuthDetail.Should().Be("apiKeyHelper");
        status.Authenticated.Should().BeTrue();
    }

    [Fact]
    public void Claude_SettingsEnvBlock_Bedrock_IsSeen()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        File.WriteAllText(Path.Combine(_home, ".claude", "settings.json"),
            """{ "env": { "CLAUDE_CODE_USE_BEDROCK": "1", "AWS_REGION": "eu-central-1", "AWS_BEARER_TOKEN_BEDROCK": "secret" } }""");

        var status = Detection(new StagedSource([])).Detect(RunnerProviders.Claude);

        status.AuthMode.Should().Be(ProviderAuthModes.Bedrock);
        status.AuthDetail.Should().Be("eu-central-1");
    }

    [Fact]
    public void Claude_NothingConfigured_IsNotSignedIn()
    {
        var status = Detection(new StagedSource([])).Detect(RunnerProviders.Claude);

        status.Authenticated.Should().BeFalse();
        status.AuthMode.Should().Be(ProviderAuthModes.None);
    }

    [Fact]
    public void Codex_ChatGptLogin_FromAuthJson()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".codex"));
        File.WriteAllText(Path.Combine(_home, ".codex", "auth.json"), """{ "tokens": { "id_token": "x" } }""");

        var status = Detection(new StagedSource([])).Detect(RunnerProviders.Codex);

        status.Authenticated.Should().BeTrue();
        status.AuthMode.Should().Be(ProviderAuthModes.Login);
        status.AuthDetail.Should().Be("ChatGPT");
    }

    [Fact]
    public void Codex_KeyringLogin_FromLoginStatus()
    {
        var status = Detection(new StagedSource([]), codexLogin: "chatgpt").Detect(RunnerProviders.Codex);

        status.Authenticated.Should().BeTrue();
        status.AuthMode.Should().Be(ProviderAuthModes.Login);
    }

    [Fact]
    public void Codex_ApiKey_FromTheEnvironment()
    {
        var status = Detection(new StagedSource(new() { ["CODEX_API_KEY"] = "sk" })).Detect(RunnerProviders.Codex);

        status.Authenticated.Should().BeTrue();
        status.AuthMode.Should().Be(ProviderAuthModes.ApiKey);
        status.AuthDetail.Should().Be("CODEX_API_KEY");
    }

    private const string AzureToml = """
        model_provider = "azure"

        [model_providers.azure]
        name = "Azure"
        base_url = "https://acme.openai.azure.com/openai"
        env_key = "ACME_AZURE_KEY"
        """;

    [Fact]
    public void Codex_AzureOpenAi_ReadyWhenItsEnvKeyIsSet()
    {
        var status = Detection(new StagedSource(new() { ["ACME_AZURE_KEY"] = "k" }), AzureToml).Detect(RunnerProviders.Codex);

        status.Authenticated.Should().BeTrue();
        status.AuthMode.Should().Be(ProviderAuthModes.AzureOpenAi);
        status.AuthDetail.Should().Be("acme.openai.azure.com");
    }

    [Fact]
    public void Codex_AzureOpenAi_NotReadyWithoutItsEnvKey_SaysWhatToSet()
    {
        var status = Detection(new StagedSource([]), AzureToml, codexLogin: "chatgpt").Detect(RunnerProviders.Codex);

        status.Authenticated.Should().BeFalse();
        status.Detail.Should().Contain("ACME_AZURE_KEY").And.Contain("workspace set-env");
    }

    [Fact]
    public void Copilot_Token_And_EnterpriseHost()
    {
        var token = Detection(new StagedSource(new() { ["GH_TOKEN"] = "github_pat_x" })).Detect(RunnerProviders.Copilot);
        token.Authenticated.Should().BeTrue();
        token.AuthMode.Should().Be(ProviderAuthModes.Token);
        token.AuthDetail.Should().Be("GH_TOKEN");

        var ghe = Detection(new StagedSource(new() { ["COPILOT_GITHUB_TOKEN"] = "t", ["GH_HOST"] = "acme.ghe.com" }))
            .Detect(RunnerProviders.Copilot);
        ghe.AuthMode.Should().Be(ProviderAuthModes.EnterpriseHost);
        ghe.AuthDetail.Should().Be("acme.ghe.com");
        ghe.Authenticated.Should().BeTrue();
    }

    [Fact]
    public void Copilot_EnterpriseHostWithoutLogin_IsNotReady()
    {
        var status = Detection(new StagedSource(new() { ["GH_HOST"] = "acme.ghe.com" })).Detect(RunnerProviders.Copilot);

        status.Authenticated.Should().BeFalse();
        status.AuthMode.Should().Be(ProviderAuthModes.None);
        status.AuthDetail.Should().Be("acme.ghe.com");
    }

    [Fact]
    public void Workspace_Decides_TheModeOfItsOwnHello()
    {
        var source = new StagedSource(
            new() { ["ANTHROPIC_API_KEY"] = "personal" },
            new() { ["acme"] = new() { ["CLAUDE_CODE_USE_BEDROCK"] = "1", ["AWS_REGION"] = "us-west-2" } });
        var detection = Detection(source);

        detection.Detect(RunnerProviders.Claude, "acme").AuthMode.Should().Be(ProviderAuthModes.Bedrock);
        detection.Detect(RunnerProviders.Claude, "personal").AuthMode.Should().Be(ProviderAuthModes.ApiKey);
        detection.Detect(RunnerProviders.Claude).AuthMode.Should().Be(ProviderAuthModes.ApiKey);
    }

    [Fact]
    public void Workspace_LoginPin_IgnoresTheMachineKey()
    {
        var source = new StagedSource(
            new() { ["ANTHROPIC_API_KEY"] = "company" },
            new() { ["personal"] = new() { [ProviderAuthEnvironment.ClaudeModeKey] = "login" } });

        var status = Detection(source).Detect(RunnerProviders.Claude, "personal");

        status.AuthMode.Should().Be(ProviderAuthModes.None, "no ~/.claude login in the staged home");
        status.Authenticated.Should().BeFalse();
    }

    [Fact]
    public void LauncherStub_IsNotTheCli()
    {
        var detection = new ProviderDetection(NullLogger<ProviderDetection>.Instance, null, new ProviderDetectionProbes
        {
            CliOnPath = _ => true,
            GhLoggedIn = () => true,
            ReadRunnerEnv = _ => null,
            Home = () => _home,
            CliIsStub = p => p == RunnerProviders.Copilot,
            CliVersion = _ => null,
            AuthSource = () => null,
        });

        var status = detection.Detect(RunnerProviders.Copilot);

        status.CliFound.Should().BeFalse();
        status.Authenticated.Should().BeFalse();
        status.Detail.Should().Contain("launcher stub");
    }

    [Theory]
    [InlineData("https://user:pass@gw.acme.com/path?token=x", "gw.acme.com")]
    [InlineData("http://localhost:4000", "localhost:4000")]
    [InlineData("not a url", null)]
    public void HostOf_NeverKeepsUserInfoPathOrQuery(string url, string? host)
        => ProviderAuthModeDetector.HostOf(url).Should().Be(host);
}
