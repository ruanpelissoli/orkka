namespace Orkka.Modules.Organizations.Features.Notifications;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;

/// <summary>
/// Creates persisted in-app notifications for the task creator when a task needs a human
/// (plan approval, questions, code/QA review, failure, completion) and pushes them to the
/// recipient's user group.
///
/// IMPORTANT: the dispatcher scope has no authenticated ICurrentUser, so the tenant query
/// filter is OFF here — OrgId must be set on new rows and filtered in queries explicitly.
/// </summary>
public sealed class TaskStatusChangedNotificationHandler(
    OrganizationsDbContext db,
    NotificationPublisher publisher,
    IProjectsApi projectsApi) : IIntegrationEventHandler<TaskStatusChanged>
{
    public async Task HandleAsync(TaskStatusChanged evt, CancellationToken ct)
    {
        var type = MapStatus(evt.NewStatus);
        if (type is null)
            return;

        await TaskNotificationCreator.CreateAsync(
            db, publisher, projectsApi,
            evt.OrgId, evt.ProjectId, evt.TaskId, evt.TaskNumber, evt.TaskTitle,
            evt.CreatedByUserId, evt.ActorUserId, type.Value, body: null, ct);
    }

    private static NotificationType? MapStatus(string newStatus) => newStatus switch
    {
        "WaitingPlanApproval" => NotificationType.PlanApprovalRequired,
        "WaitingAnswer" => NotificationType.QuestionAsked,
        "WaitingCodeReview" => NotificationType.CodeReviewRequired,
        "WaitingQaReview" => NotificationType.QaReviewRequired,
        "WaitingUserVerification" => NotificationType.VerificationRequired,
        "Blocked" or "ReasoningTimeout" or "OutOfTokens" => NotificationType.TaskFailed,
        "WaitingForRunner" => NotificationType.RunnerOffline,
        "Completed" => NotificationType.TaskCompleted,
        _ => null,
    };
}

public sealed class TaskMergeFailedNotificationHandler(
    OrganizationsDbContext db,
    NotificationPublisher publisher,
    IProjectsApi projectsApi) : IIntegrationEventHandler<TaskMergeAttemptFailed>
{
    public Task HandleAsync(TaskMergeAttemptFailed evt, CancellationToken ct) =>
        TaskNotificationCreator.CreateAsync(
            db, publisher, projectsApi,
            evt.OrgId, evt.ProjectId, evt.TaskId, evt.TaskNumber, evt.TaskTitle,
            evt.CreatedByUserId, evt.ActorUserId, NotificationType.TaskMergeFailed, evt.Reason, ct);
}

internal static class TaskNotificationCreator
{
    internal static async Task CreateAsync(
        OrganizationsDbContext db,
        NotificationPublisher publisher,
        IProjectsApi projectsApi,
        Ulid orgId, Ulid projectId, Ulid taskId, int taskNumber, string taskTitle,
        string? createdByUserId, string? actorUserId,
        NotificationType type, string? body, CancellationToken ct)
    {
        // Recipient is the task creator only. Agent-created tasks without a requesting user
        // have no recipient; a creator acting on their own task needs no notification.
        if (string.IsNullOrEmpty(createdByUserId) || actorUserId == createdByUserId)
            return;

        // One unread notification per (user, task, type) — repeated transitions (retries,
        // unblock restoring a waiting status) must not pile up rows.
        var alreadyPending = await db.Notifications.AnyAsync(n =>
            n.OrgId == orgId
            && n.UserId == createdByUserId
            && n.TaskId == taskId
            && n.Type == type
            && !n.IsRead, ct);
        if (alreadyPending)
            return;

        var project = await projectsApi.GetProjectSummaryAsync(projectId, ct);

        var notification = new Notification
        {
            OrgId = orgId,
            UserId = createdByUserId,
            TaskId = taskId,
            Type = type,
            Title = taskTitle,
            Body = body,
            ProjectId = projectId,
            ProjectSlug = project?.Slug,
            TaskNumber = taskNumber,
        };

        await publisher.PublishAsync(
            notification,
            persist: true,
            ct);
    }
}
