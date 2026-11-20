namespace Orkka.Modules.Trackers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orkka.Modules.Trackers.Contracts;
using Orkka.Modules.Trackers.Infrastructure;
using Orkka.Modules.Trackers.Infrastructure.EventHandlers;
using Orkka.Modules.Trackers.PublicApi;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;
using Orkka.SharedKernel.Persistence;

public static class TrackersModuleExtensions
{
    /// <summary>DbContext + migration registration only — used by the Migrator.</summary>
    public static IServiceCollection AddTrackersPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<TrackersDbContext>(configuration, TrackersDbContext.Module);
        return services;
    }

    /// <summary>Full module: persistence, the project ticket-source endpoints (AppHost) and the
    /// Hello-side connection upsert (Worker, which hosts the runner hub). The tracker queries
    /// relay through the Runners module's relay client, so a host composing this composes
    /// Runners too.</summary>
    public static IServiceCollection AddTrackersModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddTrackersPersistence(configuration);
        services.AddScoped<ITrackersApi, TrackersApi>();
        services.AddScoped<ITrackerImports, Features.Imports.TrackerImportService>();
        services.AddScoped<IRepoRouter, RunnerRepoRouter>();
        services.AddIntegrationEventHandler<ProjectDeleted, TrackerCleanupOnProjectDeleted>();
        return services;
    }
}
