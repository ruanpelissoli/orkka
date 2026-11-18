namespace Orkka.Tests.Worker;

using System.Text.Json;
using FluentAssertions;
using ih0.Claude.Agent.SDK.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// The guards that hold on the developer's machine beyond the shell: reads never reach their
/// credentials or another workspace, Codex and Copilot carry the same command rules, unguarded
/// CLIs need the developer's own opt-in, and the stream that leaves the machine carries no code.
/// </summary>
public sealed class DeveloperMachineGuardTests
{
    private static readonly string Home = DeveloperSecrets.UserHome();
    private static readonly string OrkkaHome = DeveloperSecrets.OrkkaHome();
    private static readonly string Worktree = OrkkaHome + "/repos/01HORG/01HREPO/worktrees/SHOP-12";

    private static PreToolUseHookInput ToolInput(string tool, object input) => new()
    {
        SessionId = "s",
        TranscriptPath = "/tmp/t",
        Cwd = Worktree,
        ToolName = tool,
        ToolInput = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(input)),
    };

    private static async Task<string> ReadDecisionAsync(string tool, object input)
    {
        var guard = new AgentSensitivePathGuard(Worktree, NullLogger.Instance);
        var output = await guard.Evaluate(ToolInput(tool, input), toolUseId: null, context: null);
        return output.HookSpecificOutput?.GetRawText() ?? "";
    }

    // ── Read / Glob / Grep ────────────────────────────────────────────────

    public static TheoryData<string, object> ProtectedReads() => new()
    {
        { "Read", new { file_path = "~/.ssh/id_ed25519" } },
        { "Read", new { file_path = Home + "/.aws/credentials" } },
        { "Read", new { file_path = Home + "/.config/gh/hosts.yml" } },
        { "Read", new { file_path = Home + "/.npmrc" } },
        { "Read", new { file_path = Home + "/.claude/.credentials.json" } },
        { "Read", new { file_path = Home + "/.codex/auth.json" } },
        { "Read", new { file_path = "/etc/shadow" } },
        { "Read", new { file_path = OrkkaHome + "/workspaces/default/profile.env" } },
        // Another task, another workspace: a second employer's code.
        { "Read", new { file_path = OrkkaHome + "/repos/01HOTHER/01HREPO/worktrees/ACME-3/src/app.ts" } },
        { "Read", new { file_path = "../SHOP-13/src/app.ts" } },
        { "Grep", new { pattern = "token", path = Home + "/.config/gh" } },
        { "Glob", new { pattern = Home + "/.ssh/*" } },
        { "LS", new { path = OrkkaHome + "/workspaces" } },
    };

    [Theory]
    [MemberData(nameof(ProtectedReads))]
    public async Task ProtectedLocations_AreDenied(string tool, object input)
    {
        (await ReadDecisionAsync(tool, input)).Should().Contain("deny");
    }

    public static TheoryData<string, object> OrdinaryReads() => new()
    {
        { "Read", new { file_path = "src/app.ts" } },
        { "Read", new { file_path = Worktree + "/package.json" } },
        { "Read", new { file_path = ".orkka/context/attachments/spec.pdf" } },
        // A repository's own .npmrc / .azure folder is the repo, not the developer's sign-in.
        { "Read", new { file_path = ".npmrc" } },
        { "Read", new { file_path = ".azure/config.json" } },
        // Package caches: how an agent sees the API it is coding against.
        { "Read", new { file_path = Home + "/.nuget/packages/newtonsoft.json/13.0.3/README.md" } },
        { "Read", new { file_path = Home + "/go/pkg/mod/github.com/gin-gonic/gin@v1.10.0/gin.go" } },
        { "Grep", new { pattern = "TODO", path = "src" } },
        { "Glob", new { pattern = "src/**/*.ts" } },
        { "Grep", new { pattern = "^import" } },
    };

    [Theory]
    [MemberData(nameof(OrdinaryReads))]
    public async Task OrdinaryReads_AreAllowed(string tool, object input)
    {
        (await ReadDecisionAsync(tool, input)).Should().Contain("allow");
    }

    // ── Codex execpolicy ──────────────────────────────────────────────────

    [Fact]
    public void CodexRules_ForbidTheGuardsPrefixes_AndStayOutOfGit()
    {
        var dir = Directory.CreateTempSubdirectory("orkka-codex-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git", "info"));
            File.WriteAllText(Path.Combine(dir, ".git", "info", "exclude"), "# git ls-files --others --exclude-from=.git/info/exclude");

            CodexExecPolicy.WriteInto(dir);
            CodexExecPolicy.WriteInto(dir); // idempotent

            var rules = File.ReadAllText(Path.Combine(dir, ".codex", "rules", "orkka.rules"));
            rules.Should().Contain("prefix_rule(pattern = [\"git\", \"push\"], decision = \"forbidden\"");
            rules.Should().Contain("prefix_rule(pattern = [\"gh\"], decision = \"forbidden\"");

            var exclude = File.ReadAllLines(Path.Combine(dir, ".git", "info", "exclude"));
            exclude.Count(l => l == "/" + CodexExecPolicy.RulesPath).Should().Be(1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CodexRules_InALinkedWorktree_GoToTheCommonExclude()
    {
        var root = Directory.CreateTempSubdirectory("orkka-codex-linked-").FullName;
        try
        {
            var common = Path.Combine(root, "bare");
            var gitDir = Path.Combine(common, "worktrees", "SHOP-12");
            Directory.CreateDirectory(gitDir);
            var tree = Path.Combine(root, "SHOP-12");
            Directory.CreateDirectory(tree);
            File.WriteAllText(Path.Combine(tree, ".git"), "gitdir: " + gitDir);

            CodexExecPolicy.WriteInto(tree);

            File.ReadAllText(Path.Combine(common, "info", "exclude")).Should().Contain("/" + CodexExecPolicy.RulesPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── Unguarded CLIs need the developer's own opt-in ───────────────────

    private static ProviderDetection Detection(Dictionary<string, string> runnerEnv, string home) => new(
        NullLogger<ProviderDetection>.Instance,
        probes: new ProviderDetectionProbes
        {
            ReadRunnerEnv = key => runnerEnv.GetValueOrDefault(key),
            CliOnPath = _ => true,
            // Hermetic: never spawn the machine's real CLIs (`--version`, `codex login status`).
            CliIsStub = _ => false,
            CliVersion = _ => null,
            CodexLoginStatus = () => null,
            Home = () => home,
            GhLoggedIn = () => false,
        });

    [Fact]
    public void UnguardedCli_IsNotReady_UntilTheDeveloperOptsInOnThisMachine()
    {
        var home = Directory.CreateTempSubdirectory("orkka-home-").FullName;
        try
        {
            // Grok's probe fails open (its token may live in the OS store), so it reads as signed in.
            var off = Detection([], home).Detect(RunnerProviders.Grok);
            off.Authenticated.Should().BeFalse();
            off.Detail.Should().Contain(ProviderDetection.AllowUnguardedKey);

            var on = Detection(new() { [ProviderDetection.AllowUnguardedKey] = "gemini, grok" }, home)
                .Detect(RunnerProviders.Grok);
            on.Authenticated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // ── What leaves the machine ───────────────────────────────────────────

    [Fact]
    public void ToolResults_NeverLeaveTheMachine()
    {
        RunnerChunkPrivacy.ForHost(new AgentChunk(AgentChunkType.ToolResult, "export const secret = 1;"))
            .Should().BeNull();
    }

    [Fact]
    public void ToolCalls_LeaveWithoutTheirFileBodies()
    {
        var write = new AgentChunk(AgentChunkType.ToolUse,
            "Write: {\"file_path\":\"src/pricing.ts\",\"content\":\"export const margin = 0.42;\"}");
        var edit = new AgentChunk(AgentChunkType.ToolUse,
            "Edit: {\"file_path\":\"src/a.ts\",\"old_string\":\"a\",\"new_string\":\"b\"}");
        var bash = new AgentChunk(AgentChunkType.ToolUse, "Bash: {\"command\":\"npm test\"}");

        var w = RunnerChunkPrivacy.ForHost(write)!.Content;
        w.Should().Contain("src/pricing.ts").And.NotContain("margin").And.Contain("kept on the developer's machine");

        RunnerChunkPrivacy.ForHost(edit)!.Content.Should().Contain("src/a.ts").And.NotContain("\"b\"");
        RunnerChunkPrivacy.ForHost(bash)!.Content.Should().Be("Bash: {\"command\":\"npm test\"}");
    }

    [Fact]
    public void OtherChunks_PassUnchanged()
    {
        var text = new AgentChunk(AgentChunkType.Text, "Done — added the export.");
        RunnerChunkPrivacy.ForHost(text).Should().Be(text);
    }
}
