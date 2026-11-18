namespace Orkka.Modules.Organizations.Features.Notifications;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.SharedKernel.Security;

/// <summary>
/// The caller's own notifications, newest first, plus their total unread count.
/// Rows are self-scoped (UserId), so no permission grant is required beyond membership.
/// </summary>
public sealed class ListNotificationsEndpoint(
    OrganizationsDbContext db,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<ListNotificationsResponse>
{
    private const int DefaultLimit = 30;
    private const int MaxLimit = 100;

    public override void Configure()
    {
        Get("/api/notifications");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var limit = Math.Clamp(Query<int?>("limit", isRequired: false) ?? DefaultLimit, 1, MaxLimit);

        var own = db.Notifications.AsNoTracking()
            .Where(n => n.UserId == currentUser.UserId);

        var items = await own
            .OrderByDescending(n => n.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        var unreadCount = await own.CountAsync(n => !n.IsRead, ct);

        await Send.OkAsync(new ListNotificationsResponse(
            [.. items.Select(n => n.ToResponse())], unreadCount), ct);
    }
}
