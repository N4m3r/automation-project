using System.Threading.Channels;
using SecurityPlatform.Core.Domain;

namespace SecurityPlatform.Core.Events;

/// <summary>
/// Barramento de eventos. A implementacao in-memory atende a topologia
/// All-in-One; trocar por Redis/RabbitMQ no Distribuido e substituir o
/// registro no DI, sem tocar no codigo de negocio.
/// </summary>
public interface IEventBus
{
    ValueTask PublishAsync(DeviceEvent evt, CancellationToken ct = default);
    IAsyncEnumerable<DeviceEvent> SubscribeAsync(CancellationToken ct = default);
}

public class InMemoryEventBus : IEventBus
{
    private readonly List<Channel<DeviceEvent>> _subscribers = [];
    private readonly object _gate = new();

    public async ValueTask PublishAsync(DeviceEvent evt, CancellationToken ct = default)
    {
        Channel<DeviceEvent>[] snapshot;
        lock (_gate) snapshot = [.. _subscribers];

        foreach (var ch in snapshot)
            await ch.Writer.WriteAsync(evt, ct);
    }

    public async IAsyncEnumerable<DeviceEvent> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // DropOldest: um cliente lento nunca segura o barramento inteiro.
        var ch = Channel.CreateBounded<DeviceEvent>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

        lock (_gate) _subscribers.Add(ch);
        try
        {
            await foreach (var e in ch.Reader.ReadAllAsync(ct))
                yield return e;
        }
        finally
        {
            lock (_gate) _subscribers.Remove(ch);
        }
    }
}
