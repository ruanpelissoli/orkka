namespace Orkka.Modules.Organizations.PublicApi;

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Organizations.Contracts;
using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Utilities;

// Constructs its own DbContext from options (no ICurrentUser): membership resolution runs
// before any tenant context exists (auth middleware, hub connections) and must see all orgs.
internal sealed partial class OrgMembershipService(
    DbContextOptions<OrganizationsDbContext> dbOptions,
    Orkka.Modules.Agents.Contracts.IAgentAuditLog? audit = null) : IOrgMembershipService
{
    public async Task<MembershipInfo?> ResolveAsync(string userId, CancellationToken ct = default)
    {
        await using var db = new OrganizationsDbContext(dbOptions);
        return await ResolveAsync(db, userId, ct);
    }

    public async Task<MembershipInfo> ResolveOrProvisionAsync(string userId, CancellationToken ct = default)
    {
        await using var db = new OrganizationsDbContext(dbOptions);

        var existing = await ResolveAsync(db, userId, ct);
        if (existing is not null)
            return existing;

        // Several requests can arrive at once for a user with no membership yet; without a lock
        // each would provision a whole org. Serialize per user (in-process: Orkka is one
        // process) and re-check inside the lock.
        ProvisionedOrg provisioned;
        await using (await UserLocks.AcquireAsync(userId, ct))
        {
            var afterLock = await ResolveAsync(db, userId, ct);
            if (afterLock is not null)
                return afterLock;

            var relational = db.Database.IsRelational();
            await using var tx = relational ? await db.Database.BeginTransactionAsync(ct) : null;
            provisioned = await ProvisionRootUserAsync(db, userId, ct);
            if (tx is not null)
                await tx.CommitAsync(ct);
        }

        // Audited only once the org is committed: the audit log opens its own context, so inside
        // the transaction it would block on it or fail to see the org at all.
        await AuditProvisionedAsync(provisioned, userId, ct);
        return provisioned.Membership;
    }

    /// <summary>One provisioning per user at a time.</summary>
    private static readonly KeyedLock<string> UserLocks = new(StringComparer.Ordinal);

    public async Task<MembershipInfo?> ResolveInOrgAsync(
        string userId, Ulid orgId, CancellationToken ct = default)
    {
        await using var db = new OrganizationsDbContext(dbOptions);
        return await ResolveAsync(db, userId, ct, orgId);
    }

    public async Task<IReadOnlyList<WorkspaceInfo>> ListWorkspacesAsync(
        string userId, CancellationToken ct = default)
    {
        await using var db = new OrganizationsDbContext(dbOptions);
        var activeOrgId = (await ResolveAsync(db, userId, ct))?.OrgId;
        return await db.OrgMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.Organization.Name)
            .Select(m => new WorkspaceInfo(
                m.OrgId, m.Organization.Name, m.Organization.Slug, m.Role,
                m.OrgId == activeOrgId))
            .ToListAsync(ct);
    }

    public async Task<WorkspaceInfo> CreateWorkspaceAsync(
        string userId, string name, CancellationToken ct = default)
    {
        await using var db = new OrganizationsDbContext(dbOptions);
        var provisioned = await ProvisionRootUserAsync(db, userId, ct, name.Trim());
        await AuditProvisionedAsync(provisioned, userId, ct);
        return new WorkspaceInfo(provisioned.OrgId, name.Trim(), provisioned.Slug, OrgRole.Owner,
            IsActive: true);
    }

    public async Task<bool> SetActiveWorkspaceAsync(string userId, Ulid orgId, CancellationToken ct = default)
    {
        await using var db = new OrganizationsDbContext(dbOptions);
        var updated = await db.OrgMembers
            .Where(m => m.UserId == userId && m.OrgId == orgId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ActivatedAt, DateTimeOffset.UtcNow), ct);
        return updated == 1;
    }

    private static async Task<MembershipInfo?> ResolveAsync(
        OrganizationsDbContext db, string userId, CancellationToken ct, Ulid? orgId = null)
    {
        // Anonymous projection: EF translates the nested permission collection in the same
        // round trip, which a record constructor projection would not.
        //
        // A user holds one membership per workspace. A request that names one (orgId) gets
        // exactly that; otherwise the most recently activated one wins, then the oldest.
        var member = await db.OrgMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId && (orgId == null || m.OrgId == orgId))
            .OrderByDescending(m => m.ActivatedAt != null)
            .ThenByDescending(m => m.ActivatedAt)
            .ThenBy(m => m.Id)
            .Select(m => new
            {
                m.Id,
                m.OrgId,
                m.Role,
                Permissions = m.Permissions
                    .Where(p => p.ProjectId == null)
                    .Select(p => p.Permission)
                    .ToList(),
                m.Preferences,
            })
            .FirstOrDefaultAsync(ct);

        if (member is null)
            return null;

        // Parsed after materialization: Preferences is a JSON blob EF cannot project through.
        // Normalized so a malformed or unsupported value falls back to the request header
        // rather than pinning the member to a language the platform does not speak.
        var locale = AgentLanguage.Normalize(
            Features.Members.MemberPreferences.Parse(member.Preferences).Locale);

        return new MembershipInfo(
            member.Id, member.OrgId, member.Role, member.Permissions, locale);
    }

    public async Task<Ulid?> GetOrgIdForUserAsync(string userId, CancellationToken ct = default)
    {
        await using var db = new OrganizationsDbContext(dbOptions);

        return await db.OrgMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            // Same order as ResolveAsync, or the two disagree about which org a user is in.
            .OrderByDescending(m => m.ActivatedAt != null)
            .ThenByDescending(m => m.ActivatedAt)
            .ThenBy(m => m.Id)
            .Select(m => (Ulid?)m.OrgId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>A freshly provisioned org, carried out of the transaction so the audit row can be written after it commits.</summary>
    private readonly record struct ProvisionedOrg(MembershipInfo Membership, Ulid OrgId, Ulid MemberId, string Slug);

    private async Task AuditProvisionedAsync(ProvisionedOrg provisioned, string userId, CancellationToken ct)
    {
        // First row of the org's audit chain: who created it and when the org came to be.
        if (audit is not null)
            await audit.RecordAsync(Orkka.Modules.Agents.Contracts.AgentAuditRecord.BySystem(
                provisioned.OrgId, Orkka.Modules.Agents.Contracts.AgentAuditEventType.AccountProvisioned,
                $"Organization provisioned for {userId}",
                metadata: new
                {
                    orgId = provisioned.OrgId.ToString(),
                    memberId = provisioned.MemberId.ToString(),
                    Slug = provisioned.Slug,
                },
                relatedUserId: userId), ct);
    }

    private async Task<ProvisionedOrg> ProvisionRootUserAsync(
        OrganizationsDbContext db, string userId, CancellationToken ct, string? name = null)
    {
        var org = new Organization
        {
            Name = string.IsNullOrWhiteSpace(name) ? Organization.DefaultName : name,
            Slug = GenerateSlug(userId),
        };
        db.Organizations.Add(org);
        await db.SaveChangesAsync(ct);

        var orgMember = new OrgMember
        {
            UserId = userId,
            Role = OrgRole.Owner,
            OrgId = org.Id,
            ActivatedAt = DateTimeOffset.UtcNow,
        };
        db.OrgMembers.Add(orgMember);
        await db.SaveChangesAsync(ct);

        return new ProvisionedOrg(
            new MembershipInfo(orgMember.Id, orgMember.OrgId, orgMember.Role, []),
            org.Id, orgMember.Id, org.Slug);
    }

    private static string GenerateSlug(string userId)
    {
        var sanitized = SlugRegex().Replace(userId.ToLower(CultureInfo.InvariantCulture), "-").Trim('-');
        var suffix = Ulid.NewUlid().ToString()[^8..].ToLower(CultureInfo.InvariantCulture);
        return $"{sanitized}-{suffix}";
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugRegex();
}
