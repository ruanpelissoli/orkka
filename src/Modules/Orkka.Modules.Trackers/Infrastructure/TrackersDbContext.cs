namespace Orkka.Modules.Trackers.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Trackers.Domain;
using Orkka.SharedKernel.Persistence;
using Orkka.SharedKernel.Security;

public sealed class TrackersDbContext(
    DbContextOptions<TrackersDbContext> options,
    ICurrentUser? currentUser = null) : ModuleDbContext(options, currentUser)
{
    public const string Module = "trackers";
    public override string ModuleName => Module;

    public DbSet<TrackerConnection> Connections => Set<TrackerConnection>();
    public DbSet<ProjectTrackerConfig> ProjectConfigs => Set<ProjectTrackerConfig>();
    public DbSet<TrackerEpicMapping> EpicMappings => Set<TrackerEpicMapping>();
    public DbSet<TicketLink> TicketLinks => Set<TicketLink>();
    public DbSet<TrackerImport> Imports => Set<TrackerImport>();
}
