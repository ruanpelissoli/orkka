namespace Orkka.Local;

using FastEndpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orkka.Git;
using Orkka.Local.Auth;
using Orkka.Local.Infrastructure;
using Orkka.Local.Storage;
using Orkka.Modules.Agents;
using Orkka.Modules.Usage;
using Orkka.Modules.Documents;
using Orkka.Modules.Execution;
using Orkka.Modules.Organizations;
using Orkka.Modules.Organizations.Features.ApiKeys;
using Orkka.Modules.Projects;
using Orkka.Modules.Runners;
using Orkka.Modules.SourceControl;
using Orkka.Modules.Tasks;
using Orkka.Modules.Trackers;
using Orkka.Runners.Server;
using Orkka.Runners.Server.Hosting;
using Orkka.SharedKernel;
using Orkka.SharedKernel.Events;

/// <summary>
/// Everything Orkka.Local registers, in one place so tests can compose exactly what the host
/// runs. One process: the API, the background engine (task queue, autopilot, planning), the
/// browsers' TaskHub and the runner's hub.
/// </summary>
public static class LocalComposition
{
    public const string DefaultScheme = "LocalBearer";

    public static IServiceCollection AddOrkkaLocal(
        this IServiceCollection services, IConfiguration configuration, LocalSettings settings)
    {
        services.AddSingleton(settings);
        services.AddHttpContextAccessor();

        // ── Modules ─────────────────────────────────────────────────────────────────
        services.AddSharedKernel(configuration);
        services.AddOrganizationsModule(configuration);
        services.AddUsageModule(configuration);
        services.AddOrkkaGit(GitTopologies.LocalHost);
        services.AddSourceControlModule(configuration);
        services.AddProjectsModule(configuration);
        services.AddProjectsModuleApi();
        services.AddAgentsModule(configuration);
        services.AddAgentsModuleApi();
        services.AddTasksModule(configuration);
        // The direct ITaskNotifier (IHubContext<TaskHub>): the hub lives in this process.
        services.AddTasksModuleApi();
        services.AddTasksModuleWorker();
        services.AddDocumentsModule(configuration);
        services.AddExecutionModuleApi();
        services.AddExecutionModuleCore(configuration);
        services.AddRunnersModule(configuration);
        services.AddTrackersModule(configuration);

        // ── Host services ───────────────────────────────────────────────────────────
        services.AddSingleton<IUserDirectory, LocalUserDirectory>();
        services.AddSingleton<BlobUrlSigner>();
        services.AddSingleton<FileSystemBlobStorageService>();
        services.AddSingleton<Orkka.SharedKernel.Storage.IBlobStorageService>(
            sp => sp.GetRequiredService<FileSystemBlobStorageService>());

        // ── SignalR: the browsers' TaskHub and the runner's hub ─────────────────────
        services.AddSignalR()
            // A paste into a Run terminal can be large; 1MB is headroom, not an open door.
            .AddHubOptions<Orkka.Modules.Tasks.Features.Hubs.TaskHub>(o =>
                o.MaximumReceiveMessageSize = 1024 * 1024)
            // Runner client results (file trees, diffs) can be multi-MB; SignalR's 32KB default
            // silently kills the connection when one exceeds it.
            .AddHubOptions<HostedRunnerHub>(o =>
            {
                o.MaximumReceiveMessageSize = 32 * 1024 * 1024;
                o.ClientTimeoutInterval = RunnerHubTimeouts.ClientTimeout;
                // Hub methods await client results from the same runner — see the constant.
                o.MaximumParallelInvocationsPerClient = RunnerHubTimeouts.MaxParallelInvocationsPerClient;
            });

        // ── The runner link ─────────────────────────────────────────────────────────
        services.AddSingleton<MultiTenantRunnerLinkProvider>(sp =>
            new MultiTenantRunnerLinkProvider(
                () => sp.GetRequiredService<IHubContext<HostedRunnerHub>>().Clients,
                sp.GetRequiredService<ILoggerFactory>(),
                new RunnerLinkMessages(
                    "Your runner is not connected. Start Orkka (`./orkka start`) and retry.",
                    "Your runner disconnected mid-run. Restart Orkka and retry the task.",
                    ProviderLoginRemedy: "Sign in to your AI CLI on this machine, then Retry.")));
        services.AddSingleton<IRunnerLinkProvider>(sp => sp.GetRequiredService<MultiTenantRunnerLinkProvider>());
        // Host-side halves of forwarded project-tool calls (HostedRunnerHub.CallProjectTool).
        services.AddSingleton<ProjectToolRegistry>();
        // Clones a workspace's repos onto the runner right after it connects.
        services.AddSingleton<RunnerRepoWarmer>();

        // App runs (the Run button): the orchestrator consumes the runner link's chunk channels;
        // its events go straight to the browsers' TaskHub group. Singleton: runs outlive requests.
        services.AddSingleton<Orkka.Runners.Server.AppRun.IAppRunEvents, LocalAppRunEvents>();
        services.AddSingleton<Orkka.Runners.Server.AppRun.AppRunOrchestrator>();
        services.AddIntegrationEventHandler<Orkka.SharedKernel.Events.Catalog.TaskMerged,
            Orkka.Runners.Server.AppRun.AppRunOnTaskMerged>();

        // Utility completions run on the runner in-process. Registered AFTER AddRunnersModule,
        // whose RelayUserRunnerUtility (an HTTP hop to a Worker) this replaces.
        services.Replace(ServiceDescriptor.Singleton<Orkka.SharedKernel.Ai.IUserRunnerUtility>(sp =>
            new RunnerLinkUtility(
                sp.GetRequiredService<IRunnerLinkProvider>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<RunnerLinkUtility>>())));

        // Every task streams to its owner's connected runner.
        services.AddSingleton<Orkka.Modules.Execution.Orchestration.IExecutionRouter>(sp =>
            new RunnerExecutionRouter(
                sp.GetRequiredService<IRunnerLinkProvider>(),
                sp.GetRequiredService<ProjectToolRegistry>(),
                sp.GetRequiredService<IGitOperationLedger>(),
                sp.GetRequiredService<ILoggerFactory>()));
        services.Replace(ServiceDescriptor.Singleton<Orkka.Modules.Runners.Contracts.IRunnerClaimAffinity>(sp =>
            new WorkerRunnerClaimAffinity(sp.GetRequiredService<MultiTenantRunnerLinkProvider>())));

        // ── Auth ────────────────────────────────────────────────────────────────────
        services.AddSingleton<LocalSessionStore>();
        services.AddSingleton<LaunchCodeStore>();
        // Default = a policy scheme: `Bearer orkka_key_…` → org API keys (MCP and scripts);
        // anything else → the local user (session cookie or install secret). Runner tokens
        // (`orkr_`) fall through to NoResult and are screened by HostedRunnerHub itself.
        services.AddAuthentication(DefaultScheme)
            .AddPolicyScheme(DefaultScheme, "Orkka local", options =>
            {
                options.ForwardDefaultSelector = context =>
                    OrgApiKeyAuthHandler.Matches(context.Request)
                        ? OrgApiKeyAuthHandler.SchemeName
                        : LocalAuthHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, OrgApiKeyAuthHandler>(OrgApiKeyAuthHandler.SchemeName, null)
            .AddScheme<AuthenticationSchemeOptions, LocalAuthHandler>(LocalAuthHandler.SchemeName, null);
        services.AddAuthorization(options =>
        {
            options.AddPolicy("HubAccess", policy =>
                policy.AddAuthenticationSchemes(LocalAuthHandler.SchemeName).RequireAuthenticatedUser());
        });
        services.AddScoped<ICurrentUser, CurrentUser>();

        services.AddFastEndpoints(o => o.Assemblies =
        [
            typeof(Orkka.Modules.Organizations.Infrastructure.OrganizationsDbContext).Assembly,
            typeof(Orkka.Modules.Usage.Infrastructure.UsageDbContext).Assembly,
            typeof(Orkka.Modules.SourceControl.Infrastructure.SourceControlDbContext).Assembly,
            typeof(Orkka.Modules.Projects.Infrastructure.ProjectsDbContext).Assembly,
            typeof(Orkka.Modules.Agents.Infrastructure.AgentsDbContext).Assembly,
            typeof(Orkka.Modules.Tasks.Infrastructure.TasksDbContext).Assembly,
            typeof(Orkka.Modules.Documents.Infrastructure.DocumentsDbContext).Assembly,
            typeof(ExecutionModuleExtensions).Assembly,
            typeof(Orkka.Modules.Runners.Infrastructure.RunnersDbContext).Assembly,
            typeof(Orkka.Modules.Trackers.Infrastructure.TrackersDbContext).Assembly,
        ]);

        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>(
            "database", tags: ["db"], failureStatus: HealthStatus.Degraded);
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails();
        services.Configure<RouteOptions>(options =>
            options.ConstraintMap.Add("ulid", typeof(UlidRouteConstraint)));

        return services;
    }
}
