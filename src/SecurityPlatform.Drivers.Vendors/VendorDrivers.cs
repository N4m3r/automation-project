using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml.Linq;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;

namespace SecurityPlatform.Drivers.Vendors;

/// <summary>Base HTTP Digest para Dahua / Intelbras / Axis (sem SDK).</summary>
public abstract class HttpVendorDriverBase : IDeviceDriver, IDisposable
{
    public abstract string Name { get; }
    public DeviceKind[] Supports => [DeviceKind.Camera];

    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// Porta do servidor HTTP CGI (snapshot, PTZ, configManager). Dahua/Intelbras
    /// expoem varias portas: 80 = HTTP, 554 = RTSP, 37777/37778 = SDK binario,
    /// 34567 = protocolo XM/JUAN. O usuario costuma cadastrar a porta do SDK
    /// (a que a interface da camera mostra como "Porta TCP"), mas as chamadas
    /// HTTP so funcionam na 80. Portas nao-HTTP conhecidas caem para 80; uma
    /// porta HTTP customizada (ex.: 8000) e preservada.
    /// </summary>
    protected static int HttpPort(Device d)
        => d.Port is <= 0 or 554 or 37777 or 37778 or 34567 ? 80 : d.Port;

    protected HttpClient Client(Device d)
    {
        var port = HttpPort(d);
        var key = $"{d.Id}|{d.Host}:{port}|{d.Username}";
        lock (_gate)
        {
            if (_clients.TryGetValue(key, out var c)) return c;
            var handler = new SocketsHttpHandler
            {
                Credentials = new NetworkCredential(d.Username, d.Password),
                PreAuthenticate = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10)
            };
            c = new HttpClient(handler)
            {
                BaseAddress = new Uri($"http://{d.Host}:{port}"),
                Timeout = TimeSpan.FromSeconds(10)
            };
            _clients[key] = c;
            return c;
        }
    }

    public virtual async Task<bool> ConnectAsync(Device device, CancellationToken ct = default)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            // Testa a porta HTTP (a mesma que os comandos usam), nao a do SDK.
            await tcp.ConnectAsync(device.Host, HttpPort(device), cts.Token);
            return true;
        }
        catch { return false; }
    }

    public abstract Task<string> GetStreamUrlAsync(Device device, CancellationToken ct = default);

    public abstract Task<DriverResult> CommandAsync(Device device, string action,
        IDictionary<string, string>? parameters = null, CancellationToken ct = default);

    public virtual async IAsyncEnumerable<DeviceEvent> StreamEventsAsync(
        Device device, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Online/offline por heartbeat TCP + (em Dahua) attach de eventManager.
        await foreach (var e in StreamVendorEventsAsync(device, ct))
            yield return e;
    }

    protected virtual async IAsyncEnumerable<DeviceEvent> StreamVendorEventsAsync(
        Device device, [EnumeratorCancellation] CancellationToken ct)
    {
        var was = true;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            var online = await ConnectAsync(device, ct);
            if (online == was) continue;
            was = online;
            yield return new DeviceEvent
            {
                TenantId = device.TenantId,
                DeviceId = device.Id,
                Type = online ? "device_online" : "device_offline",
                Severity = online ? 1 : 3,
                Payload = $"{{\"host\":\"{device.Host}\",\"driver\":\"{Name}\",\"meta\":{{\"kind\":\"other\",\"vendor\":\"{Name}\"}}}}"
            };
        }
    }

    protected static string Cred(Device d)
    {
        if (string.IsNullOrEmpty(d.Username)) return "";
        return $"{HttpUtility.UrlEncode(d.Username)}:{HttpUtility.UrlEncode(d.Password)}@";
    }

    protected static double Clamp(IDictionary<string, string>? p, string k)
    {
        if (p is null || !p.TryGetValue(k, out var v)) return 0;
        return double.TryParse(v, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? Math.Clamp(d, -1, 1) : 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var c in _clients.Values) c.Dispose();
            _clients.Clear();
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>Dahua — HTTP API + RTSP realmonitor / PTZ + eventManager attach.</summary>
public class DahuaDriver : HttpVendorDriverBase
{
    public override string Name => "dahua";

    /// <summary>
    /// Ultimo code PTZ continuo iniciado por dispositivo. O protocolo Dahua/
    /// Intelbras so para o movimento quando o <c>action=stop</c> usa o mesmo
    /// <c>code</c> que iniciou — sem isso a camera continua girando/zoom apos
    /// soltar o controle. Chave = Device.Id.
    /// </summary>
    private readonly ConcurrentDictionary<int, string> _lastPtzCode = new();

    public override Task<string> GetStreamUrlAsync(Device device, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(device.StreamUrl))
            return Task.FromResult(device.StreamUrl);
        return Task.FromResult(
            $"rtsp://{Cred(device)}{device.Host}:554/cam/realmonitor?channel=1&subtype=0");
    }

    /// <summary>
    /// Assina eventos smart Dahua (motion, crossLine, etc.) via eventManager.cgi attach.
    /// </summary>
    protected override async IAsyncEnumerable<DeviceEvent> StreamVendorEventsAsync(
        Device device, [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var reader = await OpenDahuaEventStreamAsync(device, ct);
            if (reader is not null)
            {
                await foreach (var evt in ReadDahuaEventStreamAsync(reader, device, ct))
                    yield return evt;
            }
            await Task.Delay(TimeSpan.FromSeconds(12), ct);
        }
    }

    private static async Task<StreamReader?> OpenDahuaEventStreamAsync(Device device, CancellationToken ct)
    {
        try
        {
            var handler = new SocketsHttpHandler
            {
                Credentials = new NetworkCredential(device.Username, device.Password),
                PreAuthenticate = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(30)
            };
            // HttpClient não é disposed aqui de propósito: o StreamReader mantém a conexão.
            // Em falha de leitura o GC coleta; reconexão cria outro.
            var http = new HttpClient(handler)
            {
                BaseAddress = new Uri($"http://{device.Host}:{(device.Port <= 0 ? 80 : device.Port)}"),
                Timeout = Timeout.InfiniteTimeSpan
            };
            var res = await http.GetAsync(
                "/cgi-bin/eventManager.cgi?action=attach&codes=[All]&heartbeat=5",
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode)
            {
                res.Dispose();
                http.Dispose();
                return null;
            }
            return new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        }
        catch
        {
            return null;
        }
    }

    private static async IAsyncEnumerable<DeviceEvent> ReadDahuaEventStreamAsync(
        StreamReader reader, Device device, [EnumeratorCancellation] CancellationToken ct)
    {
        using var _ = reader;
        var buf = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct); }
            catch { yield break; }
            if (line is null) yield break;
            buf.AppendLine(line);
            if (!line.Contains("Code=", StringComparison.OrdinalIgnoreCase) && buf.Length < 4000)
                continue;
            var block = buf.ToString();
            buf.Clear();
            var evt = ParseDahuaBlock(block, device);
            if (evt is not null) yield return evt;
        }
    }

    private static DeviceEvent? ParseDahuaBlock(string block, Device device)
    {
        // Ex.: Code=VideoMotion;action=Start;index=0
        var codeM = Regex.Match(block, @"Code=([A-Za-z0-9_]+)");
        if (!codeM.Success) return null;
        var raw = codeM.Groups[1].Value;
        if (block.Contains("action=Stop", StringComparison.OrdinalIgnoreCase))
            return null;

        var key = raw.ToLowerInvariant();
        var (type, severity, kind) = key switch
        {
            "videomotion" or "motiondetect" => ("motion", 2, "motion"),
            "crosslinedetection" or "crossline" or "linedetection" => ("line_crossing", 2, "line_crossing"),
            "crossregiondetection" or "intrusion" => ("intrusion", 3, "intrusion"),
            "facedetection" => ("face_detected", 2, "face"),
            "trafficejunction" or "trafficjunction" or "plate" or "anpr"
                or "trafficjunctionresult" => ("lpr_detected", 2, "lpr"),
            "humannumberstat" or "peoplecounting" or "numberstat" or "queuestat"
                => ("people_counting", 1, "people_counting"),
            "firewarning" or "smokedetection" or "heatimagingtemper" or "temperalarm"
                or "fire" or "smoke" => ("thermal", 3, "thermal"),
            "leftdetection" or "takenawaydetection" or "leftbaggage" or "abandonedobject"
                or "missingobject" => ("abandoned", 2, "abandoned"),
            "wanderdetection" or "loitering" or "parkingdetection"
                => ("loitering", 2, "loitering"),
            "alarmlocal" or "alarmin" => ("io_trigger", 2, "other"),
            _ => (key, 2, "other")
        };

        var plate = "";
        var pm = Regex.Match(block, @"PlateNumber['\""]?[:=]['\""]?([A-Za-z0-9]+)");
        if (pm.Success) plate = EventMetadata.NormalizePlate(pm.Groups[1].Value);

        var meta = new EventMetadata
        {
            Vendor = "dahua",
            RawType = raw,
            Kind = kind,
            Plate = string.IsNullOrEmpty(plate) ? null : plate,
            Description = raw
        };

        return new DeviceEvent
        {
            TenantId = device.TenantId,
            DeviceId = device.Id,
            Type = type,
            Severity = severity,
            Payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                vendor = "dahua",
                dahuaCode = raw,
                meta
            })
        };
    }

    public override async Task<DriverResult> CommandAsync(Device device, string action,
        IDictionary<string, string>? parameters = null, CancellationToken ct = default)
    {
        try
        {
            var http = Client(device);
            return action switch
            {
                "snapshot" => await SnapshotAsync(http,
                    "/cgi-bin/snapshot.cgi?channel=1", ct),
                "device_info" => await DeviceInfoAsync(http, ct),
                "ptz_move" => await PtzAsync(http, device, parameters, stop: false, ct),
                "ptz_stop" => await PtzAsync(http, device, null, stop: true, ct),
                "ptz_preset" => await GotoPresetAsync(http, parameters, ct),
                "ptz_preset_set" or "ptz_save_preset" => await SetPresetAsync(http, parameters, ct),
                "ptz_preset_list" => await ListPresetsAsync(http, ct),
                "talk_open" => await http.GetAsync("/cgi-bin/audio.cgi?action=startTalk", ct)
                    is { IsSuccessStatusCode: true }
                    ? DriverResult.Success()
                    : DriverResult.Fail("talk_open falhou (Dahua)"),
                "talk_close" => await http.GetAsync("/cgi-bin/audio.cgi?action=stopTalk", ct)
                    is { IsSuccessStatusCode: true }
                    ? DriverResult.Success()
                    : DriverResult.Fail("talk_close falhou"),
                "vca_configure" => await ConfigureVcaAsync(http, parameters, ct),
                "encode_configure" or "set_codec" => await ConfigureEncodeAsync(http, parameters, ct),
                _ => DriverResult.Fail($"ação '{action}' não suportada pelo driver dahua")
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return DriverResult.Fail($"câmera inacessível: {e.Message}");
        }
    }

    private static async Task<DriverResult> SnapshotAsync(HttpClient http, string path, CancellationToken ct)
    {
        var res = await http.GetAsync(path, ct);
        if (!res.IsSuccessStatusCode)
            return DriverResult.Fail($"HTTP {(int)res.StatusCode}");
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        return DriverResult.Success(new Dictionary<string, string>
        {
            ["contentType"] = "image/jpeg",
            ["base64"] = Convert.ToBase64String(bytes)
        });
    }

    private static async Task<DriverResult> DeviceInfoAsync(HttpClient http, CancellationToken ct)
    {
        var res = await http.GetAsync("/cgi-bin/magicBox.cgi?action=getSystemInfo", ct);
        if (!res.IsSuccessStatusCode)
            return DriverResult.Fail($"HTTP {(int)res.StatusCode}");
        var text = await res.Content.ReadAsStringAsync(ct);
        return DriverResult.Success(new Dictionary<string, string> { ["raw"] = text });
    }

    private async Task<DriverResult> PtzAsync(
        HttpClient http, Device device, IDictionary<string, string>? p, bool stop, CancellationToken ct)
    {
        // Dahua/Intelbras ptz.cgi: codes Up/Down/Left/Right + diagonais, ZoomTele/
        // ZoomWide. Em movimento reto arg2 = velocidade (1..8); nas diagonais
        // arg1 = velocidade vertical e arg2 = velocidade horizontal.
        if (stop)
        {
            // So para de fato quando o code do stop == code que iniciou. Sem o
            // ultimo code (ex.: reinicio do servico) para todos os eixos.
            var last = _lastPtzCode.TryRemove(device.Id, out var lc) ? lc : null;
            var codes = last is not null
                ? new[] { last }
                : new[] { "Left", "Right", "Up", "Down", "ZoomTele", "ZoomWide" };
            HttpResponseMessage? resp = null;
            foreach (var c in codes)
            {
                resp = await http.GetAsync(
                    $"/cgi-bin/ptz.cgi?action=stop&channel=0&code={c}&arg1=0&arg2=0&arg3=0", ct);
            }
            return resp is { IsSuccessStatusCode: true }
                ? DriverResult.Success()
                : DriverResult.Fail($"PTZ stop HTTP {(int)(resp?.StatusCode ?? 0)}");
        }

        var pan = Clamp(p, "pan");
        var tilt = Clamp(p, "tilt");
        var zoom = Clamp(p, "zoom");

        string code;
        int arg1 = 0, arg2;
        if (Math.Abs(zoom) > 0.1)
        {
            code = zoom > 0 ? "ZoomTele" : "ZoomWide";
            arg2 = Speed(zoom);
        }
        else if (Math.Abs(pan) > 0.1 && Math.Abs(tilt) > 0.1)
        {
            // Diagonal: combina pan + tilt para joystick suave.
            code = (tilt >= 0 ? "Up" : "Down") + (pan >= 0 ? "Right" : "Left");
            arg1 = Speed(tilt);            // velocidade vertical
            arg2 = Speed(pan);             // velocidade horizontal
        }
        else if (Math.Abs(pan) >= Math.Abs(tilt))
        {
            code = pan >= 0 ? "Right" : "Left";
            arg2 = Speed(pan);
        }
        else
        {
            code = tilt >= 0 ? "Up" : "Down";
            arg2 = Speed(tilt);
        }

        if (code is "Up" or "Down" or "Left" or "Right" &&
            Math.Abs(pan) < 0.05 && Math.Abs(tilt) < 0.05 && Math.Abs(zoom) < 0.05)
            return DriverResult.Fail("PTZ move sem eixo (pan/tilt/zoom = 0)");

        _lastPtzCode[device.Id] = code;
        var url = $"/cgi-bin/ptz.cgi?action=start&channel=0&code={code}&arg1={arg1}&arg2={arg2}&arg3=0";
        var res = await http.GetAsync(url, ct);
        if (res.IsSuccessStatusCode) return DriverResult.Success();
        _lastPtzCode.TryRemove(device.Id, out _);
        return DriverResult.Fail($"PTZ HTTP {(int)res.StatusCode}");
    }

    /// <summary>Mapeia velocidade normalizada -1..1 para a escala Dahua 1..8.</summary>
    private static int Speed(double normalized)
        => (int)Math.Clamp(Math.Round(Math.Abs(normalized) * 8), 1, 8);

    private static async Task<DriverResult> ListPresetsAsync(HttpClient http, CancellationToken ct)
    {
        // getPresets devolve: list.preset[N].name=... / .presetId=...
        var res = await http.GetAsync("/cgi-bin/ptz.cgi?action=getPresets&channel=0", ct);
        if (!res.IsSuccessStatusCode)
            return DriverResult.Fail($"preset list HTTP {(int)res.StatusCode}");
        var text = await res.Content.ReadAsStringAsync(ct);
        var map = ParsePresets(text);
        return DriverResult.Success(map);
    }

    /// <summary>Parser publico para testes — presets Dahua/Intelbras (getPresets).</summary>
    public static Dictionary<string, string> ParsePresets(string text)
    {
        // Agrupa por indice N em list.preset[N].(Name|PresetID|Index)
        var byIdx = new Dictionary<int, (string? Name, string? Id)>();
        foreach (Match m in Regex.Matches(text,
                     @"preset\[(\d+)\]\.(Name|PresetID|PresetId|Index)\s*=\s*(.+)",
                     RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            var idx = int.Parse(m.Groups[1].Value);
            var key = m.Groups[2].Value.ToLowerInvariant();
            var val = m.Groups[3].Value.Trim().TrimEnd('\r');
            byIdx.TryGetValue(idx, out var cur);
            byIdx[idx] = key == "name" ? (val, cur.Id) : (cur.Name, val);
        }

        var map = new Dictionary<string, string>();
        foreach (var (idx, v) in byIdx.OrderBy(x => x.Key))
        {
            var id = string.IsNullOrWhiteSpace(v.Id) ? (idx + 1).ToString() : v.Id!.Trim();
            var name = string.IsNullOrWhiteSpace(v.Name) ? $"Preset {id}" : v.Name!.Trim();
            map[id] = name;
        }
        return map;
    }

    private static async Task<DriverResult> GotoPresetAsync(
        HttpClient http, IDictionary<string, string>? p, CancellationToken ct)
    {
        var preset = p is not null && p.TryGetValue("preset", out var pr) ? pr : "1";
        var res = await http.GetAsync(
            $"/cgi-bin/ptz.cgi?action=start&channel=0&code=GotoPreset&arg1=0&arg2={preset}&arg3=0", ct);
        return res.IsSuccessStatusCode
            ? DriverResult.Success(new Dictionary<string, string> { ["preset"] = preset })
            : DriverResult.Fail($"preset HTTP {(int)res.StatusCode}");
    }

    private static async Task<DriverResult> SetPresetAsync(
        HttpClient http, IDictionary<string, string>? p, CancellationToken ct)
    {
        var preset = p is not null && p.TryGetValue("preset", out var pr) ? pr : "1";
        var res = await http.GetAsync(
            $"/cgi-bin/ptz.cgi?action=start&channel=0&code=SetPreset&arg1=0&arg2={preset}&arg3=0", ct);
        return res.IsSuccessStatusCode
            ? DriverResult.Success(new Dictionary<string, string> { ["preset"] = preset })
            : DriverResult.Fail($"set preset HTTP {(int)res.StatusCode}");
    }

    /// <summary>
    /// Reconfigura o codec de video na propria camera (Dahua/Intelbras) via
    /// configManager. Necessario porque navegadores nao decodificam H.265/HEVC
    /// no ao vivo: para o grid, o substream deve ser H.264.
    /// Parametros: <c>codec</c> (H264|H265|MJPEG), <c>stream</c> (main|sub|both),
    /// <c>channel</c> (0 = camera IP avulsa).
    /// </summary>
    private static async Task<DriverResult> ConfigureEncodeAsync(
        HttpClient http, IDictionary<string, string>? p, CancellationToken ct)
    {
        p ??= new Dictionary<string, string>();
        var comp = (p.TryGetValue("codec", out var c) ? c : "H264").ToUpperInvariant() switch
        {
            "H265" or "HEVC" or "H.265" => "H.265",
            "MJPEG" or "JPEG" or "MJPG" => "MJPEG",
            _ => "H.264"
        };
        var stream = (p.TryGetValue("stream", out var s) ? s : "sub").ToLowerInvariant();
        var ch = p.TryGetValue("channel", out var chS) && int.TryParse(chS, out var chN) ? chN : 0;

        var parts = new List<string>();
        if (stream is "sub" or "both" or "extra")
            parts.Add($"Encode[{ch}].ExtraFormat[0].Video.Compression={comp}");
        if (stream is "main" or "both")
            parts.Add($"Encode[{ch}].MainFormat[0].Video.Compression={comp}");
        if (parts.Count == 0)
            return DriverResult.Fail($"stream '{stream}' invalido (use main, sub ou both)");

        var url = "/cgi-bin/configManager.cgi?action=setConfig&" + string.Join("&", parts);
        var res = await http.GetAsync(url, ct);
        return res.IsSuccessStatusCode
            ? DriverResult.Success(new Dictionary<string, string>
            {
                ["codec"] = comp, ["stream"] = stream
            })
            : DriverResult.Fail($"encode HTTP {(int)res.StatusCode}");
    }

    /// <summary>
    /// Push basico de VCA Dahua/Intelbras via configManager (motion + IVS region).
    /// ROI 0-1 e convertida para grade 0-8191.
    /// </summary>
    private static async Task<DriverResult> ConfigureVcaAsync(
        HttpClient http, IDictionary<string, string>? p, CancellationToken ct)
    {
        p ??= new Dictionary<string, string>();
        var rule = (p.TryGetValue("rule", out var r) ? r : "motion").ToLowerInvariant();
        var enabled = !p.TryGetValue("enabled", out var en) ||
                      !string.Equals(en, "false", StringComparison.OrdinalIgnoreCase);
        var on = enabled ? "true" : "false";

        static int Grid(IDictionary<string, string> d, string k, double def)
        {
            if (d.TryGetValue(k, out var s) &&
                double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                return (int)Math.Clamp(Math.Round(v * 8191), 0, 8191);
            return (int)Math.Clamp(Math.Round(def * 8191), 0, 8191);
        }

        string url = rule switch
        {
            "motion" or "vmd" =>
                $"/cgi-bin/configManager.cgi?action=setConfig&MotionDetect[0].Enable={on}",
            "intrusion" or "field" or "region" =>
                $"/cgi-bin/configManager.cgi?action=setConfig&VideoAnalyseRule[0][0].Enable={on}" +
                $"&VideoAnalyseRule[0][0].Type=CrossRegionDetection" +
                $"&VideoAnalyseRule[0][0].Config.DetectRegion[0][0]={Grid(p, "x", 0)}" +
                $"&VideoAnalyseRule[0][0].Config.DetectRegion[0][1]={Grid(p, "y", 0)}" +
                $"&VideoAnalyseRule[0][0].Config.DetectRegion[1][0]={Grid(p, "x", 0) + Grid(p, "w", 1)}" +
                $"&VideoAnalyseRule[0][0].Config.DetectRegion[1][1]={Grid(p, "y", 0)}" +
                $"&VideoAnalyseRule[0][0].Config.DetectRegion[2][0]={Grid(p, "x", 0) + Grid(p, "w", 1)}" +
                $"&VideoAnalyseRule[0][0].Config.DetectRegion[2][1]={Grid(p, "y", 0) + Grid(p, "h", 1)}" +
                $"&VideoAnalyseRule[0][0].Config.DetectRegion[3][0]={Grid(p, "x", 0)}" +
                $"&VideoAnalyseRule[0][0].Config.DetectRegion[3][1]={Grid(p, "y", 0) + Grid(p, "h", 1)}",
            "line_crossing" or "line" =>
                $"/cgi-bin/configManager.cgi?action=setConfig&VideoAnalyseRule[0][0].Enable={on}" +
                $"&VideoAnalyseRule[0][0].Type=CrossLineDetection",
            "people_counting" =>
                $"/cgi-bin/configManager.cgi?action=setConfig&NumberStat[0].Enable={on}",
            "abandoned" =>
                $"/cgi-bin/configManager.cgi?action=setConfig&LeftDetection[0].Enable={on}",
            "loitering" =>
                $"/cgi-bin/configManager.cgi?action=setConfig&WanderDetection[0].Enable={on}",
            "thermal" =>
                $"/cgi-bin/configManager.cgi?action=setConfig&HeatImagingTemper[0].Enable={on}",
            _ => ""
        };

        if (string.IsNullOrEmpty(url))
            return DriverResult.Fail(
                $"Regra VCA '{rule}' nao suportada no Dahua (motion, intrusion, line_crossing, people_counting, abandoned, loitering, thermal).");

        var res = await http.GetAsync(url, ct);
        return res.IsSuccessStatusCode
            ? DriverResult.Success(new Dictionary<string, string> { ["rule"] = rule, ["enabled"] = on })
            : DriverResult.Fail($"VCA Dahua HTTP {(int)res.StatusCode}");
    }
}

/// <summary>Intelbras — mesma família Dahua (API compatível na maioria das linhas).</summary>
public sealed class IntelbrasDriver : DahuaDriver
{
    public override string Name => "intelbras";
}

/// <summary>Axis — VAPIX + RTSP axis-media + event stream smart.</summary>
public sealed class AxisDriver : HttpVendorDriverBase
{
    public override string Name => "axis";

    public override Task<string> GetStreamUrlAsync(Device device, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(device.StreamUrl))
            return Task.FromResult(device.StreamUrl);
        return Task.FromResult(
            $"rtsp://{Cred(device)}{device.Host}:554/axis-media/media.amp");
    }

    /// <summary>
    /// Eventos VAPIX: tenta stream XML de event.cgi; fallback heartbeat online/offline.
    /// </summary>
    protected override async IAsyncEnumerable<DeviceEvent> StreamVendorEventsAsync(
        Device device, [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var reader = await OpenAxisEventStreamAsync(device, ct);
            if (reader is not null)
            {
                await foreach (var evt in ReadAxisEventStreamAsync(reader, device, ct))
                    yield return evt;
            }
            else
            {
                // Fallback: um ciclo de heartbeat
                var online = await ConnectAsync(device, ct);
                yield return new DeviceEvent
                {
                    TenantId = device.TenantId,
                    DeviceId = device.Id,
                    Type = online ? "device_online" : "device_offline",
                    Severity = online ? 1 : 3,
                    Payload = $"{{\"host\":\"{device.Host}\",\"driver\":\"axis\",\"meta\":{{\"kind\":\"other\",\"vendor\":\"axis\"}}}}"
                };
            }
            await Task.Delay(TimeSpan.FromSeconds(12), ct);
        }
    }

    private static async Task<StreamReader?> OpenAxisEventStreamAsync(Device device, CancellationToken ct)
    {
        try
        {
            var handler = new SocketsHttpHandler
            {
                Credentials = new NetworkCredential(device.Username, device.Password),
                PreAuthenticate = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(30)
            };
            var http = new HttpClient(handler)
            {
                BaseAddress = new Uri($"http://{device.Host}:{(device.Port <= 0 ? 80 : device.Port)}"),
                Timeout = Timeout.InfiniteTimeSpan
            };
            // VAPIX event stream (smart motion / analytics quando disponíveis)
            var paths = new[]
            {
                "/axis-cgi/eventstream.cgi",
                "/vapix/services?method=get&json=%7B%22apiVersion%22:%221.0%22%7D",
                "/axis-cgi/param.cgi?action=list&group=Properties.API.HTTP.Version"
            };
            foreach (var path in paths.Take(1))
            {
                try
                {
                    var res = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!res.IsSuccessStatusCode)
                    {
                        res.Dispose();
                        continue;
                    }
                    return new StreamReader(await res.Content.ReadAsStreamAsync(ct));
                }
                catch { /* tenta próximo */ }
            }
            http.Dispose();
            return null;
        }
        catch { return null; }
    }

    private static async IAsyncEnumerable<DeviceEvent> ReadAxisEventStreamAsync(
        StreamReader reader, Device device, [EnumeratorCancellation] CancellationToken ct)
    {
        using var _ = reader;
        var buf = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct); }
            catch { yield break; }
            if (line is null) yield break;
            buf.AppendLine(line);
            // Blocos XML ou linhas com topic/source
            if (line.Contains("</tt:MetadataStream>", StringComparison.OrdinalIgnoreCase)
                || line.Contains("topic=", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Motion", StringComparison.OrdinalIgnoreCase)
                || buf.Length > 8000)
            {
                var block = buf.ToString();
                buf.Clear();
                var evt = ParseAxisBlock(block, device);
                if (evt is not null) yield return evt;
            }
        }
    }

    private static DeviceEvent? ParseAxisBlock(string block, Device device)
    {
        if (string.IsNullOrWhiteSpace(block)) return null;
        var lower = block.ToLowerInvariant();
        // Ignora ruído de heartbeat vazio
        if (lower.Contains("keepalive") && !lower.Contains("motion") && !lower.Contains("face")
            && !lower.Contains("line") && !lower.Contains("loitering"))
            return null;

        string type = "axis_event", kind = "other";
        int severity = 2;
        if (lower.Contains("motion") || lower.Contains("vmd"))
        { type = "motion"; kind = "motion"; }
        else if (lower.Contains("linecrossing") || lower.Contains("line crossing") || lower.Contains("crossline"))
        { type = "line_crossing"; kind = "line_crossing"; severity = 2; }
        else if (lower.Contains("loitering") || lower.Contains("intrusion") || lower.Contains("fielddetection"))
        { type = "intrusion"; kind = "intrusion"; severity = 3; }
        else if (lower.Contains("face"))
        { type = "face_detected"; kind = "face"; }
        else if (lower.Contains("tamper") || lower.Contains("tampering"))
        { type = "tamper"; kind = "tamper"; severity = 3; }
        else if (lower.Contains("objectanalytics") || lower.Contains("object analytics"))
        { type = "object_analytics"; kind = "other"; }
        else if (!lower.Contains("topic") && !lower.Contains("<tt:") && !lower.Contains("event"))
            return null;

        var faceId = "";
        var fm = Regex.Match(block, @"faceId[""'=:\s]+([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase);
        if (fm.Success) faceId = fm.Groups[1].Value;

        // ROI aproximado se houver coordenadas
        EventRoi? roi = null;
        var xm = Regex.Match(block, @"\bx[""'=:\s]+([0-9.]+)", RegexOptions.IgnoreCase);
        var ym = Regex.Match(block, @"\by[""'=:\s]+([0-9.]+)", RegexOptions.IgnoreCase);
        var wm = Regex.Match(block, @"\b(?:w|width)[""'=:\s]+([0-9.]+)", RegexOptions.IgnoreCase);
        var hm = Regex.Match(block, @"\b(?:h|height)[""'=:\s]+([0-9.]+)", RegexOptions.IgnoreCase);
        if (wm.Success && hm.Success
            && double.TryParse(wm.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var w)
            && double.TryParse(hm.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var h)
            && w > 0 && h > 0)
        {
            double.TryParse(xm.Success ? xm.Groups[1].Value : "0", System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var x);
            double.TryParse(ym.Success ? ym.Groups[1].Value : "0", System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var y);
            // Axis muitas vezes usa 0–1 ou 0–1000
            if (w > 1 || h > 1) { x /= 1000; y /= 1000; w /= 1000; h /= 1000; }
            roi = new EventRoi(x, y, w, h);
        }

        var meta = new EventMetadata
        {
            Vendor = "axis",
            Kind = kind,
            RawType = type,
            FaceId = string.IsNullOrEmpty(faceId) ? null : faceId,
            Description = type,
            Roi = roi
        };

        return new DeviceEvent
        {
            TenantId = device.TenantId,
            DeviceId = device.Id,
            Type = type,
            Severity = severity,
            Payload = System.Text.Json.JsonSerializer.Serialize(new { vendor = "axis", meta })
        };
    }

    public override async Task<DriverResult> CommandAsync(Device device, string action,
        IDictionary<string, string>? parameters = null, CancellationToken ct = default)
    {
        try
        {
            var http = Client(device);
            return action switch
            {
                "snapshot" => await SnapshotJpeg(http, ct),
                "device_info" => await InfoAsync(http, ct),
                "ptz_move" => await PtzContinuous(http, parameters, stop: false, ct),
                "ptz_stop" => await PtzContinuous(http, null, stop: true, ct),
                "ptz_preset" => await GotoPreset(http, parameters, ct),
                "ptz_preset_set" or "ptz_save_preset" => await SetPreset(http, parameters, ct),
                "ptz_preset_list" => await ListPresets(http, ct),
                "talk_open" => DriverResult.Success(new Dictionary<string, string>
                {
                    ["hint"] = "Use POST /api/vms/cameras/{id}/talk com áudio G.711"
                }),
                "talk_close" => DriverResult.Success(),
                _ => DriverResult.Fail($"ação '{action}' não suportada pelo driver axis")
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return DriverResult.Fail($"câmera inacessível: {e.Message}");
        }
    }

    private static async Task<DriverResult> SnapshotJpeg(HttpClient http, CancellationToken ct)
    {
        var res = await http.GetAsync("/axis-cgi/jpg/image.cgi", ct);
        if (!res.IsSuccessStatusCode) return DriverResult.Fail($"HTTP {(int)res.StatusCode}");
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        return DriverResult.Success(new Dictionary<string, string>
        {
            ["contentType"] = "image/jpeg",
            ["base64"] = Convert.ToBase64String(bytes)
        });
    }

    private static async Task<DriverResult> InfoAsync(HttpClient http, CancellationToken ct)
    {
        var res = await http.GetAsync("/axis-cgi/basicdeviceinfo.cgi", ct);
        if (!res.IsSuccessStatusCode) return DriverResult.Fail($"HTTP {(int)res.StatusCode}");
        return DriverResult.Success(new Dictionary<string, string>
        {
            ["raw"] = await res.Content.ReadAsStringAsync(ct)
        });
    }

    private static async Task<DriverResult> PtzContinuous(
        HttpClient http, IDictionary<string, string>? p, bool stop, CancellationToken ct)
    {
        double pan = 0, tilt = 0, zoom = 0;
        if (!stop)
        {
            pan = Clamp(p, "pan") * 100;
            tilt = Clamp(p, "tilt") * 100;
            zoom = Clamp(p, "zoom") * 100;
        }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var qs = $"continuouspantiltmove={pan.ToString("0", inv)},{tilt.ToString("0", inv)}" +
                 $"&continuouszoommove={zoom.ToString("0", inv)}";
        var res = await http.GetAsync($"/axis-cgi/com/ptz.cgi?{qs}", ct);
        return res.IsSuccessStatusCode ? DriverResult.Success() : DriverResult.Fail($"PTZ HTTP {(int)res.StatusCode}");
    }

    private static async Task<DriverResult> GotoPreset(
        HttpClient http, IDictionary<string, string>? p, CancellationToken ct)
    {
        var preset = p is not null && p.TryGetValue("preset", out var pr) ? pr : "1";
        var res = await http.GetAsync($"/axis-cgi/com/ptz.cgi?gotoserverpresetno={preset}", ct);
        return res.IsSuccessStatusCode
            ? DriverResult.Success(new Dictionary<string, string> { ["preset"] = preset })
            : DriverResult.Fail($"preset HTTP {(int)res.StatusCode}");
    }

    private static async Task<DriverResult> SetPreset(
        HttpClient http, IDictionary<string, string>? p, CancellationToken ct)
    {
        var preset = p is not null && p.TryGetValue("preset", out var pr) ? pr : "1";
        var name = p is not null && p.TryGetValue("name", out var n) ? n : $"Preset {preset}";
        var res = await http.GetAsync(
            $"/axis-cgi/com/ptz.cgi?setserverpresetno={preset}&setserverpresetname={Uri.EscapeDataString(name)}", ct);
        return res.IsSuccessStatusCode
            ? DriverResult.Success(new Dictionary<string, string> { ["preset"] = preset, ["name"] = name })
            : DriverResult.Fail($"set preset HTTP {(int)res.StatusCode}");
    }

    private static async Task<DriverResult> ListPresets(HttpClient http, CancellationToken ct)
    {
        var res = await http.GetAsync("/axis-cgi/param.cgi?action=list&group=PTZ.Preset", ct);
        if (!res.IsSuccessStatusCode)
            return DriverResult.Success(new Dictionary<string, string>());
        var text = await res.Content.ReadAsStringAsync(ct);
        var map = new Dictionary<string, string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // PTZ.Preset.P0.Name=Home
            var parts = line.Split('=', 2);
            if (parts.Length != 2) continue;
            if (!parts[0].Contains(".Name", StringComparison.OrdinalIgnoreCase)) continue;
            var id = parts[0].Split('.').FirstOrDefault(s => s.StartsWith('P'))?.TrimStart('P') ?? map.Count.ToString();
            map[id] = parts[1].Trim();
        }
        return DriverResult.Success(map);
    }
}

/// <summary>Uniview / UNV � API HTTP semelhante a Dahua em muitas linhas Lapi.</summary>
public sealed class UniviewDriver : DahuaDriver
{
    public override string Name => "uniview";

    public override Task<string> GetStreamUrlAsync(Device device, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(device.StreamUrl))
            return Task.FromResult(device.StreamUrl);
        // LAPI/RTSP comum Uniview
        return Task.FromResult(
            $"rtsp://{Cred(device)}{device.Host}:554/media/video1");
    }
}

/// <summary>Bosch � ONVIF-first; URL RTSP t�pica CPP4/CPP6.</summary>
public sealed class BoschDriver : HttpVendorDriverBase
{
    public override string Name => "bosch";

    public override Task<string> GetStreamUrlAsync(Device device, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(device.StreamUrl))
            return Task.FromResult(device.StreamUrl);
        return Task.FromResult(
            $"rtsp://{Cred(device)}{device.Host}:554/rtsp_tunnel");
    }

    public override async Task<DriverResult> CommandAsync(Device device, string action,
        IDictionary<string, string>? parameters = null, CancellationToken ct = default)
    {
        try
        {
            var http = Client(device);
            return action switch
            {
                "snapshot" => await SnapshotAsync(http, "/snap.jpg", ct),
                "device_info" => await InfoAsync(http, ct),
                _ => DriverResult.Fail($"a��o '{action}' n�o suportada pelo driver bosch (use onvif p/ PTZ)")
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return DriverResult.Fail($"c�mera inacess�vel: {e.Message}");
        }
    }

    private static async Task<DriverResult> SnapshotAsync(HttpClient http, string path, CancellationToken ct)
    {
        var res = await http.GetAsync(path, ct);
        if (!res.IsSuccessStatusCode) return DriverResult.Fail($"HTTP {(int)res.StatusCode}");
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        return DriverResult.Success(new Dictionary<string, string>
        {
            ["contentType"] = "image/jpeg",
            ["base64"] = Convert.ToBase64String(bytes)
        });
    }

    private static async Task<DriverResult> InfoAsync(HttpClient http, CancellationToken ct)
    {
        var res = await http.GetAsync("/rcp.xml?command=0x002f&type=F_FLAG&direction=READ", ct);
        var text = res.IsSuccessStatusCode ? await res.Content.ReadAsStringAsync(ct) : "";
        return res.IsSuccessStatusCode
            ? DriverResult.Success(new Dictionary<string, string> { ["raw"] = text, ["vendor"] = "bosch" })
            : DriverResult.Fail($"HTTP {(int)res.StatusCode}");
    }
}

/// <summary>Samsung / Hanwha WiseNet � SUNAPI snapshot + RTSP profile.</summary>
public sealed class SamsungDriver : HttpVendorDriverBase
{
    public override string Name => "samsung";

    public override Task<string> GetStreamUrlAsync(Device device, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(device.StreamUrl))
            return Task.FromResult(device.StreamUrl);
        return Task.FromResult(
            $"rtsp://{Cred(device)}{device.Host}:554/profile2/media.smp");
    }

    public override async Task<DriverResult> CommandAsync(Device device, string action,
        IDictionary<string, string>? parameters = null, CancellationToken ct = default)
    {
        try
        {
            var http = Client(device);
            return action switch
            {
                "snapshot" => await Snap(http, ct),
                "device_info" => await Info(http, ct),
                "ptz_move" or "ptz_stop" or "ptz_preset" =>
                    DriverResult.Fail("PTZ Samsung: use driver onvif ou perfil nativo Hanwha."),
                _ => DriverResult.Fail($"a��o '{action}' n�o suportada pelo driver samsung")
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return DriverResult.Fail($"c�mera inacess�vel: {e.Message}");
        }
    }

    private static async Task<DriverResult> Snap(HttpClient http, CancellationToken ct)
    {
        foreach (var path in new[] { "/stw-cgi/video.cgi?msubmenu=snapshot&action=view", "/cgi-bin/video.cgi?msubmenu=jpg" })
        {
            var res = await http.GetAsync(path, ct);
            if (!res.IsSuccessStatusCode) continue;
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            return DriverResult.Success(new Dictionary<string, string>
            {
                ["contentType"] = "image/jpeg",
                ["base64"] = Convert.ToBase64String(bytes)
            });
        }
        return DriverResult.Fail("snapshot SUNAPI falhou");
    }

    private static async Task<DriverResult> Info(HttpClient http, CancellationToken ct)
    {
        var res = await http.GetAsync("/stw-cgi/system.cgi?msubmenu=deviceinfo&action=view", ct);
        if (!res.IsSuccessStatusCode) return DriverResult.Fail($"HTTP {(int)res.StatusCode}");
        return DriverResult.Success(new Dictionary<string, string>
        {
            ["raw"] = await res.Content.ReadAsStringAsync(ct),
            ["vendor"] = "samsung"
        });
    }
}
