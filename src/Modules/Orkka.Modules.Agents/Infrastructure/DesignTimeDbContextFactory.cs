namespace Orkka.Modules.Agents.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Orkka.SharedKernel.Persistence;

// Used only by `dotnet ef migrations add` — the database is never opened for migration
// scaffolding; only the provider and the history table matter.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AgentsDbContext>
{
    public AgentsDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AgentsDbContext>();
        optionsBuilder.UseOrkkaSqlite<AgentsDbContext>(
            SqliteDatabase.ConnectionStringFor("orkka-design-time.db"), AgentsDbContext.Module);

        return new AgentsDbContext(optionsBuilder.Options);
    }
}
