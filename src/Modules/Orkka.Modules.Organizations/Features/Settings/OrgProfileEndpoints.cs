namespace Orkka.Modules.Organizations.Features.Settings;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.SharedKernel.Security;

/// <summary>The org's own identity — distinct from <c>/api/org/settings</c>, which carries
/// policy switches stored as JSON. The name is a column and is user-visible well outside
/// the app (invite pages and emails read it).</summary>
public sealed record OrgProfileResponse(string OrgId, string Name, string Slug);

public sealed record UpdateOrgProfileRequest(string Name);

/// <summary>Owner-only: the org's public identity is an ownership concern, not a grant.
/// The slug is deliberately not renameable — invite links and cached references hang off it.</summary>
public sealed class UpdateOrgProfileEndpoint(
    OrganizationsDbContext db,
    ICurrentUser currentUser,
    ILogger<UpdateOrgProfileEndpoint> logger)
    : Endpoint<UpdateOrgProfileRequest, OrgProfileResponse>
{
    public override void Configure()
    {
        Put("/api/org");
    }

    public override async Task HandleAsync(UpdateOrgProfileRequest req, CancellationToken ct)
    {
        if (!currentUser.IsOwner)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var name = OrgNaming.Normalize(req.Name);
        if (name is null)
        {
            AddError(
                r => r.Name,
                $"Name must be between 1 and {OrgNaming.MaxLength} characters.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var org = await db.Organizations.FirstOrDefaultAsync(o => o.Id == currentUser.OrgId, ct);
        if (org is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var previous = org.Name;
        org.Name = name;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Org {OrgId} renamed from {PreviousName} to {Name} by {UserId}",
            org.Id, previous, name, currentUser.UserId);

        await Send.OkAsync(new OrgProfileResponse(org.Id.ToString(), org.Name, org.Slug), ct);
    }
}
