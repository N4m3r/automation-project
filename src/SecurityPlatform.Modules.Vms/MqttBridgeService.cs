using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Events;
using SecurityPlatform.Modules.Security;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Assina tópicos MQTT e publica <see cref="DeviceEvent"/> no barramento.
/// Payload JSON: { "type":"door_open", "deviceId":1, "severity":2, "payload":{...} }
/// ou texto livre → type=iot_message.
/// </summary>
public sealed class MqttBridgeService(
    IServiceScopeFactory scopes,
    IEventBus bus,
    IOptions<VmsOptions> opt,
    PlatformMetrics metrics,
    ILogger<MqttBridgeService> log) : BackgroundService
{
    private readonly MqttOptions _mqtt = opt.Value.Mqtt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_mqtt.Enabled || string.IsNullOrWhiteSpace(_mqtt.Host))
        {
            log.LogInformation("MQTT bridge desligado (Vms:Mqtt:Enabled=false).");
            return;
        }

        var factory = new MqttFactory();
        using var client = factory.CreateMqttClient();

        client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                var topic = e.ApplicationMessage.Topic ?? "";
                var text = e.ApplicationMessage.ConvertPayloadToString() ?? "";
                await HandleAsync(topic, text, stoppingToken);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Falha ao processar mensagem MQTT");
            }
        };

        var clientId = string.IsNullOrWhiteSpace(_mqtt.ClientId)
            ? "sp-" + Environment.MachineName
            : _mqtt.ClientId;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                {
                    var builder = new MqttClientOptionsBuilder()
                        .WithTcpServer(_mqtt.Host, _mqtt.Port)
                        .WithClientId(clientId)
                        .WithCleanSession();
                    if (!string.IsNullOrEmpty(_mqtt.Username))
                        builder.WithCredentials(_mqtt.Username, _mqtt.Password);

                    await client.ConnectAsync(builder.Build(), stoppingToken);
                    foreach (var topic in _mqtt.Topics.Where(t => !string.IsNullOrWhiteSpace(t)))
                    {
                        await client.SubscribeAsync(new MqttTopicFilterBuilder()
                            .WithTopic(topic.Trim())
                            .Build(), stoppingToken);
                    }
                    log.LogInformation("MQTT conectado em {Host}:{Port} topics={Topics}",
                        _mqtt.Host, _mqtt.Port, string.Join(",", _mqtt.Topics));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "MQTT reconnect em 10s");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }

        if (client.IsConnected)
            await client.DisconnectAsync();
    }

    private async Task HandleAsync(string topic, string text, CancellationToken ct)
    {
        string type = "iot_message";
        int severity = 1;
        int? deviceId = null;
        var payload = text;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var t)) type = t.GetString() ?? type;
            if (root.TryGetProperty("severity", out var s) && s.TryGetInt32(out var sev)) severity = sev;
            if (root.TryGetProperty("deviceId", out var d) && d.TryGetInt32(out var id)) deviceId = id;
            if (root.TryGetProperty("payload", out var p))
                payload = p.ValueKind == JsonValueKind.String ? p.GetString() ?? text : p.GetRawText();
            else
                payload = JsonSerializer.Serialize(new { topic, body = text, meta = new { kind = "other", vendor = "mqtt" } });
        }
        catch
        {
            payload = JsonSerializer.Serialize(new { topic, body = text, meta = new { kind = "other", vendor = "mqtt" } });
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var evt = new DeviceEvent
        {
            TenantId = _mqtt.TenantId,
            DeviceId = deviceId,
            Type = type,
            Severity = severity,
            Payload = payload.Length > 4000 ? payload[..4000] : payload,
            CreatedAt = DateTime.UtcNow
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync(ct);
        metrics.IncEvent();
        await bus.PublishAsync(evt, ct);
        log.LogDebug("MQTT {Topic} → event #{Id} {Type}", topic, evt.Id, type);
    }
}
