namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// What an agent is told about stopping.
///
/// Both rules here come from one incident: a backend agent in the DEVELOPING phase had several
/// commands refused by an over-broad blocklist, concluded every tool was dead, and parked the
/// task asking a human to unstick a sandbox that was never involved. Under autopilot that also
/// stalled the session, because the autonomy instruction only reached the PM phase — the one
/// phase the incident did not happen in.
/// </summary>
public sealed class AgentAutonomyPromptTests
{
    private const string RefusalMarker = "A refused command is NOT a blocker";
    private const string AutopilotMarker = "Autopilot is running";

    private static TaskSnapshot MakeTask(Ulid? autopilotSessionId = null, bool answerEveryQuestion = false) => new(
        Id: Ulid.NewUlid(), OrgId: Ulid.NewUlid(), ProjectId: Ulid.NewUlid(), RepoId: null,
        Number: 2, Title: "Auth endpoints", Description: "register, login, JWT middleware",
        Priority: Priority.Critical, Type: TaskType.Feature,
        CurrentLane: Lane.Developing, Status: TaskStatus.Active, IsArchived: false,
        RequesterLocale: null, TotalTokensUsed: 0, HasAgentWorking: true,
        BranchName: "feat/auth", PrUrl: null, Tags: null,
        ContextFiles: null, ContextDocumentIds: null,
        SkipPlanning: false, SkipTesting: false, SkipCodeReview: false, IsMerged: false,
        ReviewCycleCount: 0, QaCycleCount: 0, QuestionSourceLane: null,
        AssignedAgentId: null, CurrentAgentId: null, SourceMessageId: null,
        StartedAt: null, CompletedAt: null,
        AutopilotSessionId: autopilotSessionId,
        AutopilotAnswersEveryQuestion: answerEveryQuestion);

    private static ProjectSummary MakeProject() => new(
        Ulid.NewUlid(), Ulid.NewUlid(), "Tapago", "tapago", "TAP", Settings: null);

    private static AgentSummary MakeAgent() => new(
        Ulid.NewUlid(), Ulid.NewUlid(), Ulid.NewUlid(), [],
        "Atlas", AgentRole.BackendDev, "claude-sonnet-4-6", "You are Atlas.", "");

    private static string DevelopingSystem(Ulid? session = null, bool answerAllQuestions = false) =>
        TaskOrchestrator.BuildDevelopingPrompt(
            MakeTask(session, answerAllQuestions), MakeProject(), repo: null, MakeAgent(),
            memories: "", plan: "the plan",
            contextFilesSection: null, conversationSection: null).System;

    private static string PlanningSystem(Ulid? session = null, bool answerAllQuestions = false) =>
        TaskOrchestrator.BuildPlanningPrompt(
            MakeTask(session, answerAllQuestions), MakeProject(), repo: null, MakeAgent(),
            memories: "", contextFilesSection: null, conversationSection: null).System;

    private const string AnswerEveryQuestionMarker = "answers every question automatically";

    [Fact]
    public void AnswerAllQuestionsSession_TellsTheAgentItsRecommendationIsTheDecision()
    {
        // With the session answering every question, "recommended" is no longer a hint for a
        // human — it is what the task will do. Both phases that can ask must hear that.
        DevelopingSystem(Ulid.NewUlid(), answerAllQuestions: true).Should().Contain(AnswerEveryQuestionMarker);
        PlanningSystem(Ulid.NewUlid(), answerAllQuestions: true).Should().Contain(AnswerEveryQuestionMarker);
    }

    [Fact]
    public void AnswerAllQuestionsNotice_IsAutopilotOnly()
    {
        // The choice belongs to an autopilot session: a hand-submitted task still has a person
        // to ask, and must not be told otherwise.
        DevelopingSystem(answerAllQuestions: true).Should().NotContain(AnswerEveryQuestionMarker);
        PlanningSystem(answerAllQuestions: true).Should().NotContain(AnswerEveryQuestionMarker);

        // And plain autopilot keeps its guardrails, so it is not told they are gone.
        DevelopingSystem(Ulid.NewUlid()).Should().NotContain(AnswerEveryQuestionMarker);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DevelopingPrompt_AlwaysSaysARefusalIsNotABlocker(bool autopilot)
    {
        // Not autopilot-conditional: parking a task because a command was refused is wrong
        // whoever is watching. Nobody can answer "is the sandbox stuck?" — there is no sandbox.
        DevelopingSystem(autopilot ? Ulid.NewUlid() : null)
            .Should().Contain(RefusalMarker);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlanningPrompt_AlwaysSaysARefusalIsNotABlocker(bool autopilot)
    {
        PlanningSystem(autopilot ? Ulid.NewUlid() : null)
            .Should().Contain(RefusalMarker);
    }

    [Fact]
    public void DevelopingPrompt_UnderAutopilot_TellsTheAgentNobodyWillAnswer()
    {
        // The gap this closes: the dev phase is where the incident happened, and it was the
        // one phase the autonomy instruction did not reach.
        DevelopingSystem(Ulid.NewUlid()).Should().Contain(AutopilotMarker);
    }

    [Fact]
    public void PlanningPrompt_UnderAutopilot_TellsTheAgentNobodyWillAnswer()
    {
        PlanningSystem(Ulid.NewUlid()).Should().Contain(AutopilotMarker);
    }

    [Fact]
    public void HandSubmittedTask_IsNotToldToResolveItsOwnQuestions()
    {
        // With a human watching, asking is the right move — the autonomy nudge must not leak
        // into ordinary runs and make an agent guess at a product decision.
        DevelopingSystem().Should().NotContain(AutopilotMarker);
        PlanningSystem().Should().NotContain(AutopilotMarker);
    }

    [Fact]
    public void ReadOnlyPhases_SayTheyHaveNoShell()
    {
        // Planning has Read/Glob/Grep only. Discovering that by having a command refused is
        // what makes an agent report the sandbox as broken.
        PlanningSystem().Should().Contain("You do NOT have a shell in this phase");

        // Developing does have one, and must not be told otherwise.
        DevelopingSystem().Should().NotContain("You do NOT have a shell in this phase");
    }
}
