namespace Orkka.Modules.Organizations.Features.Notifications;

using Microsoft.Extensions.Logging;
using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.Modules.Tasks.Contracts;

/// <summary>
/// The one place a notification is published: persist the row, then push it to the user's
/// realtime group (bell / in-app list). Every producer (task status, merge failure, milestone,
/// autopilot, agent chat, approvals) calls this instead of hand-rolling the two steps.
///
/// Realtime is best-effort: the row is the source of truth and the UI catches up on next load.
/// It may not fail the calling handler.
///
/// Runs in the dispatcher scope (no ICurrentUser → tenant filter OFF): OrgId is set on the row
/// by the caller.
/// </summary>
public sealed class NotificationPublisher(
    OrganizationsDbContext db,
    ITaskNotifier taskNotifier,
    ILogger<NotificationPublisher> logger)
{
    /// <summary>
    /// Persists <paramref name="notification"/> (unless <paramref name="persist"/> is false —
    /// used when the caller already dedupes and only wants delivery), then delivers.
    /// </summary>
    public async Task PublishAsync(
        Notification notification,
        bool persist = true,
        CancellationToken ct = default)
    {
        if (persist)
        {
            db.Notifications.Add(notification);
            await db.SaveChangesAsync(ct);
        }

        try
        {
            await taskNotifier.SendNotificationCreatedAsync(notification.UserId, new NotificationCreatedEvent(
                notification.Id, notification.UserId, notification.TaskId,
                notification.Type.ToString(), notification.Title, notification.Body,
                notification.ProjectId, notification.ProjectSlug, notification.TaskNumber,
                notification.CreatedAt, notification.AgentId, notification.ConversationId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Realtime push failed for notification {NotificationId}", notification.Id);
        }
    }
}
