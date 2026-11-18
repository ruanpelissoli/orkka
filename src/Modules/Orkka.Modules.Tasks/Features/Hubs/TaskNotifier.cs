namespace Orkka.Modules.Tasks.Features.Hubs;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Storage;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Microsoft.AspNetCore.SignalR;

public sealed class TaskNotifier(IHubContext<TaskHub> hubContext) : ITaskNotifier
{
    public Task SendStreamEventAsync(Ulid taskId, StreamEvent streamEvent) =>
        hubContext.Clients.Group($"task:{taskId}")
            .SendAsync("StreamEvent", streamEvent);

    public Task SendLaneChangedAsync(Ulid projectId, LaneChangedEvent laneChanged) =>
        Task.WhenAll(
            hubContext.Clients.Group($"board:{projectId}")
                .SendAsync("LaneChanged", laneChanged),
            hubContext.Clients.Group($"task:{laneChanged.TaskId}")
                .SendAsync("LaneChanged", laneChanged));

    public Task SendStatusChangedAsync(Ulid projectId, StatusChangedEvent statusChanged) =>
        Task.WhenAll(
            hubContext.Clients.Group($"board:{projectId}")
                .SendAsync("StatusChanged", statusChanged),
            hubContext.Clients.Group($"task:{statusChanged.TaskId}")
                .SendAsync("StatusChanged", statusChanged));

    public Task SendNewMessageAsync(Ulid taskId, NewMessageEvent message) =>
        hubContext.Clients.Group($"task:{taskId}")
            .SendAsync("NewMessage", message);

    public Task SendTaskLastMessageChangedAsync(Ulid projectId, TaskLastMessageChangedEvent lastMessage) =>
        hubContext.Clients.Group($"board:{projectId}")
            .SendAsync("TaskLastMessageChanged", lastMessage);

    public Task SendTaskCreatedAsync(Ulid projectId, TaskCreatedEvent taskCreated) =>
        hubContext.Clients.Group($"board:{projectId}")
            .SendAsync("TaskCreated", taskCreated);

    public Task SendTaskUpdatedAsync(Ulid projectId, TaskUpdatedEvent taskUpdated) =>
        Task.WhenAll(
            hubContext.Clients.Group($"board:{projectId}")
                .SendAsync("TaskUpdated", taskUpdated),
            hubContext.Clients.Group($"task:{taskUpdated.TaskId}")
                .SendAsync("TaskUpdated", taskUpdated));

    public Task SendTaskDeletedAsync(Ulid projectId, TaskDeletedEvent taskDeleted) =>
        Task.WhenAll(
            hubContext.Clients.Group($"board:{projectId}")
                .SendAsync("TaskDeleted", taskDeleted),
            hubContext.Clients.Group($"task:{taskDeleted.TaskId}")
                .SendAsync("TaskDeleted", taskDeleted));

    public Task SendTaskMergeFailedAsync(Ulid projectId, TaskMergeFailedEvent mergeFailed) =>
        Task.WhenAll(
            hubContext.Clients.Group($"board:{projectId}")
                .SendAsync("TaskMergeFailed", mergeFailed),
            hubContext.Clients.Group($"task:{mergeFailed.TaskId}")
                .SendAsync("TaskMergeFailed", mergeFailed));

    public Task SendTaskDependenciesChangedAsync(Ulid projectId, TaskDependenciesChangedEvent dependenciesChanged) =>
        Task.WhenAll(
            hubContext.Clients.Group($"board:{projectId}")
                .SendAsync("TaskDependenciesChanged", dependenciesChanged),
            hubContext.Clients.Group($"task:{dependenciesChanged.TaskId}")
                .SendAsync("TaskDependenciesChanged", dependenciesChanged));

    // Board and roadmap both live under board:{projectId} — the roadmap page joins the same
    // group rather than adding a second one for the same project.
    public Task SendMilestoneChangedAsync(Ulid projectId, MilestoneChangedEvent milestoneChanged) =>
        hubContext.Clients.Group($"board:{projectId}")
            .SendAsync("MilestoneChanged", milestoneChanged);

    public Task SendMilestonePlanChangedAsync(Ulid projectId, MilestonePlanChangedEvent planChanged) =>
        hubContext.Clients.Group($"board:{projectId}")
            .SendAsync("MilestonePlanChanged", planChanged);

    // Board group, like MilestoneChanged: the toggle lives in the board header, and the roadmap
    // shares the same group.
    public Task SendAutopilotChangedAsync(Ulid projectId, AutopilotChangedEvent autopilotChanged) =>
        hubContext.Clients.Group($"board:{projectId}")
            .SendAsync("AutopilotChanged", autopilotChanged);

    public Task SendPlanReviewedAsync(Ulid taskId, PlanReviewedEvent planReviewed) =>
        hubContext.Clients.Group($"task:{taskId}")
            .SendAsync("PlanReviewed", planReviewed);

    public Task SendCodeReviewedAsync(Ulid taskId, CodeReviewedEvent codeReviewed) =>
        hubContext.Clients.Group($"task:{taskId}")
            .SendAsync("CodeReviewed", codeReviewed);

    public Task SendWorkspaceChangedAsync(Ulid taskId, WorkspaceChangedEvent workspaceChanged) =>
        hubContext.Clients.Group($"task:{taskId}")
            .SendAsync("WorkspaceChanged", workspaceChanged);

    public Task SendNotificationCreatedAsync(string userId, NotificationCreatedEvent notification) =>
        hubContext.Clients.Group($"user:{userId}")
            .SendAsync("NotificationCreated", notification);

    public Task SendAgentChatMessageAsync(Ulid conversationId, AgentChatMessageEvent message) =>
        hubContext.Clients.Group($"agentchat:{conversationId}")
            .SendAsync("AgentChatMessage", message);
}
