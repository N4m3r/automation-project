using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Web;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;

namespace SecurityPlatform.Drivers.Onvif;

/// <summary>
/// Driver ONVIF/RTSP — atende a maioria das cameras do mercado (Intelbras,
/// Hikvision, Dahua, Axis, Bosch) sem SDK proprietario. Drivers de SDK nativo
/// implementam a mesma interface e convivem com este.
///
/// PTZ: ContinuousMove / Stop / presets via SOAP (ver <see cref="OnvifPtzClient"/>).
/// </summary>
public class OnvifDriver : IDeviceDriver
{
    public string Name => "onvif";
    public DeviceKind[] Supports => [DeviceKind.Camera];

    private readonly OnvifPtzClient _ptz = new();

    /// <summary>Caminho RTSP por fabricante. Suportar marca nova = 1 linha.</summary>
    private static readonly Dictionary<string, string> RtspPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["intelbras"] = "/cam/realmonitor?channel=1&subtype=0",
        ["dahua"]     = "/cam/realmonitor?channel=1&subtype=0",
        ["hikvision"] = "/Streaming/Channels/101",
        ["axis"]      = "/axis-media/media.amp",
        ["bosch"]     = "/rtsp_tunnel",
        ["generic"]   = "/onvif1",
    };

    public async Task<bool> ConnectAsync(Device device, CancellationToken ct = default)
    {
        // HTTP (porta do cadastro) e RTSP 554: câmera só com streaming ainda conta online.
        if (await ProbeTcpAsync(device.Host, device.Port <= 0 ? 80 : device.Port, ct))
            return true;
        return await ProbeTcpAsync(device.Host, 554, ct);
    }

    private static async Task<bool> ProbeTcpAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task<string> GetStreamUrlAsync(Device device, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(device.StreamUrl))
            return Task.FromResult(device.StreamUrl);

        var vendor = RtspPaths.Keys.FirstOrDefault(v =>
            device.Name.Contains(v, StringComparison.OrdinalIgnoreCase)) ?? "generic";

        var cred = string.IsNullOrEmpty(device.Username)
            ? ""
            : $"{HttpUtility.UrlEncode(device.Username)}:{HttpUtility.UrlEncode(device.Password)}@";

        return Task.FromResult($"rtsp://{cred}{device.Host}:554{RtspPaths[vendor]}");
    }

    public async Task<DriverResult> CommandAsync(Device device, string action,
        IDictionary<string, string>? parameters = null, CancellationToken ct = default)
    {
        if (action.StartsWith("ptz", StringComparison.Ordinal))
            return await _ptz.CommandAsync(device, action, parameters, ct);

        return action switch
        {
            "snapshot" => DriverResult.Success(new Dictionary<string, string>
            {
                ["url"] = $"http://{device.Host}:{device.Port}/onvif/snapshot"
            }),
            _ => DriverResult.Fail($"acao '{action}' nao suportada pelo driver onvif")
        };
    }

    /// <summary>
    /// Supervisao de disponibilidade: gera device_online / device_offline.
    /// Substituir por ONVIF PullPoint quando o analytics do fabricante entrar.
    /// </summary>
    public async IAsyncEnumerable<DeviceEvent> StreamEventsAsync(
        Device device, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var wasOnline = true;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            var online = await ConnectAsync(device, ct);
            if (online == wasOnline) continue;

            wasOnline = online;
            yield return new DeviceEvent
            {
                TenantId = device.TenantId,
                DeviceId = device.Id,
                Type = online ? "device_online" : "device_offline",
                Severity = online ? 1 : 3,
                Payload = $"{{\"host\":\"{device.Host}\",\"port\":{device.Port}}}"
            };
        }
    }
}
