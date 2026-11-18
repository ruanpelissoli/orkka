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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

[Authorize(Policy = "HubAccess")]
public sealed class TaskHub(
    DbContextOptions<TasksDbContext> dbOptions,
    Orkka.Modules.Organizations.Contracts.IOrgMembershipService membership,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    IAgentsApi agentsApi,
    ILogger<TaskHub> logger) : Hub
{
    /// <summary>
    /// Every authenticated (non-service-account) connection auto-joins its own user group —
    /// the channel for personal in-app notifications (bell badge). The id comes from the
    /// caller's validated claims, so the group is not spoofable. The local
    /// edition's LocalAuthHandler issue the same claim shape.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        if (!HubSecurity.IsServiceAccount(Context.User))
        {
            var userId = HubSecurity.GetUserId(Context.User);
            if (!string.IsNullOrEmpty(userId))
                await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
        }

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Join a task detail group to receive real-time updates for a specific task.
    /// The caller must belong to the org that owns the task, otherwise access is denied —
    /// group names are ULIDs and must never be joinable by a caller who merely knows the id.
    /// </summary>
    public async Task JoinTask(string taskId)
    {
        await EnsureCallerOwnsAsync(taskId, isTask: true);
        await Groups.AddToGroupAsync(Context.ConnectionId, $"task:{taskId}");
    }

    public Task LeaveTask(string taskId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"task:{taskId}");

    /// <summary>
    /// Join a project board group to receive kanban board updates.
    /// The caller must belong to the org that owns the project.
    /// </summary>
    public async Task JoinBoard(string projectId)
    {
        await EnsureCallerOwnsAsync(projectId, isTask: false);
        await Groups.AddToGroupAsync(Context.ConnectionId, $"board:{projectId}");
    }

    public Task LeaveBoard(string projectId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"board:{projectId}");

    /// <summary>
    /// Join an agent-chat conversation group to receive messages persisted outside the
    /// caller's own SSE stream (detached runs completing after the panel was reopened).
    /// The caller must belong to the org that owns the conversation.
    /// </summary>
    public async Task JoinAgentChat(string conversationId)
    {
        if (!Ulid.TryParse(conversationId, out var ulid))
            throw new HubException("Access denied.");

        var caller = await HubSecurity.ResolveMembershipAsync(
            Context.User, membership, Context.ConnectionAborted,
            Context.GetHttpContext()?.Request.Query["workspace"].ToString());
        var conversationProjectId = caller is null
            ? null
            : await agentsApi.GetConversationProjectIdAsync(
                ulid, caller.OrgId, Context.ConnectionAborted);

        if (caller is null || conversationProjectId is null)
        {
            logger.LogWarning("Denied group join for agent chat {ConversationId} — caller org {OrgId} does not own it",
                conversationId, caller?.OrgId);
            throw new HubException("Access denied.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"agentchat:{conversationId}");
    }

    public Task LeaveAgentChat(string conversationId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"agentchat:{conversationId}");

    /// <summary>
    /// Join the caller's own app-run group for a project — the channel for "Run project"
    /// output and state, forwarded here by the Worker holding the runner socket. The group
    /// is scoped per (project, user) because runs execute on ONE user's machine: the user id
    /// comes from the caller's validated claims, never the wire, so nobody can watch (or
    /// type into) a colleague's terminal by knowing ids.
    /// </summary>
    public async Task JoinAppRun(string projectId)
    {
        await EnsureCallerOwnsAsync(projectId, isTask: false);
        var userId = HubSecurity.GetUserId(Context.User);
        if (string.IsNullOrEmpty(userId))
            throw new HubException("Access denied.");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"apprun:{projectId}:{userId}");
    }

    public Task LeaveAppRun(string projectId)
    {
        var userId = HubSecurity.GetUserId(Context.User);
        return string.IsNullOrEmpty(userId)
            ? Task.CompletedTask
            : Groups.RemoveFromGroupAsync(Context.ConnectionId, $"apprun:{projectId}:{userId}");
    }

    // --- Broadcast methods invoked by the Worker service account ONLY ---
    // These push events into arbitrary groups; a normal user must never be able to invoke them
    // (that would let them spoof board/task events for any org). Gated on the service-account claim.

    public Task BroadcastStreamEvent(string taskId, StreamEvent streamEvent)
    {
        RequireServiceAccount();
        return Clients.Group($"task:{taskId}").SendAsync("StreamEvent", streamEvent);
    }

    public Task BroadcastLaneChanged(string projectId, LaneChangedEvent laneChanged)
    {
        RequireServiceAccount();
        return Task.WhenAll(
            Clients.Group($"board:{projectId}").SendAsync("LaneChanged", laneChanged),
            Clients.Group($"task:{laneChanged.TaskId}").SendAsync("LaneChanged", laneChanged));
    }

    public Task BroadcastStatusChanged(string projectId, StatusChangedEvent statusChanged)
    {
        RequireServiceAccount();
        return Task.WhenAll(
            Clients.Group($"board:{projectId}").SendAsync("StatusChanged", statusChanged),
            Clients.Group($"task:{statusChanged.TaskId}").SendAsync("StatusChanged", statusChanged));
    }

    public Task BroadcastNewMessage(string taskId, NewMessageEvent message)
    {
        RequireServiceAccount();
        return Clients.Group($"task:{taskId}").SendAsync("NewMessage", message);
    }

    public Task BroadcastTaskLastMessageChanged(string projectId, TaskLastMessageChangedEvent lastMessage)
    {
        RequireServiceAccount();
        return Clients.Group($"board:{projectId}").SendAsync("TaskLastMessageChanged", lastMessage);
    }

    public Task BroadcastTaskCreated(string projectId, TaskCreatedEvent taskCreated)
    {
        RequireServiceAccount();
        return Clients.Group($"board:{projectId}").SendAsync("TaskCreated", taskCreated);
    }

    public Task BroadcastTaskUpdated(string projectId, TaskUpdatedEvent taskUpdated)
    {
        RequireServiceAccount();
        return Task.WhenAll(
            Clients.Group($"board:{projectId}").SendAsync("TaskUpdated", taskUpdated),
            Clients.Group($"task:{taskUpdated.TaskId}").SendAsync("TaskUpdated", taskUpdated));
    }

    public Task BroadcastTaskDeleted(string projectId, TaskDeletedEvent taskDeleted)
    {
        RequireServiceAccount();
        return Task.WhenAll(
            Clients.Group($"board:{projectId}").SendAsync("TaskDeleted", taskDeleted),
            Clients.Group($"task:{taskDeleted.TaskId}").SendAsync("TaskDeleted", taskDeleted));
    }

    public Task BroadcastTaskMergeFailed(string projectId, TaskMergeFailedEvent mergeFailed)
    {
        RequireServiceAccount();
        return Task.WhenAll(
            Clients.Group($"board:{projectId}").SendAsync("TaskMergeFailed", mergeFailed),
            Clients.Group($"task:{mergeFailed.TaskId}").SendAsync("TaskMergeFailed", mergeFailed));
    }

    public Task BroadcastTaskDependenciesChanged(string projectId, TaskDependenciesChangedEvent dependenciesChanged)
    {
        RequireServiceAccount();
        return Task.WhenAll(
            Clients.Group($"board:{projectId}").SendAsync("TaskDependenciesChanged", dependenciesChanged),
            Clients.Group($"task:{dependenciesChanged.TaskId}").SendAsync("TaskDependenciesChanged", dependenciesChanged));
    }

    public Task BroadcastMilestoneChanged(string projectId, MilestoneChangedEvent milestoneChanged)
    {
        RequireServiceAccount();
        return Clients.Group($"board:{projectId}").SendAsync("MilestoneChanged", milestoneChanged);
    }

    // Relay for the Worker: milestone planning runs there, so every progress step and the
    // finished plan reach the browser through this hop.
    public Task BroadcastMilestonePlanChanged(string projectId, MilestonePlanChangedEvent planChanged)
    {
        RequireServiceAccount();
        return Clients.Group($"board:{projectId}").SendAsync("MilestonePlanChanged", planChanged);
    }

    // Relay for the Worker: autopilot stops itself (drained, capped, budget-blocked) with
    // nobody watching the API host, so the board's toggle has to be flipped from here.
    public Task BroadcastAutopilotChanged(string projectId, AutopilotChangedEvent autopilotChanged)
    {
        RequireServiceAccount();
        return Clients.Group($"board:{projectId}").SendAsync("AutopilotChanged", autopilotChanged);
    }

    public Task BroadcastPlanReviewed(string taskId, PlanReviewedEvent planReviewed)
    {
        RequireServiceAccount();
        return Clients.Group($"task:{taskId}").SendAsync("PlanReviewed", planReviewed);
    }

    public Task BroadcastCodeReviewed(string taskId, CodeReviewedEvent codeReviewed)
    {
        RequireServiceAccount();
        return Clients.Group($"task:{taskId}").SendAsync("CodeReviewed", codeReviewed);
    }

    public Task BroadcastWorkspaceChanged(string taskId, WorkspaceChangedEvent workspaceChanged)
    {
        RequireServiceAccount();
        return Clients.Group($"task:{taskId}").SendAsync("WorkspaceChanged", workspaceChanged);
    }

    public Task BroadcastNotificationCreated(string userId, NotificationCreatedEvent notification)
    {
        RequireServiceAccount();
        return Clients.Group($"user:{userId}").SendAsync("NotificationCreated", notification);
    }

    public Task BroadcastAgentChatMessage(string conversationId, AgentChatMessageEvent message)
    {
        RequireServiceAccount();
        return Clients.Group($"agentchat:{conversationId}").SendAsync("AgentChatMessage", message);
    }

    // App-run events are opaque relays: the Worker owns the snapshot shape (it lives with
    // the orchestrator, not this module), so state travels as a JSON payload this hub
    // forwards verbatim. PTY output is base64 VT bytes.

    public Task BroadcastAppRunOutput(string projectId, string userId, string runId, string base64)
    {
        RequireServiceAccount();
        return Clients.Group($"apprun:{projectId}:{userId}")
            .SendAsync("AppRunOutput", runId, base64);
    }

    public Task BroadcastAppRunState(
        string projectId, string userId, System.Text.Json.JsonElement snapshots)
    {
        RequireServiceAccount();
        return Clients.Group($"apprun:{projectId}:{userId}")
            .SendAsync("AppRunState", snapshots);
    }

    private void RequireServiceAccount()
    {
        if (!HubSecurity.IsServiceAccount(Context.User))
        {
            logger.LogWarning("Rejected non-service-account broadcast attempt on connection {ConnectionId}",
                Context.ConnectionId);
            throw new HubException("Access denied.");
        }
    }

    private async Task EnsureCallerOwnsAsync(string id, bool isTask)
    {
        if (!Ulid.TryParse(id, out var ulid))
            throw new HubException("Access denied.");

        var caller = await HubSecurity.ResolveMembershipAsync(
            Context.User, membership, Context.ConnectionAborted,
            Context.GetHttpContext()?.Request.Query["workspace"].ToString());
        if (caller is null)
            throw new HubException("Access denied.");

        bool owns;
        if (isTask)
        {
            await using var db = new TasksDbContext(dbOptions);
            var task = await db.Tasks.AsNoTracking()
                .Where(t => t.Id == ulid && t.OrgId == caller.OrgId)
                .Select(t => new { t.ProjectId })
                .FirstOrDefaultAsync(Context.ConnectionAborted);
            owns = task is not null;
        }
        else
        {
            var project = await projectsApi.GetProjectSummaryAsync(ulid, Context.ConnectionAborted);
            owns = project is not null && project.OrgId == caller.OrgId;
        }

        if (!owns)
        {
            logger.LogWarning("Denied group join for {Kind} {Id} — caller org {OrgId} does not own it",
                isTask ? "task" : "project", id, caller.OrgId);
            throw new HubException("Access denied.");
        }
    }
}
