namespace Orkka.Modules.Organizations.Features.Notifications;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;

/// <summary>
/// An interactive session paused on a command and the agent is blocked until the session
/// owner rules on it.
///
/// The unread-row dedupe applies to the persisted list (one "waiting for your approval" row
/// per task) — the row is refreshed to the newest command so a tap lands on the live prompt.
///
/// IMPORTANT: the dispatcher scope has no authenticated ICurrentUser, so the tenant query
/// filter is OFF here — OrgId must be set on new rows and filtered in queries explicitly.
/// </summary>
public sealed class ApprovalRequestedNotificationHandler(
    OrganizationsDbContext db,
    NotificationPublisher publisher,
    IProjectsApi projectsApi) : IIntegrationEventHandler<InteractiveApprovalRequested>
{
    public async Task HandleAsync(InteractiveApprovalRequested evt, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(evt.SessionOwnerUserId))
            return;

        var project = await projectsApi.GetProjectSummaryAsync(evt.ProjectId, ct);
        var body = $"{evt.Tool}: {evt.Command}";

        var existing = await db.Notifications.FirstOrDefaultAsync(n =>
            n.OrgId == evt.OrgId
            && n.UserId == evt.SessionOwnerUserId
            && n.TaskId == evt.TaskId
            && n.Type == NotificationType.ApprovalRequested
            && !n.IsRead, ct);

        if (existing is not null)
        {
            // Keep the list row pointing at the newest command, but do not add another row.
            existing.Title = evt.TaskTitle;
            existing.Body = body;
            await db.SaveChangesAsync(ct);
            await publisher.PublishAsync(existing, persist: false, ct: ct);
            return;
        }

        var notification = new Notification
        {
            OrgId = evt.OrgId,
            UserId = evt.SessionOwnerUserId,
            TaskId = evt.TaskId,
            Type = NotificationType.ApprovalRequested,
            Title = evt.TaskTitle,
            Body = body,
            ProjectId = evt.ProjectId,
            ProjectSlug = project?.Slug,
            TaskNumber = evt.TaskNumber,
        };
        await publisher.PublishAsync(notification, persist: true, ct: ct);
    }
}

/// <summary>
/// The prompt was answered (by anyone) or expired: the "waiting for your approval" row is no
/// longer actionable, so it is marked read — otherwise the badge keeps pointing at a decision
/// already made. Only when no other prompt on the same task is still pending.
/// </summary>
public sealed class ApprovalSettledNotificationHandler(
    OrganizationsDbContext db) : IIntegrationEventHandler<InteractiveApprovalSettled>
{
    public async Task HandleAsync(InteractiveApprovalSettled evt, CancellationToken ct)
    {
        await db.Notifications
            .Where(n => n.OrgId == evt.OrgId
                && n.TaskId == evt.TaskId
                && n.Type == NotificationType.ApprovalRequested
                && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);
    }
}
