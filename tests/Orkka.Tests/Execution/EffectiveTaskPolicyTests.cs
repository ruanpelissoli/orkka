namespace Orkka.Tests.Execution;

using FluentAssertions;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The gate resolution autopilot rides on. Two properties matter more than the individual
/// values: a hand-submitted task must behave exactly as it does today, and an autopilot task
/// must not stop at anything that waits for a person.
/// </summary>
public sealed class EffectiveTaskPolicyTests
{
    private static TaskSnapshot Task(Ulid? autopilotSessionId = null, bool answerEveryQuestion = false) => new(
        Id: Ulid.NewUlid(),
        OrgId: Ulid.NewUlid(),
        ProjectId: Ulid.NewUlid(),
        RepoId: Ulid.NewUlid(),
        Number: 1,
        Title: "t",
        Description: "d",
        Priority: Priority.Medium,
        Type: TaskType.Feature,
        CurrentLane: Lane.Ready,
        Status: TaskStatus.InQueue,
        IsArchived: false,
        RequesterLocale: null,
        TotalTokensUsed: 0,
        HasAgentWorking: false,
        BranchName: null,
        PrUrl: null,
        Tags: null,
        ContextFiles: null,
        ContextDocumentIds: null,
        SkipPlanning: false,
        SkipTesting: false,
        SkipCodeReview: false,
        IsMerged: false,
        ReviewCycleCount: 0,
        QaCycleCount: 0,
        QuestionSourceLane: null,
        AssignedAgentId: null,
        CurrentAgentId: null,
        SourceMessageId: null,
        StartedAt: null,
        CompletedAt: null,
        AutopilotSessionId: autopilotSessionId,
        AutopilotAnswersEveryQuestion: answerEveryQuestion);

    private static ProjectSettingsDto Settings(
        bool qa = false, bool manualVerification = false, bool autoApproveReview = false,
        bool autoApprovePlan = false) =>
        new(MaxParallelTasks: 3,
            SameRepoParallelism: false,
            QaEnabled: qa,
            ManualVerificationEnabled: manualVerification,
            AutoApproveCodeReview: autoApproveReview,
            AutoApprovePlan: autoApprovePlan);

    [Fact]
    public void HandSubmittedTask_MirrorsProjectSettings()
    {
        var policy = EffectiveTaskPolicy.For(
            Task(), Settings(qa: true, manualVerification: true, autoApproveReview: true));

        policy.IsAutopilot.Should().BeFalse();
        policy.RunQaLane.Should().BeTrue();
        policy.ParkForManualVerification.Should().BeTrue();
        policy.AutoApproveCodeReview.Should().BeTrue();
        policy.AutoApprovePlan.Should().BeFalse("the project did not turn plan review off");
        policy.PmResolvesOwnQuestions.Should().BeFalse();
    }

    [Fact]
    public void HandSubmittedTask_AutoApprovesThePlan_WhenTheProjectTurnedPlanReviewOff()
    {
        // The one plan gate a project can move by itself (hosted apps included): the plan is
        // still written and followed, only the "approve this plan" stop is gone. Nothing else
        // changes — the reviewer gate stays as configured.
        var policy = EffectiveTaskPolicy.For(Task(), Settings(autoApprovePlan: true));

        policy.IsAutopilot.Should().BeFalse();
        policy.AutoApprovePlan.Should().BeTrue();
        policy.AutoApproveCodeReview.Should().BeFalse();
        policy.PmResolvesOwnQuestions.Should().BeFalse();
    }

    [Fact]
    public void HandSubmittedTask_DefaultSettings_KeepsEveryHumanGate()
    {
        var policy = EffectiveTaskPolicy.For(Task(), Settings());

        policy.RunQaLane.Should().BeFalse();
        policy.ParkForManualVerification.Should().BeFalse();
        policy.AutoApproveCodeReview.Should().BeFalse();
        policy.AutoApprovePlan.Should().BeFalse();
        policy.PmResolvesOwnQuestions.Should().BeFalse();
    }

    [Fact]
    public void HandSubmittedTask_NullSettings_IsSafe()
    {
        // A project row with no settings JSON must not crash the orchestrator, and must not
        // silently behave as though automation were enabled.
        var policy = EffectiveTaskPolicy.For(Task(), settings: null);

        policy.IsAutopilot.Should().BeFalse();
        policy.AutoApproveCodeReview.Should().BeFalse();
        policy.RunQaLane.Should().BeFalse();
    }

    [Fact]
    public void AutopilotTask_ClearsEveryHumanGate()
    {
        var policy = EffectiveTaskPolicy.For(Task(Ulid.NewUlid()), Settings());

        policy.IsAutopilot.Should().BeTrue();
        policy.AutoApprovePlan.Should().BeTrue("parking at Plan Ready is not hands-free");
        policy.AutoApproveCodeReview.Should().BeTrue();
        policy.PmResolvesOwnQuestions.Should().BeTrue();
        policy.RunQaLane.Should().BeFalse();
        policy.ParkForManualVerification.Should().BeFalse();
    }

    [Fact]
    public void AutopilotTask_IgnoresProjectSettingsEntirely()
    {
        // The whole point of the per-task stamp: autopilot overrides without writing to the
        // project row, so the user's configuration survives the session untouched.
        var policy = EffectiveTaskPolicy.For(
            Task(Ulid.NewUlid()), Settings(qa: true, manualVerification: true));

        policy.RunQaLane.Should().BeFalse("QA is the most token-expensive phase there is");
        policy.ParkForManualVerification.Should().BeFalse("it is a human gate by definition");
    }

    [Fact]
    public void AutopilotTask_KeepsTheQuestionGuardrails_ByDefault()
    {
        // Autopilot answers routine questions itself, but a missing recommendation or a
        // guarded topic still parks — unless the session was started to answer everything.
        var policy = EffectiveTaskPolicy.For(Task(Ulid.NewUlid()), Settings());

        policy.AutoAnswerQuestions.Should().BeTrue();
        policy.AnswerEveryQuestion.Should().BeFalse();
    }

    [Fact]
    public void AutopilotTask_AnswersEveryQuestion_WhenItsSessionWasStartedThatWay()
    {
        // The one autopilot gate the person starting a session can move: "never interrupt me
        // on a question". It rides on the task's session, not on the project.
        var policy = EffectiveTaskPolicy.For(
            Task(Ulid.NewUlid(), answerEveryQuestion: true), Settings());

        policy.AutoAnswerQuestions.Should().BeTrue();
        policy.AnswerEveryQuestion.Should().BeTrue();
    }

    [Fact]
    public void HandSubmittedTask_NeverAutoAnswers_EvenWithTheFlagSet()
    {
        // The flag only means something under a session. A task a person submitted by hand has
        // that person watching, and their question must reach them whatever the snapshot says.
        var policy = EffectiveTaskPolicy.For(Task(answerEveryQuestion: true), Settings());

        policy.AutoAnswerQuestions.Should().BeFalse();
        policy.AnswerEveryQuestion.Should().BeFalse();
    }

    [Fact]
    public void AutopilotTask_NullSettings_KeepsTheGuardrails()
    {
        var policy = EffectiveTaskPolicy.For(Task(Ulid.NewUlid()), settings: null);

        policy.AutoAnswerQuestions.Should().BeTrue();
        policy.AnswerEveryQuestion.Should().BeFalse();
    }
}
