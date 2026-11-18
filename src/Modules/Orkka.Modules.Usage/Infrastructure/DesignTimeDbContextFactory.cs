namespace Orkka.Modules.Usage.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Orkka.SharedKernel.Persistence;

// Used only by `dotnet ef migrations add` — the database is never opened for migration
// scaffolding; only the provider and the history table matter.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<UsageDbContext>
{
    public UsageDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<UsageDbContext>();
        optionsBuilder.UseOrkkaSqlite<UsageDbContext>(
            SqliteDatabase.ConnectionStringFor("orkka-design-time.db"), UsageDbContext.Module);

        return new UsageDbContext(optionsBuilder.Options);
    }
}
