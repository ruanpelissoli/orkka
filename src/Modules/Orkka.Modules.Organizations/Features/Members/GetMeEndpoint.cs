namespace Orkka.Modules.Organizations.Features.Members;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.SharedKernel.Security;

public sealed class GetMeEndpoint(
    OrganizationsDbContext db,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<MeResponse>
{
    public override void Configure()
    {
        Get("/api/org/me");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var member = await db.OrgMembers
            .AsNoTracking()
            .Where(m => m.Id == currentUser.MemberId && m.OrgId == currentUser.OrgId)
            // OrgName rides along on the same round trip via the navigation — the settings
            // page and any org-labelled chrome need it, and this is the one call every
            // dashboard load already makes.
            .Select(m => new { m.Preferences, OrgName = m.Organization.Name })
            .FirstOrDefaultAsync(ct);

        await Send.OkAsync(new MeResponse(
            currentUser.OrgId.ToString(),
            member?.OrgName ?? string.Empty,
            currentUser.MemberId.ToString(),
            currentUser.Role.ToString(),
            [.. currentUser.Permissions.Select(p => p.ToString()).Order()],
            MemberPreferences.Parse(member?.Preferences)), ct);
    }
}
