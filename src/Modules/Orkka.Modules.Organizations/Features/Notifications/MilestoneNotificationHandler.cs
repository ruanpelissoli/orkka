namespace Orkka.Modules.Organizations.Features.Notifications;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;
using Orkka.SharedKernel.Security;

/// <summary>
/// Nudges the milestone's owner to plan what comes next when it completes — the grooming
/// trigger.
///
/// Deliberately a suggestion and nothing more. Auto-creating the next round of tasks would
/// spend real money on the strength of a milestone closing, which is exactly the decision the
/// propose/accept split on planning exists to keep with the human.
///
/// Also deliberately NOT an Attention Inbox item: that inbox is task-shaped end to end
/// (InboxTaskRow, AttentionStatuses, InboxRanking), and its value is that everything in it is
/// genuinely actionable right now. Revisit once these suggestions prove themselves.
///
/// IMPORTANT: the dispatcher scope has no authenticated ICurrentUser, so the tenant query
/// filter is OFF here — OrgId must be set on new rows and filtered in queries explicitly.
/// </summary>
public sealed class MilestoneCompletedNotificationHandler(
    OrganizationsDbContext db,
    NotificationPublisher publisher,
    IProjectsApi projectsApi) : IIntegrationEventHandler<MilestoneCompleted>
{
    public async Task HandleAsync(MilestoneCompleted evt, CancellationToken ct)
    {
        // Milestones seeded by provisioning have no owner to address. Nobody is notified
        // rather than everybody.
        if (string.IsNullOrEmpty(evt.CreatedByUserId))
            return;

        // A milestone can re-complete if a straggler is added and merged. One unread row.
        var alreadyPending = await db.Notifications.AnyAsync(n =>
            n.OrgId == evt.OrgId
            && n.UserId == evt.CreatedByUserId
            && n.Type == NotificationType.MilestoneCompleted
            && n.Title == evt.Title
            && !n.IsRead, ct);

        if (alreadyPending)
            return;

        var project = await projectsApi.GetProjectSummaryAsync(evt.ProjectId, ct);

        var notification = new Notification
        {
            OrgId = evt.OrgId,
            UserId = evt.CreatedByUserId,
            Type = NotificationType.MilestoneCompleted,
            Title = evt.Title,
            // Points at the roadmap, where the next milestone's tasks are generated on demand.
            // This used to say "ask the PM agent", which was the only route available when
            // there was nowhere for a finished milestone to hand off to.
            Body = $"All {evt.TaskCount} task(s) are done. Open the roadmap to plan what comes next.",
            ProjectId = evt.ProjectId,
            ProjectSlug = project?.Slug,
        };

        await publisher.PublishAsync(
            notification,
            persist: true,
            ct);
    }
}
