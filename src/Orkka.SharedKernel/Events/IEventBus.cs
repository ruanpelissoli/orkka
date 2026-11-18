namespace Orkka.SharedKernel.Events;

/// <summary>
/// In-process integration event bus.
///
/// LIMITATION: dispatch is per-process. An event published in AppHost is NOT seen by the
/// Worker and vice versa — cross-process coordination remains the shared database (polling),
/// exactly as before the modular refactor. Handlers run asynchronously on a background
/// dispatcher: publishing never blocks or fails the publisher, and delivery is
/// at-most-once-per-process (events are lost on process shutdown). Use it for reactions
/// (notifications, seeding, projections), not for state transitions.
///
/// The interface is transport-agnostic so a broker-backed implementation (Redis, RabbitMQ,
/// Azure Service Bus) can later make it cross-process when modules are extracted.
/// </summary>
public interface IEventBus
{
    ValueTask PublishAsync(IntegrationEvent evt, CancellationToken ct = default);
}
