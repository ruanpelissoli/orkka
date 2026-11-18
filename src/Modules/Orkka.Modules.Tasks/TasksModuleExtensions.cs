namespace Orkka.Modules.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Hubs;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Tasks.Infrastructure.EventHandlers;
using Orkka.Modules.Tasks.Infrastructure.Services;
using Orkka.Modules.Tasks.PublicApi;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;
using Orkka.SharedKernel.Persistence;

public static class TasksModuleExtensions
{
    /// <summary>DbContext + migration registration only — used by the Migrator.</summary>
    public static IServiceCollection AddTasksPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<TasksDbContext>(configuration, TasksDbContext.Module);
        return services;
    }

    /// <summary>Full module: persistence, public contracts, task services, and event handlers. Used by both hosts.</summary>
    public static IServiceCollection AddTasksModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddTasksPersistence(configuration);

        services.AddScoped<ITasksApi, TasksApi>();

        // What the task owner's runner can serve. Coverage-unaware here; AddRunnersModule
        // replaces it with the resolver that reads the runner's last report.
        services.TryAddScoped<Orkka.Modules.Runners.Contracts.IRunnerCoverageResolver,
            Infrastructure.Services.UnknownRunnerCoverageResolver>();

        // Task numbers delegate to the Projects module (the counter lives on the project row).
        // Singleton: the agent's hook blocks inside one call while the human's click arrives on
        // another, so the pending prompt has to outlive both requests.
        services.AddSingleton<IInteractiveApprovalCoordinator,
            Infrastructure.Services.InteractiveApprovalCoordinator>();

        services.AddScoped<ITaskNumberService, TaskNumberService>();

        // AI-powered services (Anthropic Messages API — the client plumbing lives in SharedKernel).
        services.AddScoped<ITaskDecomposer, TaskDecomposerService>();
        // Executes one queued milestone plan. Registered in every host because the job row is
        // shared state — whichever host composes the sweep needs to be able to run one.
        services.AddScoped<MilestonePlanRunner>();
        services.AddScoped<ITaskDependencyAnalyzer, TaskDependencyAnalyzer>();
        services.AddScoped<ICrossRepoSplitter, CrossRepoSplitService>();

        // Forecasting is pure arithmetic over measured history — no model call, nothing to bill.
        services.AddScoped<ITaskForecaster, TaskForecaster>();
        services.AddScoped<ForecastRecorder>();
        services.AddScoped<ForecastCalibrationService>();

        services.AddScoped<MilestoneProgressUpdater>();

        // The one path a task takes out of Drafting — shared by the submit endpoint and the
        // autopilot sweep so the two can never drift apart.
        services.AddScoped<TaskSubmissionService>();
        // Board operations shared by endpoints, MCP tools and integrations.
        services.AddScoped<BoardOperations>();

        services.AddIntegrationEventHandler<ProjectDeleted, TaskDataCleanupOnProjectDeleted>();

        // Milestone status is derived from its tasks, so it is refreshed wherever those move.
        services.AddIntegrationEventHandler<TaskMerged, MilestoneProgressOnTaskMerged>();
        services.AddIntegrationEventHandler<TaskStatusChanged, MilestoneProgressOnTaskStatusChanged>();

        // Refreshes dependents' "waiting on" badges live: completion flips a blocker's badge
        // to "awaiting merge"; the merge is what actually satisfies the dependency.
        services.AddIntegrationEventHandler<TaskStatusChanged, DependentsRefreshOnTaskCompleted>();
        services.AddIntegrationEventHandler<TaskMerged, DependentsRefreshOnTaskMerged>();

        return services;
    }

    /// <summary>API-host additions: the SignalR-backed task notifier (TaskHub lives in this assembly).</summary>
    public static IServiceCollection AddTasksModuleApi(this IServiceCollection services)
    {
        services.AddSingleton<ITaskNotifier, TaskNotifier>();
        return services;
    }

    /// <summary>Worker-only background services. Call from the Worker host after AddTasksModule.</summary>
    public static IServiceCollection AddTasksModuleWorker(this IServiceCollection services)
    {
        services.AddHostedService<ForecastCalibrationProcessor>();
        services.AddTasksModuleAutoArchive();
        services.AddTasksModuleMilestonePlanning();
        return services;
    }

    /// <summary>
    /// The milestone-planning queue. Split out of <see cref="AddTasksModuleWorker"/> for the
    /// same reason auto-archive is: Orkka Local composes no worker host and runs its background
    /// work in the single process, and a Plan button that queues a job nobody ever picks up
    /// would be worse than the synchronous call it replaced.
    ///
    /// Deliberately NOT registered in the API host. Planning takes minutes, and an API pod's
    /// shutdown budget is sized for SSE turns measured in seconds — every rolling deploy would
    /// cut a run in half, which is the class of failure this queue exists to end.
    /// </summary>
    public static IServiceCollection AddTasksModuleMilestonePlanning(this IServiceCollection services)
    {
        services.AddHostedService<MilestonePlanQueueProcessor>();
        return services;
    }

    /// <summary>
    /// The Done-lane auto-archive sweep. Split out of <see cref="AddTasksModuleWorker"/> because
    /// Orkka Local composes no worker host — it runs the background work in the single process —
    /// and a per-project setting that quietly does nothing in one edition is worse than no setting.
    /// </summary>
    public static IServiceCollection AddTasksModuleAutoArchive(this IServiceCollection services)
    {
        services.AddHostedService<TaskAutoArchiveProcessor>();
        return services;
    }

    /// <summary>Maps the module's SignalR hub(s). Endpoints are discovered via FastEndpoints assembly scanning.</summary>
    public static IEndpointRouteBuilder MapTasksModule(this IEndpointRouteBuilder app)
    {
        app.MapHub<TaskHub>("/hubs/tasks");
        return app;
    }
}
