namespace Orkka.Modules.Runners.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Runners.Domain;
using Orkka.SharedKernel.Persistence;
using Orkka.SharedKernel.Security;

public sealed class RunnersDbContext(
    DbContextOptions<RunnersDbContext> options,
    ICurrentUser? currentUser = null) : ModuleDbContext(options, currentUser)
{
    public const string Module = "runners";
    public override string ModuleName => Module;

    public DbSet<RunnerToken> RunnerTokens => Set<RunnerToken>();
    public DbSet<RunnerPresence> RunnerPresences => Set<RunnerPresence>();
    public DbSet<RunnerProviderSnapshot> RunnerProviderSnapshots => Set<RunnerProviderSnapshot>();
}
