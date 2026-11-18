namespace Orkka.Modules.Execution;

using Orkka.Modules.Execution.Agents;
using Orkka.Modules.Execution.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class ExecutionModuleExtensions
{
    /// <summary>
    /// The Execution module's engine: orchestration, the task queue processor, and
    /// autopilot. Agents and git run on the developer's runner; this only drives
    /// them over the runner link (see IExecutionRouter, which the host registers).
    ///
    /// Registers NO <see cref="Orkka.Modules.Tasks.Contracts.ITaskNotifier"/>: the host holds the
    /// TaskHub and gets the direct notifier from AddTasksModuleApi.
    /// </summary>
    public static IServiceCollection AddExecutionModuleCore(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ExecutionContextOptions>(
            configuration.GetSection(ExecutionContextOptions.SectionName));
        services.Configure<DocumentationSyncOptions>(
            configuration.GetSection(DocumentationSyncOptions.SectionName));

        services.AddSingleton<IAgentAssigner, AgentAssigner>();
        services.AddSingleton<IMemoryExtractor, MemoryExtractor>();
        services.AddSingleton<ITaskOrchestrator, TaskOrchestrator>();
        // The Story context section for the task Discuss chat (Agents module), built here.
        services.AddScoped<Orkka.Modules.Agents.Contracts.ITaskStoryContextSource, TaskStoryContextSource>();
        services.AddSingleton<IDocumentationSyncService, DocumentationSyncService>();
        // Claim-affinity default (any runner claimable here). The host registers the
        // runner router (IExecutionRouter) and replaces this with its connection registry.
        services.AddSingleton<Orkka.Modules.Runners.Contracts.IRunnerClaimAffinity,
            AlwaysHereRunnerClaimAffinity>();

        // Background queue processor
        // Drain state. Nothing begins a drain in Orkka.Local, which keeps the plain
        // cancel-and-re-run stop; the type stays because the queue and orchestrator read it.
        services.AddSingleton<WorkerDrain>();
        services.AddHostedService<TaskQueueProcessor>();

        // Feeds the queue from Drafting while a session is open. Separate from the queue
        // processor on purpose: submission policy and execution are different jobs, and this
        // one must keep sweeping on a slower clock even when the queue is saturated.
        services.AddHostedService<AutopilotProcessor>();

        return services;
    }

    /// <summary>
    /// API-side composition of the Execution module. The module's FastEndpoints (agent-run
    /// callback ingestion) are discovered via the host's assembly registration; no additional
    /// services are required today.
    /// </summary>
    public static IServiceCollection AddExecutionModuleApi(this IServiceCollection services)
        => services;
}
