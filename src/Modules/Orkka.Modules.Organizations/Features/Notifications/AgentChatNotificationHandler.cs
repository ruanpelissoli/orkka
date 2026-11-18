namespace Orkka.Modules.Organizations.Features.Notifications;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;

/// <summary>
/// Creates a persisted in-app notification when an agent-chat run completes detached
/// (the SSE observer had disconnected) or shipped deliverables (diagrams, tasks,
/// documents, design assets — the Agents module publishes for those even while
/// connected, since a live stream doesn't mean the user is watching). Title is the
/// agent's name and Body the conversation title; the type label is localized
/// client-side like task notifications.
///
/// IMPORTANT: the dispatcher scope has no authenticated ICurrentUser, so the tenant query
/// filter is OFF here — OrgId must be set on new rows and filtered in queries explicitly.
/// </summary>
public sealed class AgentChatNotificationHandler(
    OrganizationsDbContext db,
    NotificationPublisher publisher,
    IProjectsApi projectsApi) : IIntegrationEventHandler<AgentChatReplyCompleted>
{
    public async Task HandleAsync(AgentChatReplyCompleted evt, CancellationToken ct)
    {
        // One unread notification per (user, conversation) — several detached replies in
        // the same conversation must not pile up rows.
        var alreadyPending = await db.Notifications.AnyAsync(n =>
            n.OrgId == evt.OrgId
            && n.UserId == evt.RecipientUserId
            && n.ConversationId == evt.ConversationId
            && n.Type == NotificationType.AgentReplied
            && !n.IsRead, ct);
        if (alreadyPending)
            return;

        var project = await projectsApi.GetProjectSummaryAsync(evt.ProjectId, ct);

        var notification = new Notification
        {
            OrgId = evt.OrgId,
            UserId = evt.RecipientUserId,
            Type = NotificationType.AgentReplied,
            Title = evt.AgentName,
            Body = evt.ConversationTitle,
            ProjectId = evt.ProjectId,
            ProjectSlug = project?.Slug,
            AgentId = evt.AgentId,
            ConversationId = evt.ConversationId,
        };

        await publisher.PublishAsync(
            notification,
            persist: true,
            ct);
    }
}
