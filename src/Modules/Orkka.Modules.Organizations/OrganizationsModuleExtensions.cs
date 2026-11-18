namespace Orkka.Modules.Organizations;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orkka.Modules.Organizations.Contracts;
using Orkka.Modules.Organizations.Features.Notifications;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.Modules.Organizations.PublicApi;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;
using Orkka.SharedKernel.Persistence;

public static class OrganizationsModuleExtensions
{
    /// <summary>DbContext + migration registration.</summary>
    public static IServiceCollection AddOrganizationsPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<OrganizationsDbContext>(configuration, OrganizationsDbContext.Module);
        return services;
    }

    /// <summary>Full module: persistence + public contracts.</summary>
    public static IServiceCollection AddOrganizationsModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOrganizationsPersistence(configuration);
        services.AddScoped<IOrgMembershipService, OrgMembershipService>();
        // Org API keys (public REST + MCP): validated during authentication, before tenant context.
        services.AddScoped<IOrgApiKeyValidator, PublicApi.OrgApiKeyValidator>();

        services.AddMemoryCache();

        // Notifications: one publisher (row + realtime) for every producer.
        services.AddScoped<NotificationPublisher>();

        services.AddIntegrationEventHandler<TaskStatusChanged, Features.Notifications.TaskStatusChangedNotificationHandler>();
        services.AddIntegrationEventHandler<MilestoneCompleted, Features.Notifications.MilestoneCompletedNotificationHandler>();
        services.AddIntegrationEventHandler<TaskMergeAttemptFailed, Features.Notifications.TaskMergeFailedNotificationHandler>();
        services.AddIntegrationEventHandler<AgentChatReplyCompleted, Features.Notifications.AgentChatNotificationHandler>();
        services.AddIntegrationEventHandler<AutopilotSessionStopped, Features.Notifications.AutopilotStoppedNotificationHandler>();
        services.AddIntegrationEventHandler<ScheduleRunFailed, Features.Notifications.ScheduleFailedNotificationHandler>();
        services.AddIntegrationEventHandler<InteractiveApprovalRequested, Features.Notifications.ApprovalRequestedNotificationHandler>();
        services.AddIntegrationEventHandler<InteractiveApprovalSettled, Features.Notifications.ApprovalSettledNotificationHandler>();

        return services;
    }
}
