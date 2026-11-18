namespace Orkka.Modules.Projects.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Orkka.SharedKernel.Persistence;

// Used only by `dotnet ef migrations add` — the database is never opened for migration
// scaffolding; only the provider and the history table matter.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ProjectsDbContext>
{
    public ProjectsDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ProjectsDbContext>();
        optionsBuilder.UseOrkkaSqlite<ProjectsDbContext>(
            SqliteDatabase.ConnectionStringFor("orkka-design-time.db"), ProjectsDbContext.Module);

        return new ProjectsDbContext(optionsBuilder.Options);
    }
}
