namespace Orkka.Modules.Organizations.Features.Notifications;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.SharedKernel.Security;

public sealed class MarkNotificationReadEndpoint(
    OrganizationsDbContext db,
    ICurrentUser currentUser)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/api/notifications/{id:ulid}/read");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = Ulid.Parse(Route<string>("id"));

        // Own rows only — a notification id from another user 404s rather than leaks.
        var notification = await db.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == currentUser.UserId, ct);

        if (notification is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!notification.IsRead)
        {
            notification.IsRead = true;
            await db.SaveChangesAsync(ct);
        }

        await Send.NoContentAsync(ct);
    }
}

public sealed class MarkAllNotificationsReadEndpoint(
    OrganizationsDbContext db,
    ICurrentUser currentUser)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/api/notifications/read-all");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Explicit OrgId alongside the tenant filter — ExecuteUpdate must never touch
        // another org's rows even if the query-filter context changes.
        await db.Notifications
            .Where(n => n.OrgId == currentUser.OrgId
                && n.UserId == currentUser.UserId
                && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);

        await Send.NoContentAsync(ct);
    }
}
