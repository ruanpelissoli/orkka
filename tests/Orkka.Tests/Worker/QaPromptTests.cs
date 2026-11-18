namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The QA lane has two procedures behind one verdict contract.
///
/// It was written around a browser — start the app, navigate, click, assert — which verifies
/// almost nothing on a repo that serves only a JSON API. Backend tasks got a Playwright run
/// pointed at endpoints returning JSON. An API repo now runs its own test suite and probes the
/// changed endpoints with curl instead, and the tests themselves are the dev agent's job.
/// </summary>
public sealed class QaPromptTests
{
    private const string RunCommand = "go run ./cmd/api";

    private static TaskSnapshot MakeTask() => new(
        Id: Ulid.NewUlid(), OrgId: Ulid.NewUlid(), ProjectId: Ulid.NewUlid(), RepoId: Ulid.NewUlid(),
        Number: 7, Title: "Active bets endpoint", Description: "GET /v1/bets/active",
        Priority: Priority.High, Type: TaskType.Feature,
        CurrentLane: Lane.Testing, Status: TaskStatus.Active, IsArchived: false,
        RequesterLocale: null, TotalTokensUsed: 0, HasAgentWorking: true,
        BranchName: "orkka/TAP-13", PrUrl: null, Tags: null,
        ContextFiles: null, ContextDocumentIds: null,
        SkipPlanning: false, SkipTesting: false, SkipCodeReview: false, IsMerged: false,
        ReviewCycleCount: 0, QaCycleCount: 0, QuestionSourceLane: null,
        AssignedAgentId: null, CurrentAgentId: null, SourceMessageId: null,
        StartedAt: null, CompletedAt: null);

    private static ProjectSummary MakeProject() => new(
        Ulid.NewUlid(), Ulid.NewUlid(), "Tapago", "tapago", "TAP", Settings: null);

    private static AgentSummary MakeAgent() => new(
        Ulid.NewUlid(), Ulid.NewUlid(), Ulid.NewUlid(), [],
        "Scout", AgentRole.QA, "claude-sonnet-4-6", "You are Scout.", "");

    private static string Qa(bool apiMode) => TaskOrchestrator.BuildQaSystemPrompt(
        MakeTask(), MakeProject(), MakeAgent(), memories: "", RunCommand, apiMode);

    // The browser lane used to steer away from screenshots entirely, so a homepage that
    // wrapped one word per line at every width passed QA on its accessibility tree alone.
    // A UI change is now checked at a phone and a desktop width, with the overflow probe
    // and one screenshot each.
    [Fact]
    public void BrowserMode_ChecksEveryChangedScreenAtPhoneAndDesktopWidths()
    {
        var prompt = Qa(apiMode: false);

        prompt.Should().Contain("browser_resize").And.Contain("375x812").And.Contain("1280x800");
        prompt.Should().Contain("scrollWidth > document.documentElement.clientWidth");
        prompt.Should().Contain("browser_take_screenshot");
        prompt.Should().Contain("one word per line");
    }

    [Fact]
    public void ApiMode_HasNoBrowserViewportStep()
        => Qa(apiMode: true).Should().NotContain("browser_resize");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothModes_KeepTheVerdictContractAndRunCommand(bool apiMode)
    {
        var prompt = Qa(apiMode);

        // The rest of the lane parses this shape — QaVerdict, the escalation on an unreadable
        // verdict, and the report written back into the conversation all depend on it.
        prompt.Should().Contain("\"decision\": \"pass\" | \"fail\"");
        prompt.Should().Contain("\"failures\"");
        prompt.Should().Contain(RunCommand);
        prompt.Should().Contain("test cycle 1 of");
        prompt.Should().Contain("You are Scout.");
    }

    [Fact]
    public void ApiMode_VerifiesOverHttpAndNeverMentionsABrowser()
    {
        var prompt = Qa(apiMode: true);

        prompt.Should().Contain("over HTTP");
        prompt.Should().Contain("curl");
        prompt.Should().Contain("test suite");
        prompt.Should().Contain("status codes AND response bodies");

        // The run gets no Playwright server, so naming those tools would teach the agent its
        // tools are broken.
        prompt.Should().NotContain("Playwright");
        prompt.Should().NotContain("browser_navigate");
    }

    [Fact]
    public void ApiMode_LeavesWritingTestsToTheDevAgent()
    {
        var prompt = Qa(apiMode: true);

        prompt.Should().Contain("writing tests is the dev agent's job");
        // Absent coverage is a reportable failure, not something QA silently backfills.
        prompt.Should().Contain("NO test coverage");
    }

    [Fact]
    public void BrowserMode_IsUnchanged()
    {
        var prompt = Qa(apiMode: false);

        prompt.Should().Contain("real browser");
        prompt.Should().Contain("browser_navigate");
        prompt.Should().NotContain("over HTTP");
    }

    [Fact]
    public void DevelopingPrompt_RequiresTestsAsPartOfTheImplementation()
    {
        var system = TaskOrchestrator.BuildDevelopingPrompt(
            MakeTask(), MakeProject(), repo: null, MakeAgent(),
            memories: "", plan: "the plan",
            contextFilesSection: null, conversationSection: null).System;

        // QA runs these; whatever the dev agent skips is coverage nobody adds later.
        system.Should().Contain("unit tests");
        system.Should().Contain("integration tests");
        system.Should().Contain("no test framework at all");
    }
}
