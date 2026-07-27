using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Events;
using StackExchange.Redis;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Barramento distribuído via Redis Pub/Sub.
/// Canal padrão: <c>sp:events</c>. Envelope JSON: <c>{ node, event }</c>
/// para o publicador não reprocessar o próprio eco.
///
/// Connection string em <see cref="VmsOptions.EventBus"/>:
/// <c>redis://localhost:6379</c> ou <c>localhost:6379,abortConnect=false</c>.
/// </summary>
public sealed class RedisEventBus : IEventBus, IAsyncDisposable
{
    public const string DefaultChannel = "sp:events";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _nodeId;
    private readonly string _channel;
    private readonly ILogger<RedisEventBus> _log;
    private readonly List<Channel<DeviceEvent>> _subscribers = [];
    private readonly object _gate = new();

    private ConnectionMultiplexer? _mux;
    private ISubscriber? _sub;
    private int _connected;

    public RedisEventBus(IOptions<VmsOptions> options, ILogger<RedisEventBus> log)
    {
        _log = log;
        var opt = options.Value;
        _nodeId = opt.ResolveNodeId();
        _channel = DefaultChannel;

        var cs = NormalizeConnectionString(opt.EventBus);
        try
        {
            _mux = ConnectionMultiplexer.Connect(cs);
            _sub = _mux.GetSubscriber();
            _sub.Subscribe(RedisChannel.Literal(_channel), OnRedisMessage);
            Interlocked.Exchange(ref _connected, 1);
            _log.LogInformation(
                "EventBus Redis ativo — node={Node} channel={Channel} endpoints={Ep}",
                _nodeId, _channel, string.Join(",", _mux.GetEndPoints().Select(e => e.ToString())));
        }
        catch (Exception e)
        {
            _log.LogError(e,
                "Falha ao conectar Redis ({Cs}) — EventBus degrada para fan-out local apenas neste processo",
                cs);
            _mux = null;
            _sub = null;
            Interlocked.Exchange(ref _connected, 0);
        }
    }

    public bool IsConnected => Volatile.Read(ref _connected) == 1 && _mux is { IsConnected: true };

    public async ValueTask PublishAsync(DeviceEvent evt, CancellationToken ct = default)
    {
        // Sempre entrega local (mesmo processo / fallback sem Redis).
        await FanOutLocalAsync(evt, ct);

        if (_sub is null || !IsConnected) return;

        try
        {
            var envelope = new RedisEventEnvelope(_nodeId, evt);
            var json = JsonSerializer.Serialize(envelope, JsonOpts);
            await _sub.PublishAsync(RedisChannel.Literal(_channel), json);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Publish Redis falhou — evento ficou só local");
        }
    }

    public async IAsyncEnumerable<DeviceEvent> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var ch = Channel.CreateBounded<DeviceEvent>(
            new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });

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

    private void OnRedisMessage(RedisChannel channel, RedisValue value)
    {
        if (value.IsNullOrEmpty) return;
        try
        {
            var env = JsonSerializer.Deserialize<RedisEventEnvelope>(value!, JsonOpts);
            if (env?.Event is null) return;

            // Eco do próprio nó: já foi entregue em PublishAsync local.
            if (string.Equals(env.Node, _nodeId, StringComparison.OrdinalIgnoreCase))
                return;

            _ = FanOutLocalAsync(env.Event, CancellationToken.None);
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Mensagem Redis inválida no canal de eventos");
        }
    }

    private async ValueTask FanOutLocalAsync(DeviceEvent evt, CancellationToken ct)
    {
        Channel<DeviceEvent>[] snapshot;
        lock (_gate) snapshot = [.. _subscribers];
        foreach (var ch in snapshot)
        {
            try { await ch.Writer.WriteAsync(evt, ct); }
            catch (ChannelClosedException) { /* subscriber saiu */ }
        }
    }

    /// <summary>
    /// Aceita <c>redis://host:port</c>, <c>rediss://</c> (TLS) ou connection string StackExchange.
    /// </summary>
    public static string NormalizeConnectionString(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(s))
            return "localhost:6379,abortConnect=false";

        if (s.StartsWith("redis://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(s);
            var host = uri.Host;
            var port = uri.Port > 0 ? uri.Port : 6379;
            var ssl = s.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase);
            var user = Uri.UnescapeDataString(uri.UserInfo.Split(':').FirstOrDefault() ?? "");
            var pass = uri.UserInfo.Contains(':')
                ? Uri.UnescapeDataString(uri.UserInfo[(uri.UserInfo.IndexOf(':') + 1)..])
                : "";

            var parts = new List<string> { $"{host}:{port}", "abortConnect=false" };
            if (ssl) parts.Add("ssl=true");
            if (!string.IsNullOrEmpty(pass))
            {
                parts.Add($"password={pass}");
                if (!string.IsNullOrEmpty(user)) parts.Add($"user={user}");
            }
            return string.Join(",", parts);
        }

        if (!s.Contains("abortConnect", StringComparison.OrdinalIgnoreCase))
            s += ",abortConnect=false";
        return s;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_sub is not null)
                await _sub.UnsubscribeAsync(RedisChannel.Literal(_channel));
        }
        catch { /* */ }
        if (_mux is not null)
            await _mux.CloseAsync();
        _mux?.Dispose();
    }

    private sealed record RedisEventEnvelope(string Node, DeviceEvent Event);
}

/// <summary>Factory / helper de registro DI do EventBus.</summary>
public static class EventBusRegistration
{
    public static bool IsRedisConfigured(string? eventBus)
    {
        var s = (eventBus ?? "").Trim();
        if (string.IsNullOrEmpty(s)) return false;
        return s.StartsWith("redis", StringComparison.OrdinalIgnoreCase)
               || s.Contains(":6379", StringComparison.Ordinal)
               || s.Contains("abortConnect", StringComparison.OrdinalIgnoreCase);
    }
}
