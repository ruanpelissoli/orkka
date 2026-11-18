namespace Orkka.Modules.Organizations.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Orkka.SharedKernel.Persistence;

// Used only by `dotnet ef migrations add` — the database is never opened for migration
// scaffolding; only the provider and the history table matter.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OrganizationsDbContext>
{
    public OrganizationsDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<OrganizationsDbContext>();
        optionsBuilder.UseOrkkaSqlite<OrganizationsDbContext>(
            SqliteDatabase.ConnectionStringFor("orkka-design-time.db"), OrganizationsDbContext.Module);

        return new OrganizationsDbContext(optionsBuilder.Options);
    }
}
