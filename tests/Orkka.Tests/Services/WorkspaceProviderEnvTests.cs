namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Cli;
using Orkka.LocalRunner.Trackers;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// A developer with several jobs: each workspace can carry its own AI sign-in. Secrets go to the
/// secret store (never profile.env, never the console), plain settings to profile.env, only
/// allowlisted names are accepted, and one workspace's values never reach another's runs.
/// </summary>
[Collection("WorkspaceCommandReadValue")]
public sealed class WorkspaceProviderEnvTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkka-wsenv-" + Guid.NewGuid().ToString("N"));
    private readonly string _storeRoot;
    private readonly Func<string, bool, string?> _readBefore = WorkspaceCommand.ReadValue;

    public WorkspaceProviderEnvTests()
    {
        _storeRoot = Path.Combine(_root, "store");
        foreach (var ws in new[] { "acme", "personal" })
        {
            var path = WorkspaceProfiles.ProfilePath(_root, ws);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "# profile\nORKKA_HOSTED_URL=http://127.0.0.1:6750\nORKKA_GIT_AUTHOR_NAME=Dana\nORKKA_GIT_AUTHOR_EMAIL=dana@acme.com\n");
        }
    }

    public void Dispose()
    {
        WorkspaceCommand.ReadValue = _readBefore;
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private IWorkspaceSecretStore Store(string workspace) => new FileSecretStore(Path.Combine(_storeRoot, workspace));

    private (int Code, string Out, string Err) Cli(params string[] args)
    {
        var @out = new StringWriter();
        var err = new StringWriter();
        var code = WorkspaceCommand.Run(args, _root, @out, err, Store);
        return (code, @out.ToString(), err.ToString());
    }

    private string Profile(string ws) => File.ReadAllText(WorkspaceProfiles.ProfilePath(_root, ws));

    [Fact]
    public void SetEnv_Secret_GoesToTheStore_NeverTheFileOrTheConsole()
    {
        WorkspaceCommand.ReadValue = (_, secret) => secret ? "sk-ant-acme-secret" : throw new InvalidOperationException("must prompt hidden");

        var (code, output, _) = Cli("set-env", "acme", "ANTHROPIC_API_KEY");

        code.Should().Be(0);
        output.Should().Contain("ANTHROPIC_API_KEY").And.NotContain("sk-ant-acme-secret");
        Profile("acme").Should().NotContain("sk-ant-acme-secret")
            .And.Contain($"{WorkspaceProviderEnv.SecretNamesKey}=ANTHROPIC_API_KEY");
        Store("acme").Get(WorkspaceProviderEnv.SecretId("ANTHROPIC_API_KEY")).Should().Be("sk-ant-acme-secret");
        // The connection lines survive the rewrite.
        Profile("acme").Should().Contain("ORKKA_GIT_AUTHOR_EMAIL=dana@acme.com");
    }

    [Fact]
    public void SetEnv_RefusesASecretOnTheCommandLine()
    {
        var (code, _, err) = Cli("set-env", "acme", "ANTHROPIC_API_KEY=sk-ant-inline");

        code.Should().Be(1);
        err.Should().Contain("never put it on the command line").And.NotContain("sk-ant-inline");
        Profile("acme").Should().NotContain("sk-ant-inline");
    }

    [Fact]
    public void SetEnv_Plain_GoesToProfileEnv()
    {
        Cli("set-env", "acme", "CLAUDE_CODE_USE_BEDROCK=1").Code.Should().Be(0);
        Cli("set-env", "acme", "AWS_REGION=us-east-1").Code.Should().Be(0);

        Profile("acme").Should().Contain("CLAUDE_CODE_USE_BEDROCK=1").And.Contain("AWS_REGION=us-east-1");
        var (_, shown, _) = Cli("env", "acme");
        shown.Should().Contain("AWS_REGION=us-east-1");
    }

    [Theory]
    [InlineData("DATABASE_URL=postgres://x")]
    [InlineData("PATH=/tmp")]
    [InlineData("STRIPE_SECRET_KEY")]
    [InlineData("ORKKA_HOSTED_URL=http://evil")]
    public void SetEnv_RefusesNamesOnNoAllowlist(string spec)
    {
        WorkspaceCommand.ReadValue = (_, _) => "x";
        var (code, _, err) = Cli("set-env", "acme", spec);

        code.Should().Be(1);
        err.Should().Contain("not an AI sign-in variable");
    }

    [Fact]
    public void SetEnv_ModePin_TakesOnlyLogin()
    {
        Cli("set-env", "personal", "ORKKA_CLAUDE_AUTH=apikey").Code.Should().Be(1);
        Cli("set-env", "personal", "ORKKA_CLAUDE_AUTH=login").Code.Should().Be(0);
        Profile("personal").Should().Contain("ORKKA_CLAUDE_AUTH=login");
    }

    [Fact]
    public void Env_ShowsSecretNamesOnly()
    {
        WorkspaceCommand.ReadValue = (_, _) => "github_pat_hidden";
        Cli("set-env", "acme", "GH_TOKEN");

        var (_, shown, _) = Cli("env", "acme");

        shown.Should().Contain("GH_TOKEN").And.Contain("secret store").And.NotContain("github_pat_hidden");
    }

    [Fact]
    public void UnsetEnv_RemovesTheStoreEntryAndTheName()
    {
        WorkspaceCommand.ReadValue = (_, _) => "k";
        Cli("set-env", "acme", "CODEX_API_KEY");

        Cli("unset-env", "acme", "CODEX_API_KEY").Code.Should().Be(0);

        Store("acme").Get(WorkspaceProviderEnv.SecretId("CODEX_API_KEY")).Should().BeNull();
        Profile("acme").Should().NotContain("CODEX_API_KEY");
    }

    [Fact]
    public void DeleteAllSecrets_ClearsTheWorkspacesStoreEntries()
    {
        WorkspaceCommand.ReadValue = (_, _) => "v";
        Cli("set-env", "acme", "ANTHROPIC_API_KEY");
        Cli("set-env", "acme", "GH_TOKEN");
        var removed = new List<string>();

        WorkspaceProviderEnv.DeleteAllSecrets(_root, "acme", TextWriter.Null, Store("acme"), removed.Add);

        removed.Should().BeEquivalentTo("orkka/acme/ai/ANTHROPIC_API_KEY", "orkka/acme/ai/GH_TOKEN");
        Store("acme").Get(WorkspaceProviderEnv.SecretId("GH_TOKEN")).Should().BeNull();
    }

    [Fact]
    public void RunnerSource_WorkspaceValuesWin_AndNeverLeakAcrossWorkspaces()
    {
        WorkspaceCommand.ReadValue = (_, _) => "acme-bedrock-key";
        Cli("set-env", "acme", "CLAUDE_CODE_USE_BEDROCK=1");
        Cli("set-env", "acme", "AWS_BEARER_TOKEN_BEDROCK");

        var source = new RunnerProviderAuthSource(_root,
            startupEnvironment: new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "machine-key" },
            readRunnerEnv: _ => null,
            secretStores: Store);

        var acme = ProviderAuthEnvironment.Resolve(AiProvider.Anthropic, "acme", source);
        acme.Variables.Should().Contain(new KeyValuePair<string, string>("AWS_BEARER_TOKEN_BEDROCK", "acme-bedrock-key"))
            .And.ContainKey("CLAUDE_CODE_USE_BEDROCK")
            .And.NotContainKey("ANTHROPIC_API_KEY");

        var personal = ProviderAuthEnvironment.Resolve(AiProvider.Anthropic, "personal", source);
        personal.Variables.Should().ContainKey("ANTHROPIC_API_KEY")
            .And.NotContainKey("AWS_BEARER_TOKEN_BEDROCK").And.NotContainKey("CLAUDE_CODE_USE_BEDROCK");
    }

    [Fact]
    public void RunnerSource_RunnerEnvFile_OutranksTheStartupEnvironment()
    {
        var source = new RunnerProviderAuthSource(_root,
            startupEnvironment: new Dictionary<string, string> { ["GH_TOKEN"] = "from-shell" },
            readRunnerEnv: name => name == "GH_TOKEN" ? "from-runner-env" : null,
            secretStores: Store);

        ProviderAuthEnvironment.Resolve(AiProvider.GitHubCopilot, null, source).Variables["GH_TOKEN"]
            .Should().Be("from-runner-env");
    }

    [Fact]
    public void CaptureStartupEnvironment_KeepsOnlyAllowlistedNames()
    {
        var env = new System.Collections.Hashtable
        {
            ["ANTHROPIC_API_KEY"] = "k",
            ["CLAUDE_CODE_USE_BEDROCK"] = "1",
            ["DATABASE_URL"] = "postgres://u:p@h/db",
            ["STRIPE_SECRET_KEY"] = "sk_live",
            ["PATH"] = "/usr/bin",
            ["GH_TOKEN"] = "",
        };

        RunnerProviderAuthSource.CaptureStartupEnvironment(env).Keys
            .Should().BeEquivalentTo("ANTHROPIC_API_KEY", "CLAUDE_CODE_USE_BEDROCK");
    }

    [Fact]
    public void ProfileRewrite_ByTheConnectionCode_KeepsTheAiLines()
    {
        Cli("set-env", "acme", "AWS_REGION=eu-west-1").Code.Should().Be(0);

        WorkspaceProfiles.Save(_root, new WorkspaceProfile
        {
            Name = "acme",
            HostUrl = "http://127.0.0.1:6750",
            Token = "orkr_test",
            GitAuthorName = "Dana",
            GitAuthorEmail = "dana@acme.com",
        }, Store("acme"));

        Profile("acme").Should().Contain("AWS_REGION=eu-west-1");
    }
}
