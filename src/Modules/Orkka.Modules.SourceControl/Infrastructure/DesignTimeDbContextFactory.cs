namespace Orkka.Modules.SourceControl.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Orkka.SharedKernel.Persistence;

// Used only by `dotnet ef migrations add` — the database is never opened for migration
// scaffolding; only the provider and the history table matter.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SourceControlDbContext>
{
    public SourceControlDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SourceControlDbContext>();
        optionsBuilder.UseOrkkaSqlite<SourceControlDbContext>(
            SqliteDatabase.ConnectionStringFor("orkka-design-time.db"), SourceControlDbContext.Module);

        return new SourceControlDbContext(optionsBuilder.Options);
    }
}
