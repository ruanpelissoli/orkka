namespace Orkka.Modules.Organizations.Features.Members;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.SharedKernel.Caching;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Utilities;

/// <summary>Self-scoped: any authenticated member can update their own UI preferences —
/// no permission guard, writes only the caller's OrgMember row. Fields are merged over the
/// stored blob, so a caller flipping one preference never clobbers the others.</summary>
public sealed class UpdateMyPreferencesEndpoint(
    OrganizationsDbContext db,
    ICurrentUser currentUser,
    ICacheInvalidator invalidator,
    IMemoryCache memoryCache)
    : Endpoint<UpdateMyPreferencesRequest, MemberPreferences>
{
    public override void Configure()
    {
        Put("/api/org/me/preferences");
    }

    public override async Task HandleAsync(UpdateMyPreferencesRequest req, CancellationToken ct)
    {
        var member = await db.OrgMembers
            .FirstOrDefaultAsync(m => m.Id == currentUser.MemberId, ct);
        if (member is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var current = MemberPreferences.Parse(member.Preferences);
        var preferences = current with
        {
            ShowTechnicalDetails = req.ShowTechnicalDetails ?? current.ShowTechnicalDetails,
            OnboardingTourSeen = req.OnboardingTourSeen ?? current.OnboardingTourSeen,
            // Normalized before it is stored, so an unsupported tag is a no-op rather than a
            // value that silently disables the agents' language handling later.
            Locale = AgentLanguage.Normalize(req.Locale) ?? current.Locale,
        };
        member.Preferences = preferences.ToJson();
        await db.SaveChangesAsync(ct);

        // The auth middleware reads the language off the CACHED membership, so without this
        // a member who just switched language keeps getting agent replies in the old one
        // until the entry expires. Two caches, one key: the hosted API keeps membership in
        // IOrkkaCache, Orkka Local in its own IMemoryCache. Removing a key that is not there
        // is a no-op, so each host pays for one miss and neither needs to know about the other.
        if (!string.IsNullOrEmpty(currentUser.UserId))
        {
            // Both the active-workspace entry and this workspace's own: preferences live on
            // the membership, and a request may resolve it either way.
            foreach (var membershipKey in new[]
            {
                CacheKeys.Membership(currentUser.UserId),
                CacheKeys.Membership(currentUser.UserId, currentUser.OrgId),
            })
            {
                await invalidator.RemoveAsync(membershipKey, ct);
                memoryCache.Remove(membershipKey);
            }
        }

        await Send.OkAsync(preferences, ct);
    }
}
