namespace Orkka.Modules.SourceControl;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.SourceControl.Infrastructure;
using Orkka.Modules.SourceControl.Infrastructure.Services;
using Orkka.SharedKernel.Persistence;

public static class SourceControlModuleExtensions
{
    /// <summary>DbContext + migration registration only — used by the Migrator.</summary>
    public static IServiceCollection AddSourceControlPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<SourceControlDbContext>(configuration, SourceControlDbContext.Module);
        return services;
    }

    /// <summary>Full module: persistence + Orkka's pull requests + public contracts.</summary>
    public static IServiceCollection AddSourceControlModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSourceControlPersistence(configuration);

        services.AddScoped<IPullRequestService, OrkkaPullRequestService>();
        services.AddScoped<StoryLandingService>();
        services.AddScoped<IStoryPullRequests>(sp => sp.GetRequiredService<StoryLandingService>());

        // Replaced by a host that can actually reach the developer's machine (the runner
        // relay). Registered here so that resolving IPullRequestService never depends on a
        // host having done so.
        services.TryAddScoped<ILocalRepoGitOps, UnreachableLocalRepoGitOps>();
        services.TryAddScoped<ILocalRepoReader, UnreachableLocalRepoReader>();

        // The git engine's ledger, persisted: this module owns the table, so a host that
        // composes it gets rows instead of the engine's default log lines. The engine itself
        // (GitProcess, GitTopology) is registered by the host via AddOrkkaGit; the TryAdds
        // here only cover a composition that forgot.
        services.TryAddSingleton<Orkka.Git.GitProcess>();
        services.TryAddSingleton(new Orkka.Git.GitTopology(Orkka.Git.GitTopologies.Unknown));
        services.Replace(ServiceDescriptor.Singleton<Orkka.Git.IGitOperationLedger, DbGitOperationLedger>());
        services.AddHostedService<GitOperationLedgerWriter>();

        // Markdown assembly over the developer's runner reads — shared by task planning and the
        // cross-repo split. Scoped: the runner reader it relays through is.
        services.AddScoped<IRepoContextBuilder, RepoContextBuilder>();

        return services;
    }
}
