namespace Orkka.Modules.Usage.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Usage.Domain;
using Orkka.SharedKernel.Persistence;
using Orkka.SharedKernel.Security;

public sealed class UsageDbContext(
    DbContextOptions<UsageDbContext> options,
    ICurrentUser? currentUser = null) : ModuleDbContext(options, currentUser)
{
    public const string Module = "usage";
    public override string ModuleName => Module;

    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
}
