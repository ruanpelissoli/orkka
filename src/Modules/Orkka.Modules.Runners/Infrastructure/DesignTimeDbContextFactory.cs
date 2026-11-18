namespace Orkka.Modules.Runners.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Orkka.SharedKernel.Persistence;

// Used only by `dotnet ef migrations add` — the database is never opened for migration
// scaffolding; only the provider and the history table matter.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RunnersDbContext>
{
    public RunnersDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<RunnersDbContext>();
        optionsBuilder.UseOrkkaSqlite<RunnersDbContext>(
            SqliteDatabase.ConnectionStringFor("orkka-design-time.db"), RunnersDbContext.Module);

        return new RunnersDbContext(optionsBuilder.Options);
    }
}
