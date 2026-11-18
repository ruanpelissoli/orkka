namespace Orkka.Modules.Organizations.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Organizations.Domain;
using Orkka.SharedKernel.Persistence;
using Orkka.SharedKernel.Security;

public sealed class OrganizationsDbContext(
    DbContextOptions<OrganizationsDbContext> options,
    ICurrentUser? currentUser = null) : ModuleDbContext(options, currentUser)
{
    public const string Module = "organizations";
    public override string ModuleName => Module;

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrgMember> OrgMembers => Set<OrgMember>();
    public DbSet<MemberPermission> MemberPermissions => Set<MemberPermission>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<OrgApiKey> OrgApiKeys => Set<OrgApiKey>();
}
