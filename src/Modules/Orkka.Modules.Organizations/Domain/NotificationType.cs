namespace Orkka.Modules.Organizations.Domain;

// Stored as strings (max 30 chars) — renaming a member is a data migration once rows exist.
public enum NotificationType
{
    PlanApprovalRequired,
    QuestionAsked,
    CodeReviewRequired,
    QaReviewRequired,
    TaskFailed,
    TaskMergeFailed,
    TaskCompleted,
    AgentReplied,
    VerificationRequired,
    // Hybrid: the creator's runner is offline and a task parked waiting for it.
    RunnerOffline,
    // A milestone finished. The prompt is "plan what's next", never auto-created tasks —
    // spending on agent runs because a milestone closed is not the platform's call.
    MilestoneCompleted,
    // A completed milestone's integration branch could not be merged into the default branch.
    // No longer raised — milestones land task by task — and kept only so notifications already
    // stored with this type still load. Stored as a string: never rename or remove it.
    MilestoneShipFailed,
    // An autopilot session ended — drained, capped out, budget-blocked, or stopped after
    // repeated failures. This is the "here's what happened while you were away" moment, so it
    // goes to whoever started the session rather than to each task's creator.
    AutopilotStopped,
    // An interactive session paused on a command and is waiting for the session owner's ruling.
    ApprovalRequested,
    // A schedule (playbook / autopilot window) failed to run — sent to whoever created it. When the
    // failure streak tripped the auto-disable, the body says so: that is the part to act on.
    ScheduleFailed
}
