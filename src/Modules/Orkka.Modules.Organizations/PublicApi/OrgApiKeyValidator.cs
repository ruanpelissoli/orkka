namespace Orkka.Modules.Organizations.PublicApi;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Organizations.Contracts;
using Orkka.Modules.Organizations.Features.ApiKeys;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.SharedKernel.Security;

/// <summary>
/// Resolves a raw org API key to its principal. Constructs its own DbContext from options
/// (validation runs during authentication, before any tenant context exists, and must see all
/// orgs). Effective permissions = key snapshot ∩ member's live org-wide permissions, so a
/// demoted member's keys shrink with them and a removed member's keys die.
/// </summary>
internal sealed class OrgApiKeyValidator(DbContextOptions<OrganizationsDbContext> dbOptions) : IOrgApiKeyValidator
{
    public async Task<ApiKeyPrincipal?> ValidateAsync(string rawKey, CancellationToken ct = default)
    {
        if (!rawKey.StartsWith(ApiKeyValues.Prefix, StringComparison.Ordinal) || rawKey.Length < 30)
            return null;

        var hash = ApiKeyValues.Hash(rawKey);
        await using var db = new OrganizationsDbContext(dbOptions);

        var key = await db.OrgApiKeys.AsNoTracking()
            .Where(k => k.KeyHash == hash && k.RevokedAt == null)
            .Select(k => new { k.Id, k.Name, k.OrgId, k.MemberId, k.UserId, k.Permissions, k.ProjectId })
            .FirstOrDefaultAsync(ct);
        if (key is null) return null;

        var member = await db.OrgMembers.AsNoTracking()
            .Where(m => m.Id == key.MemberId && m.OrgId == key.OrgId)
            .Select(m => new
            {
                m.Role,
                Permissions = m.Permissions.Where(p => p.ProjectId == null).Select(p => p.Permission).ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (member is null) return null;

        var requested = key.Permissions
            .Select(name => Enum.TryParse<Permission>(name, out var p) ? (Permission?)p : null)
            .Where(p => p is not null)
            .Select(p => p!.Value)
            .ToHashSet();
        // Owners hold every permission implicitly; members are capped by their grants.
        var effective = member.Role == OrgRole.Owner
            ? requested.ToList()
            : requested.Intersect(member.Permissions).ToList();

        // Fire-and-forget last-used stamp; never on the auth critical path.
        _ = Task.Run(async () =>
        {
            try
            {
                await using var stamp = new OrganizationsDbContext(dbOptions);
                await stamp.OrgApiKeys.Where(k => k.Id == key.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(k => k.LastUsedAt, DateTimeOffset.UtcNow), CancellationToken.None);
            }
            catch { /* best effort */ }
        }, CancellationToken.None);

        return new ApiKeyPrincipal(key.Id, key.Name, key.OrgId, key.MemberId, key.UserId, effective, key.ProjectId);
    }
}
