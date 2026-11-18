namespace Orkka.Modules.Organizations.Features.Notifications;

using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;

/// <summary>
/// Tells the creator of a schedule that a firing failed. Recurring work fails quietly by
/// nature — nobody is watching a 3 a.m. playbook — so the notification is the only way they
/// learn about it. Leads with "disabled" when the streak tripped the auto-disable, because
/// that is what needs a human.
///
/// IMPORTANT: the dispatcher scope has no authenticated ICurrentUser, so the tenant query
/// filter is OFF here — OrgId must be set on new rows and filtered in queries explicitly.
/// </summary>
public sealed class ScheduleFailedNotificationHandler(
    NotificationPublisher publisher,
    IProjectsApi projectsApi)
    : IIntegrationEventHandler<ScheduleRunFailed>
{
    public async Task HandleAsync(ScheduleRunFailed evt, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(evt.RecipientUserId))
            return;

        var project = await projectsApi.GetProjectSummaryAsync(evt.ProjectId, ct);

        var title = evt.Disabled
            ? $"\"{evt.ScheduleName}\" was disabled after repeated failures"
            : $"\"{evt.ScheduleName}\" failed to run";
        var body = evt.Error.Length > 200 ? evt.Error[..200] + "…" : evt.Error;

        var notification = new Notification
        {
            OrgId = evt.OrgId,
            UserId = evt.RecipientUserId,
            Type = NotificationType.ScheduleFailed,
            Title = title,
            Body = body,
            ProjectId = evt.ProjectId,
            ProjectSlug = project?.Slug,
        };

        await publisher.PublishAsync(
            notification,
            persist: true,
            ct);
    }
}
