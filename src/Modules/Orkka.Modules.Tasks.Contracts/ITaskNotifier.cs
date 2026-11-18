namespace Orkka.Modules.Tasks.Contracts;

public interface ITaskNotifier
{
    /// <summary>
    /// Send a stream event to clients watching a specific task (detail view).
    /// </summary>
    Task SendStreamEventAsync(Ulid taskId, StreamEvent streamEvent);

    /// <summary>
    /// Notify board viewers that a task changed lanes.
    /// </summary>
    Task SendLaneChangedAsync(Ulid projectId, LaneChangedEvent laneChanged);

    /// <summary>
    /// Notify board and task detail viewers that the status changed (e.g. blocked/unblocked).
    /// </summary>
    Task SendStatusChangedAsync(Ulid projectId, StatusChangedEvent statusChanged);

    /// <summary>
    /// Notify task detail viewers of a new message.
    /// </summary>
    Task SendNewMessageAsync(Ulid taskId, NewMessageEvent message);

    /// <summary>
    /// Notify board viewers that a task's newest agent message changed (card preview).
    /// </summary>
    Task SendTaskLastMessageChangedAsync(Ulid projectId, TaskLastMessageChangedEvent lastMessage);

    /// <summary>
    /// Notify board viewers that a new task was created (e.g. by the PM agent from chat).
    /// </summary>
    Task SendTaskCreatedAsync(Ulid projectId, TaskCreatedEvent taskCreated);

    /// <summary>
    /// Notify board viewers that a task was updated (title, priority, archived, etc).
    /// </summary>
    Task SendTaskUpdatedAsync(Ulid projectId, TaskUpdatedEvent taskUpdated);

    /// <summary>
    /// Notify board and task detail viewers that a task was permanently deleted.
    /// </summary>
    Task SendTaskDeletedAsync(Ulid projectId, TaskDeletedEvent taskDeleted);

    /// <summary>
    /// Notify board and task detail viewers that a merge attempt failed (or that a prior
    /// failure cleared — Reason=null).
    /// </summary>
    Task SendTaskMergeFailedAsync(Ulid projectId, TaskMergeFailedEvent mergeFailed);

    /// <summary>
    /// Notify board and task detail viewers that a task's unmet-dependency set changed
    /// (AI analysis after submission, manual add/remove, or a blocking task completing).
    /// </summary>
    Task SendTaskDependenciesChangedAsync(Ulid projectId, TaskDependenciesChangedEvent dependenciesChanged);

    /// <summary>
    /// Notify board and roadmap viewers that a milestone changed (created, edited, auto-started,
    /// auto-completed, or deleted).
    /// </summary>
    Task SendMilestoneChangedAsync(Ulid projectId, MilestoneChangedEvent milestoneChanged);

    /// <summary>
    /// Notify roadmap viewers that an asynchronous milestone-planning job moved — claimed,
    /// changed step, finished, or failed.
    ///
    /// This is what replaces the spinner the user used to sit in front of for three minutes
    /// before giving up and killing the request. Board group, like MilestoneChanged: the
    /// roadmap and the milestone detail page both live there.
    /// </summary>
    Task SendMilestonePlanChangedAsync(Ulid projectId, MilestonePlanChangedEvent planChanged);

    /// <summary>
    /// Notify board viewers that autopilot started, stopped, or moved its counters.
    ///
    /// The toggle is project-wide state, so a second person looking at the same board must see
    /// it flip — and must see it flip when the Worker stops the session, not only when a human
    /// clicks. Without this, autopilot switching itself off at 3am looks like a stale toggle.
    /// </summary>
    Task SendAutopilotChangedAsync(Ulid projectId, AutopilotChangedEvent autopilotChanged);

    /// <summary>
    /// Notify task detail viewers that a plan was reviewed.
    /// </summary>
    Task SendPlanReviewedAsync(Ulid taskId, PlanReviewedEvent planReviewed);

    /// <summary>
    /// Notify task detail viewers that a code review was submitted.
    /// </summary>
    Task SendCodeReviewedAsync(Ulid taskId, CodeReviewedEvent codeReviewed);

    /// <summary>A running task's live code-change snapshot was refreshed (Code View).</summary>
    Task SendWorkspaceChangedAsync(Ulid taskId, WorkspaceChangedEvent workspaceChanged);

    /// <summary>
    /// Push a freshly persisted in-app notification to its recipient's user group
    /// (the bell badge). Connections auto-join user:{userId} in TaskHub.OnConnectedAsync.
    /// </summary>
    Task SendNotificationCreatedAsync(string userId, NotificationCreatedEvent notification);

    /// <summary>
    /// Push a persisted agent-chat message to viewers of its conversation
    /// (clients join via TaskHub.JoinAgentChat).
    /// </summary>
    Task SendAgentChatMessageAsync(Ulid conversationId, AgentChatMessageEvent message);
}
