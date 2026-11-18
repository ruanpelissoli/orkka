namespace Orkka.Modules.Documents.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Orkka.SharedKernel.Persistence;

// Used only by `dotnet ef migrations add` — the database is never opened for migration
// scaffolding; only the provider and the history table matter.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DocumentsDbContext>
{
    public DocumentsDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<DocumentsDbContext>();
        optionsBuilder.UseOrkkaSqlite<DocumentsDbContext>(
            SqliteDatabase.ConnectionStringFor("orkka-design-time.db"), DocumentsDbContext.Module);

        return new DocumentsDbContext(optionsBuilder.Options);
    }
}
