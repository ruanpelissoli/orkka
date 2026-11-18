namespace Orkka.Modules.Execution.Orchestration;

using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;

/// <summary>
/// Which human gates apply to one task's run.
///
/// The pipeline has five places it stops and waits for a person: the PM's clarifying question,
/// plan approval, the QA lane, manual verification, and confirming an agent's code-review
/// approval. Normally each is answered by the project's settings. A task autopilot submitted
/// answers all five without a human, because a session that parks on the first ambiguous task
/// is not hands-free — it is a slower way to arrive at the same inbox.
///
/// Resolved in one place, and read-only: this type never writes anything. Autopilot's override
/// lives entirely on <see cref="TaskSnapshot.AutopilotSessionId"/>, so a project's configured
/// behaviour is untouched and a hand-submitted task keeps it exactly.
/// </summary>
public readonly record struct EffectiveTaskPolicy(
    bool IsAutopilot,
    /// <summary>Approve the dev agent's plan and go straight to Developing, instead of parking
    /// in PlanReady. Note this is NOT <c>SkipPlanning</c>: the plan is still written, and the
    /// dev agent still works from it. Skipping planning outright produces worse code on
    /// anything non-trivial, and autopilot is exactly when nobody is watching for that.</summary>
    bool AutoApprovePlan,
    /// <summary>Run the QA lane. Off under autopilot: browser-driving runs are the most
    /// token-expensive phase there is, and a session's cap is better spent shipping tasks.</summary>
    bool RunQaLane,
    /// <summary>Park in Testing for the requester to try the change by hand. Never under
    /// autopilot — it is a human gate by definition.</summary>
    bool ParkForManualVerification,
    /// <summary>Complete and merge on the reviewer agent's approval, with no human confirming.
    ///
    /// The reviewer still RUNS. This is <c>AutoApproveCodeReview</c> semantics, never
    /// <c>SkipCodeReview</c>: one cheap agent pass is the only thing between a bad diff and the
    /// default branch at 3am, and a rejection still parks the task.</summary>
    bool AutoApproveCodeReview,
    /// <summary>The PM resolves ambiguity from the brief and the project's documents and
    /// proceeds on a stated assumption, rather than parking the task with a question. Parking
    /// remains the fallback for a task genuinely missing information — one nobody can act on
    /// should not be built either.</summary>
    bool PmResolvesOwnQuestions,
    /// <summary>When an agent still asks despite <see cref="PmResolvesOwnQuestions"/>, answer
    /// with the agent's own recommended options instead of parking — with the guardrails in
    /// TaskOrchestrator.TryAutoAnswerQuestionsAsync (every question must carry a
    /// recommendation, sensitive topics park, and a per-task cap stops loops).</summary>
    bool AutoAnswerQuestions,
    /// <summary>Drop every guardrail on <see cref="AutoAnswerQuestions"/> except the loop cap:
    /// a question without a recommendation takes its first option, a guarded topic is answered
    /// like any other, and a developer's free-text question is answered with "proceed on your
    /// best assumption". Chosen per autopilot session when it starts
    /// (<see cref="TaskSnapshot.AutopilotAnswersEveryQuestion"/>) — a run the owner wants
    /// genuinely uninterrupted, at the price of the agent's judgement standing on anything short
    /// of a loop.</summary>
    bool AnswerEveryQuestion)
{
    /// <summary>Every gate as the project configured it.</summary>
    public static EffectiveTaskPolicy FromSettings(TaskSnapshot task, ProjectSettingsDto? settings)
        => new(
            IsAutopilot: false,
            AutoApprovePlan: settings?.AutoApprovePlan == true,
            RunQaLane: settings?.QaEnabled == true,
            ParkForManualVerification: settings?.ManualVerificationEnabled == true,
            AutoApproveCodeReview: settings?.AutoApproveCodeReview == true,
            PmResolvesOwnQuestions: false,
            AutoAnswerQuestions: false,
            AnswerEveryQuestion: false);

    /// <summary>Hands-free: no gate waits for anybody. Whether a question can still park the
    /// task is the one thing the person starting the session gets a say in, via
    /// <paramref name="answerEveryQuestion"/>.</summary>
    public static EffectiveTaskPolicy Autopilot(bool answerEveryQuestion)
        => new(
            IsAutopilot: true,
            AutoApprovePlan: true,
            RunQaLane: false,
            ParkForManualVerification: false,
            AutoApproveCodeReview: true,
            PmResolvesOwnQuestions: true,
            AutoAnswerQuestions: true,
            AnswerEveryQuestion: answerEveryQuestion);

    /// <summary>
    /// The policy for this run. The task's own <c>Skip*</c> flags are deliberately NOT folded in
    /// here — they are opt-outs a human set on that specific task, they already read correctly
    /// at each call site, and autopilot has no business overriding them in either direction.
    /// </summary>
    public static EffectiveTaskPolicy For(TaskSnapshot task, ProjectSettingsDto? settings)
        => task.AutopilotSessionId is not null
            ? Autopilot(answerEveryQuestion: task.AutopilotAnswersEveryQuestion)
            : FromSettings(task, settings);
}
