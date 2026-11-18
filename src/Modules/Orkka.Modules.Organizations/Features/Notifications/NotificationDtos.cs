namespace Orkka.Modules.Organizations.Features.Notifications;

using Orkka.Modules.Organizations.Domain;

public sealed record NotificationResponse(
    Ulid Id,
    Ulid? TaskId,
    string Type,
    string Title,
    string? Body,
    Ulid? ProjectId,
    string? ProjectSlug,
    int? TaskNumber,
    bool IsRead,
    DateTimeOffset CreatedAt,
    Ulid? AgentId = null,
    Ulid? ConversationId = null);

public sealed record ListNotificationsResponse(
    List<NotificationResponse> Items,
    int UnreadCount);

internal static class NotificationMappers
{
    internal static NotificationResponse ToResponse(this Notification n) => new(
        n.Id, n.TaskId, n.Type.ToString(), n.Title, n.Body,
        n.ProjectId, n.ProjectSlug, n.TaskNumber, n.IsRead, n.CreatedAt,
        n.AgentId, n.ConversationId);
}
