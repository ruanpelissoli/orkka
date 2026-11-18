namespace Orkka.Modules.Tasks.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Orkka.SharedKernel.Persistence;

// Used only by `dotnet ef migrations add` — the database is never opened for migration
// scaffolding; only the provider and the history table matter.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TasksDbContext>
{
    public TasksDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TasksDbContext>();
        optionsBuilder.UseOrkkaSqlite<TasksDbContext>(
            SqliteDatabase.ConnectionStringFor("orkka-design-time.db"), TasksDbContext.Module);

        return new TasksDbContext(optionsBuilder.Options);
    }
}
