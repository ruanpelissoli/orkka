namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Ai;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The whole general quality instruction in a developing run used to be one sentence —
/// "write clean, production-quality code" — and "Definition of Done" was the agent's own word.
/// These pin what replaced it: the engineering bar reaching agents whose persona predates it,
/// the self-review, the craft-skill pointer, and the check-gate notice that only appears when
/// the repo actually has a CheckCommand to run.
/// </summary>
public sealed class DevelopingPromptTests
{
    private static TaskSnapshot MakeTask(int reviewCycle = 0, int checkCycle = 0) => new(
        Id: Ulid.NewUlid(), OrgId: Ulid.NewUlid(), ProjectId: Ulid.NewUlid(), RepoId: Ulid.NewUlid(),
        Number: 7, Title: "Active bets endpoint", Description: "GET /v1/bets/active",
        Priority: Priority.High, Type: TaskType.Feature,
        CurrentLane: Lane.Developing, Status: TaskStatus.Active, IsArchived: false,
        RequesterLocale: null, TotalTokensUsed: 0, HasAgentWorking: true,
        BranchName: "orkka/TAP-13", PrUrl: null, Tags: null,
        ContextFiles: null, ContextDocumentIds: null,
        SkipPlanning: false, SkipTesting: false, SkipCodeReview: false, IsMerged: false,
        ReviewCycleCount: reviewCycle, QaCycleCount: 0, QuestionSourceLane: null,
        AssignedAgentId: null, CurrentAgentId: null, SourceMessageId: null,
        StartedAt: null, CompletedAt: null, CheckCycleCount: checkCycle);

    private static ProjectSummary MakeProject() => new(
        Ulid.NewUlid(), Ulid.NewUlid(), "Tapago", "tapago", "TAP", Settings: null);

    private static RepoSummary MakeRepo(string? checkCommand) => new(
        Id: Ulid.NewUlid(), ProjectId: Ulid.NewUlid(), OrgId: Ulid.NewUlid(),
        Name: "api", GitUrl: "https://github.com/acme/api.git", DefaultBranch: "main",
        CardColor: "#000000", WorkspaceStatus: WorkspaceStatus.Ready,
        SetupCommand: null, QaRunCommand: null, BuildCommand: null, CheckCommand: checkCommand);

    /// <summary>An agent seeded before the engineering bar existed — its stored persona has
    /// no marker, which is the case the runtime injection exists for.</summary>
    private static AgentSummary LegacyBackendDev() => new(
        Ulid.NewUlid(), Ulid.NewUlid(), Ulid.NewUlid(), [],
        "Atlas", AgentRole.BackendDev, "claude-opus-5",
        "You are Atlas, a Senior Backend Developer.", "");

    /// <summary>A freshly seeded agent — its persona already carries every bar.</summary>
    private static AgentSummary FreshFrontendDev() => new(
        Ulid.NewUlid(), Ulid.NewUlid(), Ulid.NewUlid(), [],
        "Pixel", AgentRole.FrontendDev, "claude-opus-5",
        DefaultPrompts.FrontendDev, "");

    private static (string System, string User) Build(AgentSummary agent, RepoSummary? repo, TaskSnapshot? task = null)
        => TaskOrchestrator.BuildDevelopingPrompt(
            task ?? MakeTask(), MakeProject(), repo, agent, memories: "", plan: "{}",
            contextFilesSection: null, conversationSection: null);

    [Fact]
    public void ATrackerTask_NamesItsTicket_EpicAndFolder()
    {
        var task = MakeTask() with
        {
            Description = "Export invoices.\n\n## Attachments\n\n- mock.png",
            ExternalKey = "SHOP-12",
            ExternalUrl = "https://acme.atlassian.net/browse/SHOP-12",
            ExternalEpic = "SHOP-1 Checkout",
            WorkingSubpath = "packages/api",
        };

        var (_, user) = Build(FreshFrontendDev(), MakeRepo(null), task);

        user.Should().Contain("Ticket: SHOP-12 (https://acme.atlassian.net/browse/SHOP-12), under epic SHOP-1 Checkout")
            .And.Contain("Prefix the commit message with SHOP-12")
            .And.Contain("Work inside `packages/api`")
            .And.Contain(".orkka/context/attachments/");
    }

    [Fact]
    public void ABoardTask_HasNoTicketLines()
    {
        var (_, user) = Build(FreshFrontendDev(), MakeRepo(null));

        user.Should().NotContain("Ticket:").And.NotContain("Work inside");
    }

    [Fact]
    public void LegacyPersona_GetsTheEngineeringBarInjectedOnce_BeforeTeamInstructions()
    {
        var agent = LegacyBackendDev() with { UserInstructions = "Always use MediatR." };
        var (system, _) = Build(agent, MakeRepo(null));

        var bar = system.IndexOf(SharedPromptBlocks.EngineeringBarMarker, StringComparison.Ordinal);
        var team = system.IndexOf("## Additional instructions from your team", StringComparison.Ordinal);

        bar.Should().BePositive();
        team.Should().BeGreaterThan(bar, "the bar is a default the team's instructions must be able to override");
        system.Split(SharedPromptBlocks.EngineeringBarMarker).Length.Should().Be(2, "one copy, never two");
    }

    [Fact]
    public void FreshPersona_IsNotGivenASecondBar()
    {
        var (system, _) = Build(FreshFrontendDev(), MakeRepo(null));

        system.Split(SharedPromptBlocks.EngineeringBarMarker).Length.Should().Be(2);
        system.Split(SharedPromptBlocks.VisualBarMarker).Length.Should().Be(2);
        system.Split(SharedPromptBlocks.CodeCommentPolicyMarker).Length.Should().Be(2);
    }

    [Fact]
    public void AssignmentAsksForSelfReviewAndTheCraftSkills()
    {
        var (system, _) = Build(LegacyBackendDev(), MakeRepo(null));

        system.Should().Contain("## Self-review before you finish");
        system.Should().Contain("git diff");
        system.Should().Contain("code-craft");
        system.Should().Contain("Existing code to reuse");
        // The no-visual-proof rule survives verbatim: the dev lane never launches the app.
        system.Should().Contain("Do NOT launch the application or game");
        system.Should().NotContain("Write clean, production-quality code.");
    }

    [Fact]
    public void CheckNotice_AppearsOnlyWhenTheRepoHasACheckCommand()
    {
        var (withCheck, _) = Build(LegacyBackendDev(), MakeRepo("npm run lint && npm test"));
        var (without, _) = Build(LegacyBackendDev(), MakeRepo(null));
        var (noRepo, _) = Build(LegacyBackendDev(), repo: null);

        withCheck.Should().Contain("the platform executes `npm run lint && npm test` in this worktree");
        without.Should().NotContain("the platform executes");
        noRepo.Should().NotContain("the platform executes");
    }

    [Fact]
    public void AfterAFailedCheck_TheUserMessageSaysSo()
    {
        var (_, user) = Build(LegacyBackendDev(), MakeRepo("dotnet test"), MakeTask(checkCycle: 1));
        var (_, firstPass) = Build(LegacyBackendDev(), MakeRepo("dotnet test"), MakeTask(checkCycle: 0));

        user.Should().Contain("The automated check failed after your previous pass");
        user.Should().Contain("run `dotnet test` yourself");
        firstPass.Should().NotContain("The automated check failed");
    }

    [Fact]
    public void FormatCheckFailure_KeepsTheTail_NotTheHead()
    {
        var banner = new string('H', 7_000);
        var output = banner + "\nFAIL src/bets.test.ts > returns active bets";

        var brief = TaskOrchestrator.FormatCheckFailure("npm test", 1, output);

        brief.Should().StartWith("## Automated checks failed");
        brief.Should().Contain("`npm test` exited 1");
        brief.Should().Contain("FAIL src/bets.test.ts");
        brief.Should().Contain("…", because: "a truncated head is marked");
        brief.Length.Should().BeLessThan(7_000, "the head is banner noise; the tail is where the runner says what broke");
    }

    // Budget guard: persona + bar + team instructions + memories must stay clear of the
    // ClaudeAgentInvoker warning threshold (26k) on the largest realistic shape.
    [Fact]
    public void WorstCaseDevelopingPrompt_StaysUnderTheCommandLineWarningThreshold()
    {
        var agent = FreshFrontendDev() with { UserInstructions = new string('i', 4_000) };
        var memories = "## Project memory\n" + new string('m', 4_000);
        var (system, _) = TaskOrchestrator.BuildDevelopingPrompt(
            MakeTask(), MakeProject(), MakeRepo("npm run lint && npx tsc --noEmit && npm test"), agent,
            memories, plan: "{}", contextFilesSection: null, conversationSection: null);

        system.Length.Should().BeLessThan(26_000);
    }
}
