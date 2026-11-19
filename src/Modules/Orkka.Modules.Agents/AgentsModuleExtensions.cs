namespace Orkka.Modules.Agents;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Features.Mcp;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Agents.Infrastructure.EventHandlers;
using Orkka.Modules.Agents.Infrastructure.Services;
using Orkka.Modules.Agents.PublicApi;
using Orkka.SharedKernel.Ai;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;
using Orkka.SharedKernel.Persistence;

public static class AgentsModuleExtensions
{
    /// <summary>DbContext + migration registration only — used by the Migrator.</summary>
    public static IServiceCollection AddAgentsPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<AgentsDbContext>(configuration, AgentsDbContext.Module);
        return services;
    }

    /// <summary>Full module: persistence, public contracts, audit log, and event handlers. Used by both hosts.</summary>
    public static IServiceCollection AddAgentsModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAgentsPersistence(configuration);

        // Chat tool servers, built here rather than in AddAgentsModuleApi because the WORKER
        // is what resolves this: its chat relay rebuilds a relayed turn's tool server on the
        // host that has the database. Both hosts call this method; only one calls the API
        // additions — and a minimal-API parameter whose service is missing binds from the
        // request BODY instead, which is how every relayed chat turn 500'd on 2026-09-08.
        services.AddSingleton<Contracts.IChatToolServerFactory,
            Features.AgentChat.Tools.ChatToolServerFactory>();

        services.AddScoped<IAgentsApi, AgentsApi>();

        // The model catalog (agents.ai_models). The DB rate provider replaces SharedKernel's
        // null default so usage records are priced from the catalog.
        services.AddScoped<IModelCatalog, DbModelCatalog>();
        services.Replace(ServiceDescriptor.Scoped<IModelRateProvider, DbModelRateProvider>());
        services.AddHostedService<AiModelSeeder>();

        // Design-asset pipeline + the brand-kit read used by task prompts — registered in the
        // full module (not the API host) so the Worker can resolve them.
        services.AddScoped<DesignAssetGenerator>();
        services.AddScoped<IDesignGenerationApi, DesignGenerationApi>();
        // Singleton by design: writes in its own scope so callers (often singletons) can audit freely.
        services.AddSingleton<IAgentAuditLog, AgentAuditLog>();

        // TryAdd: the default where no developer machine is in view (never blocks). Task routes
        // carry their runner's own readiness; seeding reads the workspace's runner coverage
        // through the resolver below and only falls back to this when nothing is known.
        services.TryAddSingleton<IProviderReadiness, AlwaysReadyProviderReadiness>();
        // The workspace's AI provider choice (Settings → Workspace) + runner coverage → the
        // provider new agents are seeded on and system-picked work prefers.
        services.AddScoped<IWorkspaceAiProviderResolver, WorkspaceAiProviderResolver>();

        // Rolling-summary fold cadence (both editions; Orkka Local relaxes it — each fold
        // is a CLI spawn on the user's subscription there).
        services.AddOptions<ChatSummaryOptions>().BindConfiguration("AgentChat:Summary");

        services.AddIntegrationEventHandler<ProjectCreated, AgentSeedOnProjectCreated>();

        // One-shot startup sweep: tops existing projects up to the full always-seeded
        // roster when a new specialist role ships (idempotent; unique-index guarded).
        services.AddHostedService<AgentRosterBackfillService>();
        // Same idea for the built-in starter skills (SkillSeeder.BuiltIns).
        services.AddHostedService<SkillBackfillService>();
        services.AddIntegrationEventHandler<ProjectDeleted, AgentDataCleanupOnProjectDeleted>();
        services.AddIntegrationEventHandler<UsageRecorded, AgentCostOnUsageRecorded>();

        return services;
    }

    /// <summary>API-host additions: the chat workspace/streaming service used by the SSE chat endpoint.
    /// Singleton: stateless — configuration paths and the Anthropic key only. Registered via
    /// <see cref="IChatAgentInvoker"/> so alternative hosts can substitute a different invoker.</summary>
    public static IServiceCollection AddAgentsModuleApi(this IServiceCollection services)
    {
        // Every chat turn relays to the Worker pod holding the requester's runner connection;
        // nothing runs a turn in this process (customer AI never runs on Orkka's key).
        services.AddSingleton<IChatAgentInvoker, HybridChatAgentInvoker>();
        services.AddHttpClient(HybridChatAgentInvoker.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
            {
                client.DefaultRequestHeaders.Add("X-Worker-Key",
                    sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()
                        ["WorkerAuth:ApiKey"] ?? "");
                // Chat turns stream for minutes — the per-request CancellationToken is
                // the real lifetime control.
                client.Timeout = Timeout.InfiniteTimeSpan;
            });

        services.AddScoped<ChatContextResolver>();
        // Shared turn preparation: the chat endpoint and the schedule processor build
        // ChatTurnSpecs the same way.
        services.AddScoped<ChatTurnPreparer>();

        // Detached chat runs: the singleton coordinator owns run lifetimes; each run
        // resolves a fresh scoped ChatTurnRunner so persistence outlives the SSE request.
        services.AddSingleton<ChatRunCoordinator>();
        services.AddScoped<ChatTurnRunner>();

        // A task's Discuss chat: the same turn machinery, bound to the task.
        services.AddScoped<Features.TaskChat.TaskChatService>();
        services.AddScoped<Features.TaskChat.TaskChatContextBuilder>();

        // Schedules (playbooks, autopilot windows, reminders) tick in the host — the one
        // process, so there is no election; row leases still keep a firing single.
        services.AddScoped<Features.Schedules.ScheduleRunner>();
        services.AddHostedService<Features.Schedules.ScheduleProcessor>();

        // MCP endpoint (/mcp) — Orkka's board operations for Claude Code, Cursor and scripts.
        // Mapped by the host via MapOrkkaMcp().
        services.AddHttpContextAccessor();
        services.AddOrkkaMcp();
        return services;
    }
}
