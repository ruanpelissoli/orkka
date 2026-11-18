namespace Orkka.SharedKernel.Events;

/// <summary>Implemented by modules to react to integration events published by other modules.</summary>
public interface IIntegrationEventHandler<in TEvent> where TEvent : IntegrationEvent
{
    Task HandleAsync(TEvent evt, CancellationToken ct);
}
