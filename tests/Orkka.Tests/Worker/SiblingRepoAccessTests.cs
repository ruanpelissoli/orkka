namespace Orkka.Tests.Worker;

using System.Text.Json;
using FluentAssertions;
using ih0.Claude.Agent.SDK.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.LocalRunner;
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Execution.Agents;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Projects.Contracts;

/// <summary>
/// Agents on a folder-of-repos project may READ the sibling repositories for reference — never
/// their dotenv files, never the developer's own checkout of the repo they work on, and never
/// anything inside the Orkka home.
/// </summary>
public sealed class SiblingRepoAccessTests
{
    private static readonly string OrkkaHome = DeveloperSecrets.OrkkaHome();
    private static readonly string Worktree = OrkkaHome + "/repos/01HORG/01HREPO/worktrees/SHOP-12";
    private const string BaseFolder = "C:/work/shop";

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

    // ── Read guard: dotenv outside the worktree ─────────────────────────

    [Theory]
    [InlineData(BaseFolder + "/web/.env")]
    [InlineData(BaseFolder + "/web/.env.local")]
    [InlineData(BaseFolder + "/api/config/.env.production")]
    [InlineData(BaseFolder + "/.env")]
    [InlineData("../web/.env")]
    public async Task DotenvOutsideTheWorktree_IsDenied(string path)
    {
        (await ReadDecisionAsync("Read", new { file_path = path })).Should().Contain("deny");
    }

    [Theory]
    [InlineData(BaseFolder + "/web/.env.example")]
    [InlineData(BaseFolder + "/web/.env.sample")]
    [InlineData(BaseFolder + "/web/.env.template")]
    [InlineData(BaseFolder + "/web/src/app.ts")]
    [InlineData(BaseFolder + "/web/.environment/notes.md")]
    public async Task SiblingSourcesAndEnvTemplates_AreReadable(string path)
    {
        (await ReadDecisionAsync("Read", new { file_path = path })).Should().NotContain("deny");
    }

    [Fact]
    public async Task DotenvInsideTheWorktree_IsLeftToTheBashGuardAndTheRepo()
    {
        // The read guard's dotenv rule is about OTHER folders; the task's own worktree is its
        // repository (the Bash guard still refuses shell reads of .env everywhere).
        (await ReadDecisionAsync("Read", new { file_path = Worktree + "/.env" })).Should().NotContain("deny");
    }

    [Theory]
    [InlineData("C:/work/shop/web/.env", true)]
    [InlineData("C:/work/shop/web/.env.development.local", true)]
    [InlineData("C:/work/shop/web/.env.example", false)]
    [InlineData("C:/work/shop/web/.envrc", false)]
    [InlineData("C:/work/shop/web/env", false)]
    public void IsDotenvFile(string path, bool expected) =>
        DeveloperSecrets.IsDotenvFile(path).Should().Be(expected);

    // ── Bash guard: shell reads of a sibling's dotenv ───────────────────

    [Theory]
    [InlineData("cat ../web/.env")]
    [InlineData("cat C:/work/shop/web/.env.local")]
    [InlineData("type ..\\web\\.env")]
    [InlineData("grep API_KEY ../web/.env")]
    [InlineData("cp ../web/.env .")]
    public void ShellReadOfASiblingDotenv_IsBlocked(string command) =>
        AgentBashGuard.IsBlockedCommand(command.ToLowerInvariant(), Worktree).Should().BeTrue();

    [Theory]
    [InlineData("cat ../web/.env.example")]
    [InlineData("cat C:/work/shop/web/package.json")]
    [InlineData("grep -r useAuth C:/work/shop/web/src")]
    public void ShellReadOfSiblingSources_IsAllowed(string command) =>
        AgentBashGuard.IsBlockedCommand(command.ToLowerInvariant(), Worktree).Should().BeFalse();

    // ── Runner: what the host may widen reads to ────────────────────────

    [Fact]
    public void ReadOnlyDirs_KeepRealFolders_DropRelativeMissingAndOrkkaHomePaths()
    {
        var real = Path.Combine(Path.GetTempPath(), $"orkka-readonly-{Ulid.NewUlid()}");
        Directory.CreateDirectory(real);
        try
        {
            var kept = RunnerOperations.ExistingDirs(
                [real, "relative/dir", Path.Combine(real, "missing"), DeveloperSecrets.OrkkaHome(),
                 Path.GetPathRoot(real)!]);

            kept.Should().BeEquivalentTo([Path.TrimEndingDirectorySeparator(Path.GetFullPath(real))]);
            RunnerOperations.ExistingDirs([]).Should().BeNull();
        }
        finally
        {
            Directory.Delete(real);
        }
    }

    // ── Prompts ─────────────────────────────────────────────────────────

    private static readonly ProjectSummary MultiRepoProject = new(
        Ulid.NewUlid(), Ulid.NewUlid(), "Shop", "shop", "SHOP", null, @"C:\work\shop", BaseFolderShape.MultiRepo);

    private static RepoSummary Repo(string name, string folder, string? description = null, DateTimeOffset? missing = null) => new(
        Ulid.NewUlid(), MultiRepoProject.Id, MultiRepoProject.OrgId, name, $"https://github.com/acme/{name}.git", "main",
        "#000", WorkspaceStatus.Ready, FolderPath: folder, Description: description, MissingSince: missing,
        LocalCheckoutPath: BaseFolders.CheckoutPath(MultiRepoProject.BaseFolder, folder));

    [Fact]
    public void SiblingSection_ListsTheOtherRepos_AndForbidsTheDevelopersCheckout()
    {
        var api = Repo("api", "api", "Orders HTTP API");
        var web = Repo("web", "web", "Next.js storefront");
        var gone = Repo("old", "old", missing: DateTimeOffset.UtcNow);

        var section = TaskOrchestrator.BuildSiblingReposSection(MultiRepoProject, api, [api, web, gone]);

        section.Should().Contain("Sibling repositories (read-only reference)");
        section.Should().Contain(@"**web** — `C:\work\shop\web` — Next.js storefront");
        section.Should().NotContain("**api**").And.NotContain("**old**");
        section.Should().Contain(
            @"Your working copy is the current directory. `C:\work\shop\api` is the developer's own checkout — "
            + "never read from or edit it; read other repos only for reference.");
    }

    [Fact]
    public void SiblingSection_IsEmptyForAMonorepoOrAProjectWithoutABaseFolder()
    {
        var api = Repo("api", "api");
        var web = Repo("web", "web");

        TaskOrchestrator.BuildSiblingReposSection(
            MultiRepoProject with { BaseFolderShape = BaseFolderShape.Monorepo }, api, [api, web]).Should().BeEmpty();
        TaskOrchestrator.BuildSiblingReposSection(
            MultiRepoProject with { BaseFolder = null, BaseFolderShape = null }, api, [api, web]).Should().BeEmpty();
        TaskOrchestrator.BuildSiblingReposSection(MultiRepoProject, api, [api]).Should().BeEmpty();
    }

    [Fact]
    public void ChatPrompt_NamesTheBaseFolder_AndKeepsItReadOnly()
    {
        var section = SendMessageEndpoint.BaseFolderSection(MultiRepoProject);

        section.Should().Contain(@"`C:\work\shop`");
        section.Should().Contain("Never edit, create or delete anything there");
        SendMessageEndpoint.BaseFolderSection(MultiRepoProject with { BaseFolder = null }).Should().BeEmpty();
    }

    // ── Folder paths ────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "")]
    [InlineData("api", "api")]
    [InlineData("services\\billing", "services/billing")]
    [InlineData("/api/", null)]
    [InlineData("../api", null)]
    [InlineData("a/../b", null)]
    [InlineData("C:/work/api", null)]
    [InlineData("~/api", null)]
    public void FolderPath_IsRelativeForwardSlashedAndNeverClimbsOut(string input, string? expected) =>
        BaseFolders.NormalizeFolderPath(input).Should().Be(expected);

    [Fact]
    public void CheckoutPath_JoinsInTheBaseFoldersOwnStyle()
    {
        BaseFolders.CheckoutPath(@"C:\work\shop\", "services/billing").Should().Be(@"C:\work\shop\services\billing");
        BaseFolders.CheckoutPath("/home/dev/shop", "").Should().Be("/home/dev/shop");
        BaseFolders.CheckoutPath(null, "api").Should().BeNull();
    }

    [Theory]
    [InlineData(AgentPhase.Planning, true)]
    [InlineData(AgentPhase.Developing, false)]
    [InlineData(AgentPhase.Testing, false)]
    public void Copilot_GetsSiblingDirs_OnlyInPhasesThatCannotWrite(
        AgentPhase phase, bool granted)
    {
        var args = CopilotAgentInvoker.SiblingDirArgs(phase, ["C:/work/shop"]).ToList();

        if (granted)
            args.Should().Equal("--add-dir", "C:/work/shop");
        else
            args.Should().BeEmpty();
    }
}
