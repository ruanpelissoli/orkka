namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// The per-provider sign-in passthrough: which variables reach which CLI, and from where. The
/// rule that matters most is isolation — a provider only ever gets its own allowlisted names,
/// and a workspace's values never mix with the machine's or another workspace's.
/// </summary>
public sealed class ProviderAuthEnvironmentTests
{
    /// <summary>A staged source: workspace → (name → value), plus machine values.</summary>
    private sealed class FakeSource(
        Dictionary<string, Dictionary<string, string>>? workspaces = null,
        Dictionary<string, string>? machine = null,
        string[]? codexKeys = null) : IProviderAuthSource
    {
        public IReadOnlyDictionary<string, string> WorkspaceValues(string? workspace, IReadOnlyCollection<string> names)
            => workspace is not null && workspaces?.TryGetValue(workspace, out var values) == true
                ? values.Where(kv => names.Contains(kv.Key)).ToDictionary()
                : new Dictionary<string, string>();

        public IReadOnlyDictionary<string, string> MachineValues(IReadOnlyCollection<string> names)
            => (machine ?? []).Where(kv => names.Contains(kv.Key)).ToDictionary();

        public IReadOnlyCollection<string> CodexConfigEnvKeys() => codexKeys ?? [];
    }

    private static readonly Dictionary<string, string> EveryProvidersKeys = new()
    {
        ["ANTHROPIC_API_KEY"] = "sk-ant-x",
        ["OPENAI_API_KEY"] = "sk-openai-x",
        ["CODEX_API_KEY"] = "sk-codex-x",
        ["AWS_SECRET_ACCESS_KEY"] = "aws-secret",
        ["AWS_ACCESS_KEY_ID"] = "AKIA",
        ["GH_TOKEN"] = "github_pat_x",
        ["COPILOT_GITHUB_TOKEN"] = "github_pat_y",
        ["DATABASE_URL"] = "postgres://u:p@h/db",
        ["STRIPE_SECRET_KEY"] = "sk_live",
    };

    [Fact]
    public void Claude_GetsOnlyClaudeVariables()
    {
        var auth = ProviderAuthEnvironment.Resolve(AiProvider.Anthropic, null, new FakeSource(machine: EveryProvidersKeys));

        auth.Variables.Keys.Should().BeEquivalentTo("ANTHROPIC_API_KEY", "AWS_SECRET_ACCESS_KEY", "AWS_ACCESS_KEY_ID");
        auth.Variables.Should().NotContainKeys("OPENAI_API_KEY", "CODEX_API_KEY", "GH_TOKEN", "COPILOT_GITHUB_TOKEN",
            "DATABASE_URL", "STRIPE_SECRET_KEY");
        auth.CarriesSecret.Should().BeTrue();
    }

    [Fact]
    public void Codex_NeverGetsAwsOrAnthropicOrGitHub()
    {
        var auth = ProviderAuthEnvironment.Resolve(AiProvider.OpenAI, null, new FakeSource(machine: EveryProvidersKeys));

        auth.Variables.Keys.Should().BeEquivalentTo("OPENAI_API_KEY", "CODEX_API_KEY");
    }

    [Fact]
    public void Copilot_GetsOnlyGitHubVariables()
    {
        var auth = ProviderAuthEnvironment.Resolve(AiProvider.GitHubCopilot, null, new FakeSource(machine: EveryProvidersKeys));

        auth.Variables.Keys.Should().BeEquivalentTo("GH_TOKEN", "COPILOT_GITHUB_TOKEN");
    }

    [Theory]
    [InlineData(AiProvider.Ollama)]
    [InlineData(AiProvider.Google)]
    [InlineData(AiProvider.XAI)]
    [InlineData(AiProvider.OpenCode)]
    public void OtherProviders_GetNothing(AiProvider provider)
        => ProviderAuthEnvironment.Resolve(provider, null, new FakeSource(machine: EveryProvidersKeys))
            .Variables.Should().BeEmpty();

    [Fact]
    public void Bedrock_PassesTheSwitchRegionProfileAndCredentials()
    {
        var auth = ProviderAuthEnvironment.Resolve(AiProvider.Anthropic, null, new FakeSource(machine: new()
        {
            ["CLAUDE_CODE_USE_BEDROCK"] = "1",
            ["AWS_REGION"] = "us-east-1",
            ["AWS_PROFILE"] = "work",
            ["AWS_BEARER_TOKEN_BEDROCK"] = "bedrock-key",
        }));

        auth.Variables.Should().Contain(new KeyValuePair<string, string>("CLAUDE_CODE_USE_BEDROCK", "1"))
            .And.Contain(new KeyValuePair<string, string>("AWS_REGION", "us-east-1"))
            .And.Contain(new KeyValuePair<string, string>("AWS_PROFILE", "work"))
            .And.ContainKey("AWS_BEARER_TOKEN_BEDROCK");
    }

    [Fact]
    public void Vertex_PassesProjectRegionAndCredentialsFile()
    {
        var auth = ProviderAuthEnvironment.Resolve(AiProvider.Anthropic, null, new FakeSource(machine: new()
        {
            ["CLAUDE_CODE_USE_VERTEX"] = "1",
            ["CLOUD_ML_REGION"] = "global",
            ["ANTHROPIC_VERTEX_PROJECT_ID"] = "acme-ai",
            ["GOOGLE_APPLICATION_CREDENTIALS"] = "/home/dev/sa.json",
        }));

        auth.Variables.Keys.Should().BeEquivalentTo(
            "CLAUDE_CODE_USE_VERTEX", "CLOUD_ML_REGION", "ANTHROPIC_VERTEX_PROJECT_ID", "GOOGLE_APPLICATION_CREDENTIALS");
        // Only configuration, no secret: the CLI's subprocess scrub is not needed for it.
        auth.CarriesSecret.Should().BeFalse();
    }

    [Fact]
    public void Gateway_PassesBaseUrlAndAuthToken()
    {
        var auth = ProviderAuthEnvironment.Resolve(AiProvider.Anthropic, null, new FakeSource(machine: new()
        {
            ["ANTHROPIC_BASE_URL"] = "https://llm.acme.internal",
            ["ANTHROPIC_AUTH_TOKEN"] = "gw-token",
        }));

        auth.Variables.Keys.Should().BeEquivalentTo("ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN");
    }

    [Fact]
    public void Codex_AzureOpenAi_PassesTheConfiguredEnvKey()
    {
        var source = new FakeSource(machine: new() { ["ACME_AZURE_KEY"] = "azure-secret" }, codexKeys: ["ACME_AZURE_KEY"]);

        ProviderAuthEnvironment.Resolve(AiProvider.OpenAI, null, source).Variables
            .Should().ContainKey("ACME_AZURE_KEY");
        // Codex's own config naming a variable never hands it to Claude.
        ProviderAuthEnvironment.Resolve(AiProvider.Anthropic, null, source).Variables.Should().BeEmpty();
    }

    [Fact]
    public void Copilot_EnterpriseHost_PassesTokenAndHost()
    {
        var auth = ProviderAuthEnvironment.Resolve(AiProvider.GitHubCopilot, null, new FakeSource(machine: new()
        {
            ["COPILOT_GITHUB_TOKEN"] = "github_pat_x",
            ["GH_HOST"] = "acme.ghe.com",
        }));

        auth.Variables.Keys.Should().BeEquivalentTo("COPILOT_GITHUB_TOKEN", "GH_HOST");
    }

    [Fact]
    public void Workspace_Overrides_ReplaceTheMachineForThatProvider()
    {
        var source = new FakeSource(
            workspaces: new()
            {
                ["acme"] = new() { ["CLAUDE_CODE_USE_BEDROCK"] = "1", ["AWS_REGION"] = "eu-west-1" },
            },
            machine: new() { ["ANTHROPIC_API_KEY"] = "personal-key" });

        var acme = ProviderAuthEnvironment.Resolve(AiProvider.Anthropic, "acme", source);
        acme.FromWorkspace.Should().BeTrue();
        acme.Variables.Keys.Should().BeEquivalentTo("CLAUDE_CODE_USE_BEDROCK", "AWS_REGION");
        acme.Variables.Should().NotContainKey("ANTHROPIC_API_KEY", "a job on the company's Bedrock never inherits a personal key");

        var other = ProviderAuthEnvironment.Resolve(AiProvider.Anthropic, "personal", source);
        other.FromWorkspace.Should().BeFalse();
        other.Variables.Keys.Should().BeEquivalentTo(["ANTHROPIC_API_KEY"], "another workspace never sees acme's values");
    }

    [Fact]
    public void Workspace_LoginPin_PassesNothingEvenWithMachineKeys()
    {
        var source = new FakeSource(
            workspaces: new() { ["personal"] = new() { [ProviderAuthEnvironment.ClaudeModeKey] = "login" } },
            machine: new() { ["ANTHROPIC_API_KEY"] = "company-key", ["CLAUDE_CODE_OAUTH_TOKEN"] = "token" });

        var auth = ProviderAuthEnvironment.Resolve(AiProvider.Anthropic, "personal", source);

        auth.LoginOnly.Should().BeTrue();
        auth.Variables.Should().BeEmpty();
    }

    [Fact]
    public void Workspace_OverrideForOneProvider_LeavesTheOthersOnTheMachine()
    {
        var source = new FakeSource(
            workspaces: new() { ["acme"] = new() { ["ANTHROPIC_API_KEY"] = "acme-key" } },
            machine: new() { ["GH_TOKEN"] = "machine-token" });

        ProviderAuthEnvironment.Resolve(AiProvider.GitHubCopilot, "acme", source).Variables
            .Should().ContainKey("GH_TOKEN");
    }

    [Fact]
    public void BlankValues_AreNotPassed()
    {
        var auth = ProviderAuthEnvironment.Resolve(AiProvider.Anthropic, null,
            new FakeSource(machine: new() { ["ANTHROPIC_API_KEY"] = "   " }));

        auth.Variables.Should().BeEmpty();
    }

    [Fact]
    public void NoSource_PassesNothing()
    {
        var saved = ProviderAuthEnvironment.Source;
        try
        {
            ProviderAuthEnvironment.Source = null;
            ProviderAuthEnvironment.Resolve(AiProvider.Anthropic, "acme").Should().BeSameAs(ProviderAuthResolution.None);
        }
        finally
        {
            ProviderAuthEnvironment.Source = saved;
        }
    }

    [Fact]
    public void Resolution_ToString_NeverPrintsAValue()
    {
        var auth = ProviderAuthEnvironment.Resolve(AiProvider.Anthropic, null,
            new FakeSource(machine: new() { ["ANTHROPIC_API_KEY"] = "sk-ant-very-secret" }));

        auth.ToString().Should().Contain("ANTHROPIC_API_KEY").And.NotContain("sk-ant-very-secret");
    }

    [Fact]
    public void ParseCodexEnvKeys_ReadsProviderEnvKeys_AndDropsForbiddenNames()
    {
        const string toml = """
            model_provider = "azure"

            [model_providers.azure]
            name = "Azure"
            base_url = "https://acme.openai.azure.com/openai"
            env_key = "AZURE_OPENAI_API_KEY"

            [model_providers.gw]
            env_key = 'ACME_GATEWAY_KEY'

            [model_providers.sneaky]
            env_key = "GITHUB_TOKEN"

            [model_providers.sneaky2]
            env_key = "AWS_SECRET_ACCESS_KEY"
            """;

        ProviderAuthEnvironment.ParseCodexEnvKeys(toml)
            .Should().BeEquivalentTo("AZURE_OPENAI_API_KEY", "ACME_GATEWAY_KEY");
    }

    [Theory]
    [InlineData("ANTHROPIC_API_KEY", true)]
    [InlineData("AWS_REGION", false)]
    [InlineData("ANTHROPIC_BASE_URL", false)]
    [InlineData("GH_HOST", false)]
    [InlineData("GH_TOKEN", true)]
    [InlineData("SOME_CONFIG_ENV_KEY", true)]
    public void IsSecret_ClassifiesNames(string name, bool secret)
        => ProviderAuthEnvironment.IsSecret(name).Should().Be(secret);

    [Fact]
    public void Codex_ShellEnvironmentPolicy_ExcludesEveryPassedVariable()
    {
        var auth = ProviderAuthEnvironment.Resolve(AiProvider.OpenAI, null,
            new FakeSource(machine: new() { ["CODEX_API_KEY"] = "k", ["AZURE_OPENAI_API_KEY"] = "a" }));

        var args = CodexAgentInvoker.ShellEnvironmentPolicyArgs(auth);

        args.Should().Equal(
            "-c", "shell_environment_policy.ignore_default_excludes=false",
            "-c", "shell_environment_policy.exclude=['AZURE_OPENAI_API_KEY','CODEX_API_KEY']");
        string.Join(' ', args).Should().NotContain("\"").And.NotContain(" '", "nothing a cmd /c shim could mangle");
    }

    [Fact]
    public void Codex_SubscriptionRun_KeepsItsEnvironmentPolicy()
        => CodexAgentInvoker.ShellEnvironmentPolicyArgs(ProviderAuthResolution.None).Should().BeEmpty();

    [Fact]
    public void Copilot_SecretEnvVars_NamesOnlyTheCredentials()
    {
        var auth = ProviderAuthEnvironment.Resolve(AiProvider.GitHubCopilot, null,
            new FakeSource(machine: new() { ["GH_TOKEN"] = "t", ["GH_HOST"] = "acme.ghe.com" }));

        CopilotAgentInvoker.SecretEnvVarArgs(auth).Should().Equal("--secret-env-vars=GH_TOKEN");
        CopilotAgentInvoker.SecretEnvVarArgs(ProviderAuthResolution.None).Should().BeEmpty();
    }
}
