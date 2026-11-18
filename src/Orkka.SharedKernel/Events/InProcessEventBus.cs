namespace Orkka.SharedKernel.Events;

using System.Threading.Channels;

/// <summary>Channel-backed <see cref="IEventBus"/>; events are drained by <see cref="EventDispatcherService"/>.</summary>
public sealed class InProcessEventBus : IEventBus
{
    private readonly Channel<IntegrationEvent> _channel = Channel.CreateUnbounded<IntegrationEvent>(
        new UnboundedChannelOptions { SingleReader = true });

    internal ChannelReader<IntegrationEvent> Reader => _channel.Reader;

    public ValueTask PublishAsync(IntegrationEvent evt, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(evt, ct);
}
