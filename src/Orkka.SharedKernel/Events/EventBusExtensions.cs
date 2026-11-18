namespace Orkka.SharedKernel.Events;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class EventBusExtensions
{
    public static IServiceCollection AddInProcessEventBus(this IServiceCollection services)
    {
        services.TryAddSingleton<InProcessEventBus>();
        services.TryAddSingleton<IEventBus>(sp => sp.GetRequiredService<InProcessEventBus>());
        if (!services.Any(d => d.ImplementationType == typeof(EventDispatcherService)))
            services.AddHostedService<EventDispatcherService>();
        return services;
    }

    /// <summary>Registration sugar: <c>services.AddIntegrationEventHandler&lt;TaskSubmitted, NotifyOnTaskSubmitted&gt;()</c>.</summary>
    public static IServiceCollection AddIntegrationEventHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : IntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
        => services.AddScoped<IIntegrationEventHandler<TEvent>, THandler>();
}
