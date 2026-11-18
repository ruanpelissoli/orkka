namespace Orkka.Modules.Projects;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Logging;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Caching;
using Orkka.Modules.Projects.Infrastructure;
using Orkka.Modules.Projects.Infrastructure.Services;
using Orkka.Modules.Projects.PublicApi;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;
using Orkka.SharedKernel.Persistence;

public static class ProjectsModuleExtensions
{
    /// <summary>DbContext + migration registration only — used by the Migrator.</summary>
    public static IServiceCollection AddProjectsPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ProjectsDbContext>(configuration, ProjectsDbContext.Module);
        return services;
    }

    /// <summary>Full module: persistence, public contracts, and project-owned services. Used by both hosts.</summary>
    public static IServiceCollection AddProjectsModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddProjectsPersistence(configuration);

        services.AddScoped<IProjectsApi, ProjectsApi>();

        services.AddScoped<IProjectIdentifierService, ProjectIdentifierService>();
        services.AddScoped<Features.Projects.ProjectCreator>();

        // Both hosts: every repo-reading path goes through the synchronizer first, and those
        // paths are not API-only — IProjectsApi itself needs it, so registering it per-host
        // leaves one host unable to clone.
        services.AddScoped<Features.ProjectRepos.RepoCloneSynchronizer>();
        services.AddScoped<Features.ProjectRepos.RepoReaders>();

        // Both hosts: the Run panel reads it, and the local run hub turns it into processes.
        services.AddScoped<IRunConfigDetector, RunConfigDetector>();

        return services;
    }

    /// <summary>API-host additions: the request-scoped resolver and the cached read model.</summary>
    public static IServiceCollection AddProjectsModuleApi(this IServiceCollection services)
    {
        // Request-scoped: resolves slugs within the calling user's org (needs ICurrentUser).
        services.AddScoped<IProjectResolver, ProjectResolver>();

        // Cache project metadata in the API host only. Deliberately NOT in the Worker: it
        // runs without an ICurrentUser, so its reads bypass the tenant filter and span every
        // org — see CachedProjectsApi.
        services.AddScoped<ProjectsApi>();
        services.Replace(ServiceDescriptor.Scoped<IProjectsApi>(sp => new CachedProjectsApi(
            sp.GetRequiredService<ProjectsApi>(),
            sp.GetRequiredService<IOrkkaCache>(),
            sp.GetRequiredService<ICacheInvalidator>(),
            sp.GetRequiredService<ICurrentUser>())));

        return services;
    }
}
