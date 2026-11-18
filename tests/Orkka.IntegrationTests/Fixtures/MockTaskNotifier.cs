namespace Orkka.IntegrationTests.Fixtures;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Infrastructure;

public sealed class MockTaskNotifier : ITaskNotifier
{
    public List<object> SentEvents { get; } = [];

    public Task SendStreamEventAsync(Ulid taskId, StreamEvent streamEvent)
    {
        SentEvents.Add(streamEvent);
        return Task.CompletedTask;
    }

    public Task SendLaneChangedAsync(Ulid projectId, LaneChangedEvent laneChanged)
    {
        SentEvents.Add(laneChanged);
        return Task.CompletedTask;
    }

    public Task SendStatusChangedAsync(Ulid projectId, StatusChangedEvent statusChanged)
    {
        SentEvents.Add(statusChanged);
        return Task.CompletedTask;
    }

    public Task SendNewMessageAsync(Ulid taskId, NewMessageEvent message)
    {
        SentEvents.Add(message);
        return Task.CompletedTask;
    }

    public Task SendTaskLastMessageChangedAsync(Ulid projectId, TaskLastMessageChangedEvent lastMessage)
    {
        SentEvents.Add(lastMessage);
        return Task.CompletedTask;
    }

    public Task SendTaskCreatedAsync(Ulid projectId, TaskCreatedEvent taskCreated)
    {
        SentEvents.Add(taskCreated);
        return Task.CompletedTask;
    }

    public Task SendTaskUpdatedAsync(Ulid projectId, TaskUpdatedEvent taskUpdated)
    {
        SentEvents.Add(taskUpdated);
        return Task.CompletedTask;
    }

    public Task SendTaskDeletedAsync(Ulid projectId, TaskDeletedEvent taskDeleted)
    {
        SentEvents.Add(taskDeleted);
        return Task.CompletedTask;
    }

    public Task SendTaskMergeFailedAsync(Ulid projectId, TaskMergeFailedEvent mergeFailed)
    {
        SentEvents.Add(mergeFailed);
        return Task.CompletedTask;
    }

    public Task SendMilestoneChangedAsync(Ulid projectId, MilestoneChangedEvent milestoneChanged)
    {
        SentEvents.Add(milestoneChanged);
        return Task.CompletedTask;
    }

    public Task SendMilestonePlanChangedAsync(Ulid projectId, MilestonePlanChangedEvent planChanged)
    {
        SentEvents.Add(planChanged);
        return Task.CompletedTask;
    }

    public Task SendAutopilotChangedAsync(Ulid projectId, AutopilotChangedEvent autopilotChanged)
    {
        SentEvents.Add(autopilotChanged);
        return Task.CompletedTask;
    }

    public Task SendTaskDependenciesChangedAsync(Ulid projectId, TaskDependenciesChangedEvent dependenciesChanged)
    {
        SentEvents.Add(dependenciesChanged);
        return Task.CompletedTask;
    }

    public Task SendPlanReviewedAsync(Ulid taskId, PlanReviewedEvent planReviewed)
    {
        SentEvents.Add(planReviewed);
        return Task.CompletedTask;
    }

    public Task SendCodeReviewedAsync(Ulid taskId, CodeReviewedEvent codeReviewed)
    {
        SentEvents.Add(codeReviewed);
        return Task.CompletedTask;
    }

    public Task SendWorkspaceChangedAsync(Ulid taskId, WorkspaceChangedEvent workspaceChanged)
    {
        SentEvents.Add(workspaceChanged);
        return Task.CompletedTask;
    }

    public Task SendNotificationCreatedAsync(string userId, NotificationCreatedEvent notification)
    {
        SentEvents.Add(notification);
        return Task.CompletedTask;
    }

    public Task SendAgentChatMessageAsync(Ulid conversationId, AgentChatMessageEvent message)
    {
        SentEvents.Add(message);
        return Task.CompletedTask;
    }
}
