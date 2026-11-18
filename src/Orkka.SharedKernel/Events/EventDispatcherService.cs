namespace Orkka.SharedKernel.Events;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Drains the <see cref="InProcessEventBus"/> channel and dispatches each event to all
/// registered <see cref="IIntegrationEventHandler{TEvent}"/> implementations, each in its
/// own DI scope. A failing handler is logged and never blocks other handlers or the queue.
/// </summary>
public sealed class EventDispatcherService(
    InProcessEventBus bus,
    IServiceScopeFactory scopeFactory,
    ILogger<EventDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var evt in bus.Reader.ReadAllAsync(stoppingToken))
        {
            var handlerType = typeof(IIntegrationEventHandler<>).MakeGenericType(evt.GetType());

            await using var scope = scopeFactory.CreateAsyncScope();
            var handlers = scope.ServiceProvider.GetServices(handlerType);

            foreach (var handler in handlers)
            {
                try
                {
                    var handleMethod = handlerType.GetMethod(nameof(IIntegrationEventHandler<IntegrationEvent>.HandleAsync))!;
                    await (Task)handleMethod.Invoke(handler, [evt, stoppingToken])!;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Integration event handler {Handler} failed for {Event} ({EventId})",
                        handler!.GetType().Name, evt.GetType().Name, evt.EventId);
                }
            }
        }
    }
}
