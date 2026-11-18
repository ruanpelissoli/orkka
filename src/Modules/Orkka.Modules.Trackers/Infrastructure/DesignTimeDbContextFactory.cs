namespace Orkka.Modules.Trackers.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Orkka.SharedKernel.Persistence;

// Used only by `dotnet ef migrations add` — the database is never opened for migration
// scaffolding; only the provider and the history table matter.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TrackersDbContext>
{
    public TrackersDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TrackersDbContext>();
        optionsBuilder.UseOrkkaSqlite<TrackersDbContext>(
            SqliteDatabase.ConnectionStringFor("orkka-design-time.db"), TrackersDbContext.Module);

        return new TrackersDbContext(optionsBuilder.Options);
    }
}
