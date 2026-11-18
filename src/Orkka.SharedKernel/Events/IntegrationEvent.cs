namespace Orkka.SharedKernel.Events;

/// <summary>
/// Base type for all cross-module integration events. Concrete events live in
/// <c>Orkka.SharedKernel.Events.Catalog</c> so any module can publish or subscribe
/// without referencing another module.
/// </summary>
public abstract record IntegrationEvent
{
    public Ulid EventId { get; init; } = Ulid.NewUlid();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
