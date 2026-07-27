using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;

namespace SecurityPlatform.Drivers.HttpIo;

/// <summary>
/// Driver de I/O genérico por HTTP — relés, portas e saídas acionáveis via
/// webhook do fabricante ou de um gateway (Shelly, Node-RED, CLP).
///
/// Convenção de cadastro:
/// <list type="bullet">
/// <item><c>Host</c> + <c>Port</c> — base HTTP do dispositivo</item>
/// <item><c>StreamUrl</c> — path do relé (ex.: <c>/relay/0</c>) se preenchido</item>
/// <item>Comandos: <c>relay_on</c>, <c>relay_off</c>, <c>relay_pulse</c></item>
/// </list>
/// </summary>
public class HttpIoDriver : IDeviceDriver
{
    public string Name => "http-io";
    public DeviceKind[] Supports => [DeviceKind.AccessPoint, DeviceKind.AlarmPanel];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public async Task<bool> ConnectAsync(Device device, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Base(device));
            ApplyAuth(req, device);
            using var res = await Http.SendAsync(req, ct);
            return (int)res.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    public Task<string> GetStreamUrlAsync(Device device, CancellationToken ct = default)
        => Task.FromResult(""); // I/O não tem stream de vídeo

    public async Task<DriverResult> CommandAsync(
        Device device, string action,
        IDictionary<string, string>? parameters = null, CancellationToken ct = default)
    {
        parameters ??= new Dictionary<string, string>();
        var channel = parameters.TryGetValue("channel", out var ch) ? ch : "0";
        var path = string.IsNullOrWhiteSpace(device.StreamUrl)
            ? $"/relay/{channel}"
            : device.StreamUrl.Replace("{channel}", channel, StringComparison.OrdinalIgnoreCase);

        var (method, body) = action.ToLowerInvariant() switch
        {
            "relay_on" or "output_on" or "output" =>
                (HttpMethod.Post, BuildBody(parameters, on: true, channel)),
            "relay_off" or "output_off" =>
                (HttpMethod.Post, BuildBody(parameters, on: false, channel)),
            "relay_pulse" =>
                (HttpMethod.Post, BuildBody(parameters, on: true, channel,
                    pulseMs: parameters.TryGetValue("ms", out var ms) ? ms : "1000")),
            _ => (null as HttpMethod, null as string)
        };

        if (method is null)
            return DriverResult.Fail($"acao '{action}' nao suportada pelo driver http-io");

        try
        {
            var url = new Uri(new Uri(Base(device).TrimEnd('/') + "/"), path.TrimStart('/'));
            using var req = new HttpRequestMessage(method, url);
            ApplyAuth(req, device);
            if (body is not null)
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var res = await Http.SendAsync(req, ct);
            return res.IsSuccessStatusCode
                ? DriverResult.Success(new Dictionary<string, string>
                {
                    ["action"] = action,
                    ["channel"] = channel,
                    ["http"] = ((int)res.StatusCode).ToString()
                })
                : DriverResult.Fail($"I/O respondeu HTTP {(int)res.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return DriverResult.Fail($"I/O inacessivel: {e.Message}");
        }
    }

    public async IAsyncEnumerable<DeviceEvent> StreamEventsAsync(
        Device device, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Polling de disponibilidade — eventos de hardware viriam por webhook.
        var wasOnline = true;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
            var online = await ConnectAsync(device, ct);
            if (online == wasOnline) continue;
            wasOnline = online;
            yield return new DeviceEvent
            {
                TenantId = device.TenantId,
                DeviceId = device.Id,
                Type = online ? "device_online" : "device_offline",
                Severity = online ? 1 : 2,
                Payload = $"{{\"host\":\"{device.Host}\"}}"
            };
        }
    }

    private static string Base(Device d)
        => $"http://{d.Host}:{(d.Port <= 0 ? 80 : d.Port)}";

    private static void ApplyAuth(HttpRequestMessage req, Device d)
    {
        if (string.IsNullOrEmpty(d.Username)) return;
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{d.Username}:{d.Password}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    private static string BuildBody(
        IDictionary<string, string> p, bool on, string channel, string? pulseMs = null)
    {
        // Payload genérico; gateways costumam aceitar JSON flexível.
        var doc = new Dictionary<string, object?>
        {
            ["on"] = on,
            ["channel"] = channel,
            ["state"] = on ? "on" : "off"
        };
        if (pulseMs is not null) doc["pulse_ms"] = int.TryParse(pulseMs, out var ms) ? ms : 1000;
        if (p.TryGetValue("body", out var custom) && !string.IsNullOrWhiteSpace(custom))
            return custom;
        return JsonSerializer.Serialize(doc);
    }
}
