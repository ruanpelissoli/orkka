namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The reviewer used to be told to approve anything below [Critical] with no definition of
/// what, beyond correctness and security, is critical — so duplicated helpers, business rules
/// in components and swallowed exceptions sailed through as [Suggestion]. The orchestrator now
/// owns an objective "What blocks" list (mirrored from the `engineering-review` skill) so the
/// bar the dev agent builds to and the bar the reviewer bounces on cannot drift apart.
/// </summary>
public sealed class CodeReviewPromptTests
{
    private static TaskSnapshot MakeTask(int reviewCycle = 0) => new(
        Id: Ulid.NewUlid(), OrgId: Ulid.NewUlid(), ProjectId: Ulid.NewUlid(), RepoId: Ulid.NewUlid(),
        Number: 7, Title: "Active bets endpoint", Description: "GET /v1/bets/active",
        Priority: Priority.High, Type: TaskType.Feature,
        CurrentLane: Lane.CodeReview, Status: TaskStatus.Active, IsArchived: false,
        RequesterLocale: null, TotalTokensUsed: 0, HasAgentWorking: true,
        BranchName: "orkka/TAP-13", PrUrl: null, Tags: null,
        ContextFiles: null, ContextDocumentIds: null,
        SkipPlanning: false, SkipTesting: false, SkipCodeReview: false, IsMerged: false,
        ReviewCycleCount: reviewCycle, QaCycleCount: 0, QuestionSourceLane: null,
        AssignedAgentId: null, CurrentAgentId: null, SourceMessageId: null,
        StartedAt: null, CompletedAt: null, CheckCycleCount: 0);

    private static AgentSummary MakeReviewer(string persona = "You are Sentinel.") => new(
        Ulid.NewUlid(), Ulid.NewUlid(), Ulid.NewUlid(), [],
        "Sentinel", AgentRole.CodeReviewer, "claude-opus-5", persona, "");

    [Fact]
    public void CarriesTheObjectiveBlockingList_AndTheVerdictContract()
    {
        var prompt = TaskOrchestrator.BuildCodeReviewSystemPrompt(MakeTask(), MakeReviewer(), memories: "", maxReviewCycles: 2);

        prompt.Should().Contain("You are Sentinel.");
        prompt.Should().Contain("review cycle 1 of 2");

        // The blocking list — objective defects, each named so the reviewer can point at one.
        prompt.Should().Contain("## What blocks");
        prompt.Should().Contain("duplicating logic or a component the repo already");
        prompt.Should().Contain("business rules placed in a view");
        prompt.Should().Contain("no test when the repo has a test framework");
        prompt.Should().Contain("swallowed exception");
        prompt.Should().Contain("N+1");
        prompt.Should().Contain("hard-coded secret");
        prompt.Should().Contain("debug output");
        prompt.Should().Contain("a public entry point without input", because: "input validation on public entry points is a blocking item");
        prompt.Should().Contain("engineering-review");

        // Taste never bounces — the human owns preference, and a bounce costs a full cycle.
        prompt.Should().Contain("matter of taste or preference is [Suggestion]");

        // The verdict contract the lane parses (ReviewVerdict) — unchanged by the new block.
        prompt.Should().Contain("\"decision\": \"approve\" | \"request_changes\"");
        prompt.Should().Contain("Only use \"request_changes\" for [Critical] issues");
        prompt.Should().Contain("Suggestions and nits alone mean \"approve\"");
    }

    [Fact]
    public void ReviewerPersona_IsNotGivenTheCodeWritingBars()
    {
        // The bars are for agents whose output is code; a reviewer reading them would only
        // pay tokens — and BuildPersonaSection(agent) without codeWritingPhase must stay that way.
        var prompt = TaskOrchestrator.BuildCodeReviewSystemPrompt(MakeTask(), MakeReviewer(), memories: "", maxReviewCycles: 2);

        prompt.Should().NotContain("YOUR ENGINEERING BAR");
        prompt.Should().NotContain("COMMENTS:");
    }

    [Fact]
    public void SecondCycle_SaysSo()
        => TaskOrchestrator.BuildCodeReviewSystemPrompt(MakeTask(reviewCycle: 1), MakeReviewer(), memories: "", maxReviewCycles: 10)
            .Should().Contain("review cycle 2 of 10");
}
