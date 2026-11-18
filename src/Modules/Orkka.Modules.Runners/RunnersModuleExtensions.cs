namespace Orkka.Modules.Runners;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Runners.Infrastructure;
using Orkka.Modules.Runners.Infrastructure.Services;
using Orkka.Modules.Runners.PublicApi;
using Orkka.SharedKernel.Persistence;

public static class RunnersModuleExtensions
{
    /// <summary>DbContext + migration registration only — used by the Migrator.</summary>
    public static IServiceCollection AddRunnersPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<RunnersDbContext>(configuration, RunnersDbContext.Module);
        return services;
    }

    /// <summary>Full module: persistence, presence reads (IRunnerFleet), and the hub-side
    /// services (token screening, presence writes). Composed by the AppHost (endpoints)
    /// and the Worker (which hosts the runner hub).</summary>
    public static IServiceCollection AddRunnersModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddRunnersPersistence(configuration);

        services.AddScoped<IRunnerFleet, RunnerFleet>();
        services.AddScoped<IRunnerRelayLocator, RunnerRelayLocator>();

        // How a hosted process reaches a repository that is a folder on a user's machine.
        // Replaces the SourceControl module's "no route from here" default — a host that
        // composes runners can actually get there. Only local-path repositories ever reach
        // this; nothing about a GitHub repository changes.
        services.AddScoped<Orkka.Modules.SourceControl.Contracts.ILocalRepoGitOps,
            PublicApi.RelayLocalRepoGitOps>();
        // And the reads the Repo page makes of such a folder (tree, files, history).
        services.AddScoped<Orkka.Modules.SourceControl.Contracts.ILocalRepoReader,
            PublicApi.RelayLocalRepoReader>();

        services.AddHttpClient(RunnerRelay.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
            {
                client.DefaultRequestHeaders.Add("X-Worker-Key",
                    sp.GetRequiredService<IConfiguration>()["WorkerAuth:ApiKey"] ?? "");
                // Request/response ops only. Launching an editor is near-instant; anything
                // slower than this is a wedged runner, and waiting longer helps nobody.
                client.Timeout = TimeSpan.FromSeconds(30);
            });
        // Git on a folder on the user's machine (a synthetic PR's squash merge, diff, conflict
        // check): a clone-merge-push on a busy laptop is not an editor launch. The hop gets
        // the runner link's own per-op budget.
        services.AddHttpClient(PublicApi.RelayLocalRepoGitOps.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
            {
                client.DefaultRequestHeaders.Add("X-Worker-Key",
                    sp.GetRequiredService<IConfiguration>()["WorkerAuth:ApiKey"] ?? "");
                client.Timeout = PublicApi.RelayLocalRepoGitOps.Budget;
            });
        // Starting a project's app stack staggers process spawns on the runner; the worker
        // gives the op a 2-minute budget, so the relay hop must outlive it.
        services.AddHttpClient(Features.AppRun.AppRunRelay.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
            {
                client.DefaultRequestHeaders.Add("X-Worker-Key",
                    sp.GetRequiredService<IConfiguration>()["WorkerAuth:ApiKey"] ?? "");
                client.Timeout = TimeSpan.FromMinutes(3);
            });
        // Utility completions relayed to the user's runner. The runner gives a utility
        // generation a 15-minute op ceiling; the hop must outlive it or a long brief
        // synthesis is abandoned while still healthy.
        services.AddHttpClient(RelayUserRunnerUtility.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
            {
                client.DefaultRequestHeaders.Add("X-Worker-Key",
                    sp.GetRequiredService<IConfiguration>()["WorkerAuth:ApiKey"] ?? "");
                client.Timeout = TimeSpan.FromMinutes(16);
            });
        // API host: utility calls (run-config detection, …) reach the user's machine through
        // the relay — there is no platform-key fallback. Replace,
        // not TryAdd — AddSharedKernel registered the no-op default. The Worker replaces
        // this again with its in-process RunnerLinkUtility (it holds the sockets itself).
        services.Replace(ServiceDescriptor.Singleton<
            Orkka.SharedKernel.Ai.IUserRunnerUtility, RelayUserRunnerUtility>());
        services.AddScoped<RunnerHubAuth>();
        services.AddScoped<RunnerPresenceWriter>();
        // The Tasks module's default knows nothing about coverage; this one reads what the
        // runner last reported about the model's provider.
        services.Replace(ServiceDescriptor.Scoped<IRunnerCoverageResolver, RunnerCoverageResolver>());

        return services;
    }
}
