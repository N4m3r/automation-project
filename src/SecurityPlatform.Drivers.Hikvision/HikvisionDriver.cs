using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;

namespace SecurityPlatform.Drivers.Hikvision;

/// <summary>
/// Driver nativo Hikvision via <b>ISAPI</b> (protocolo do fabricante).
///
/// Vantagens sobre ONVIF nesta linha de equipamento:
/// - modelo e firmware reais, número de canais e capacidades;
/// - PTZ com presets, patrulha e foco;
/// - eventos nativos por <c>alertStream</c> (VMD, line crossing, intrusão,
///   rosto, LPR) que o ONVIF Profile S não expõe;
/// - snapshot direto, sem renegociar mídia.
/// </summary>
public class HikvisionDriver(ILogger<HikvisionDriver> log) : IDeviceDriver, IDisposable
{
    public string Name => "hikvision";
    public DeviceKind[] Supports => [DeviceKind.Camera];

    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    /// <summary>
    /// ISAPI autentica por Digest (algumas linhas aceitam Basic). O handler com
    /// <see cref="NetworkCredential"/> negocia os dois automaticamente.
    ///
    /// Os clientes são cacheados por dispositivo+credencial: criar um HttpClient
    /// por requisição esgotaria as portas TCP em instalações com muitas câmeras.
    /// Nunca descarte o cliente devolvido aqui.
    /// </summary>
    private HttpClient Client(Device d, bool longLived = false)
    {
        var key = $"{d.Id}|{d.Host}:{d.Port}|{d.Username}|{d.Password.GetHashCode()}|{longLived}";

        return _clients.GetOrAdd(key, _ =>
        {
            var handler = new SocketsHttpHandler
            {
                Credentials = new NetworkCredential(d.Username, d.Password),
                PreAuthenticate = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10)
            };

            return new HttpClient(handler)
            {
                BaseAddress = new Uri($"http://{d.Host}:{(d.Port == 0 ? 80 : d.Port)}"),
                Timeout = longLived ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(10)
            };
        });
    }

    public void Dispose()
    {
        foreach (var c in _clients.Values) c.Dispose();
        _clients.Clear();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------- conexao

    public async Task<bool> ConnectAsync(Device device, CancellationToken ct = default)
    {
        var info = await DeviceInfoAsync(device, ct);
        return info is not null;
    }

    /// <summary>Identidade real do equipamento — modelo, firmware, série, canais.</summary>
    public async Task<HikDeviceInfo?> DeviceInfoAsync(Device device, CancellationToken ct = default)
    {
        try
        {
            var http = Client(device);
            var xml = await http.GetStringAsync("/ISAPI/System/deviceInfo", ct);
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            string V(string tag) => doc.Root?.Element(ns + tag)?.Value ?? "";

            return new HikDeviceInfo(
                Name: V("deviceName"),
                Model: V("model"),
                Firmware: $"{V("firmwareVersion")} {V("firmwareReleasedDate")}".Trim(),
                SerialNumber: V("serialNumber"),
                MacAddress: V("macAddress"),
                DeviceType: V("deviceType"),
                Channels: int.TryParse(V("videoInputPortNums"), out var c) ? c : 1);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogDebug("Hikvision {Host} inacessivel: {Message}", device.Host, e.Message);
            return null;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Resposta ISAPI inesperada de {Host}", device.Host);
            return null;
        }
    }

    // -------------------------------------------------------------- stream

    /// <summary>
    /// Canal ISAPI: <c>{canal}0{stream}</c> — 101 = canal 1, stream principal;
    /// 102 = canal 1, substream (usado em grids grandes).
    /// </summary>
    public Task<string> GetStreamUrlAsync(Device device, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(device.StreamUrl))
            return Task.FromResult(device.StreamUrl);

        var cred = string.IsNullOrEmpty(device.Username)
            ? ""
            : $"{Uri.EscapeDataString(device.Username)}:{Uri.EscapeDataString(device.Password)}@";

        return Task.FromResult($"rtsp://{cred}{device.Host}:554/Streaming/Channels/101");
    }

    // ------------------------------------------------------------ comandos

    public async Task<DriverResult> CommandAsync(
        Device device, string action, IDictionary<string, string>? parameters = null,
        CancellationToken ct = default)
    {
        parameters ??= new Dictionary<string, string>();
        var channel = parameters.TryGetValue("channel", out var c) ? c : "1";

        try
        {
            var http = Client(device);

            switch (action)
            {
                case "device_info":
                {
                    var info = await DeviceInfoAsync(device, ct);
                    return info is null
                        ? DriverResult.Fail("camera inacessivel via ISAPI")
                        : DriverResult.Success(new Dictionary<string, string>
                        {
                            ["nome"] = info.Name,
                            ["modelo"] = info.Model,
                            ["firmware"] = info.Firmware,
                            ["numeroSerie"] = info.SerialNumber,
                            ["mac"] = info.MacAddress,
                            ["tipo"] = info.DeviceType,
                            ["canais"] = info.Channels.ToString()
                        });
                }

                case "snapshot":
                    return await SnapshotAsync(http, channel, ct);

                case "ptz_preset":
                {
                    var preset = parameters.TryGetValue("preset", out var p) ? p : "1";
                    var res = await http.PutAsync(
                        $"/ISAPI/PTZCtrl/channels/{channel}/presets/{preset}/goto", null, ct);
                    return res.IsSuccessStatusCode
                        ? DriverResult.Success(new Dictionary<string, string> { ["preset"] = preset })
                        : DriverResult.Fail($"ISAPI respondeu HTTP {(int)res.StatusCode}");
                }

                case "ptz_move":
                {
                    var body = $"""
                        <PTZData><pan>{Num(parameters, "pan")}</pan>
                        <tilt>{Num(parameters, "tilt")}</tilt>
                        <zoom>{Num(parameters, "zoom")}</zoom></PTZData>
                        """;
                    return await PutXmlAsync(http, $"/ISAPI/PTZCtrl/channels/{channel}/continuous", body, ct);
                }

                case "ptz_stop":
                    return await PutXmlAsync(http, $"/ISAPI/PTZCtrl/channels/{channel}/continuous",
                        "<PTZData><pan>0</pan><tilt>0</tilt><zoom>0</zoom></PTZData>", ct);

                case "ptz_preset_list":
                {
                    var res = await http.GetAsync($"/ISAPI/PTZCtrl/channels/{channel}/presets", ct);
                    if (!res.IsSuccessStatusCode)
                        return DriverResult.Fail($"ISAPI respondeu HTTP {(int)res.StatusCode}");

                    var xml = XDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                    var presets = xml.Descendants()
                        .Where(e => e.Name.LocalName == "PTZPreset")
                        .Select(e => new
                        {
                            Id = e.Elements().FirstOrDefault(x => x.Name.LocalName == "id")?.Value,
                            Nome = e.Elements().FirstOrDefault(x => x.Name.LocalName == "presetName")?.Value
                        })
                        .Where(p => !string.IsNullOrEmpty(p.Id))
                        .ToDictionary(p => p.Id!, p => p.Nome ?? $"Preset {p.Id}");

                    return DriverResult.Success(presets);
                }

                // "ptz_save_preset" e o nome antigo; mantido para nao quebrar
                // integracao existente.
                case "ptz_preset_set":
                case "ptz_save_preset":
                {
                    var preset = parameters.TryGetValue("preset", out var p) ? p : "1";
                    var nome = parameters.TryGetValue("name", out var n) ? n : $"Preset {preset}";
                    var body = $"<PTZPreset><id>{preset}</id><presetName>{Escape(nome)}</presetName></PTZPreset>";
                    return await PutXmlAsync(http,
                        $"/ISAPI/PTZCtrl/channels/{channel}/presets/{preset}", body, ct);
                }

                case "reboot":
                {
                    var res = await http.PutAsync("/ISAPI/System/reboot", null, ct);
                    return res.IsSuccessStatusCode
                        ? DriverResult.Success()
                        : DriverResult.Fail($"ISAPI respondeu HTTP {(int)res.StatusCode}");
                }

                // Analitico embarcado: habilita regra + ROI opcional (0-1).
                case "vca_configure":
                    return await ConfigureVcaAsync(http, channel, parameters, ct);

                // Saídas de alarme / relé — usadas pela ação OutputRelay da automação.
                case "relay_on":
                case "output_on":
                case "relay_off":
                case "output_off":
                {
                    var on = action is "relay_on" or "output_on";
                    var io = parameters.TryGetValue("channel", out var ioCh) ? ioCh : "1";
                    var body = $"""
                        <IOPortData><outputState>{(on ? "high" : "low")}</outputState></IOPortData>
                        """;
                    return await PutXmlAsync(http, $"/ISAPI/System/IO/outputs/{io}/trigger", body, ct);
                }

                // Talk-back: abre/fecha canal bidirecional ISAPI.
                case "talk_open":
                {
                    var res = await http.PutAsync(
                        "/ISAPI/System/TwoWayAudio/channels/1/open", null, ct);
                    return res.IsSuccessStatusCode
                        ? DriverResult.Success(new Dictionary<string, string>
                        {
                            ["channel"] = "1",
                            ["dataPath"] = "/ISAPI/System/TwoWayAudio/channels/1/audioData"
                        })
                        : DriverResult.Fail($"talk_open HTTP {(int)res.StatusCode}");
                }
                case "talk_close":
                {
                    var res = await http.PutAsync(
                        "/ISAPI/System/TwoWayAudio/channels/1/close", null, ct);
                    return res.IsSuccessStatusCode
                        ? DriverResult.Success()
                        : DriverResult.Fail($"talk_close HTTP {(int)res.StatusCode}");
                }
                case "talk_send":
                {
                    // parameters["base64"] = payload de áudio (G.711/μ-law típico)
                    if (parameters is null || !parameters.TryGetValue("base64", out var b64) || string.IsNullOrEmpty(b64))
                        return DriverResult.Fail("talk_send exige base64 do áudio");
                    var bytes = Convert.FromBase64String(b64);
                    using var content = new ByteArrayContent(bytes);
                    content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    var res = await http.PutAsync(
                        "/ISAPI/System/TwoWayAudio/channels/1/audioData", content, ct);
                    return res.IsSuccessStatusCode
                        ? DriverResult.Success()
                        : DriverResult.Fail($"talk_send HTTP {(int)res.StatusCode}");
                }

                default:
                    return DriverResult.Fail($"acao '{action}' nao suportada pelo driver hikvision");
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return DriverResult.Fail($"camera inacessivel: {e.Message}");
        }
    }

    private static async Task<DriverResult> ConfigureVcaAsync(
        HttpClient http, string channel, IDictionary<string, string> p, CancellationToken ct)
    {
        var rule = (p.TryGetValue("rule", out var r) ? r : "motion").ToLowerInvariant();
        var enabled = !p.TryGetValue("enabled", out var en) ||
                      !string.Equals(en, "false", StringComparison.OrdinalIgnoreCase);
        var enXml = enabled ? "true" : "false";

        // ROI normalizado 0-1 -> percent 0-1000 (grade ISAPI comum).
        static int Pct(IDictionary<string, string> d, string k, double def)
        {
            if (d.TryGetValue(k, out var s) &&
                double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                return (int)Math.Clamp(Math.Round(v * 1000), 0, 1000);
            return (int)Math.Clamp(Math.Round(def * 1000), 0, 1000);
        }

        var x = Pct(p, "x", 0);
        var y = Pct(p, "y", 0);
        var w = Pct(p, "w", 1);
        var h = Pct(p, "h", 1);
        if (w <= 0) w = 1000;
        if (h <= 0) h = 1000;

        string path;
        string body;
        switch (rule)
        {
            case "motion":
            case "vmd":
                path = $"/ISAPI/System/Video/inputs/channels/{channel}/motionDetection";
                body = $"""
                    <MotionDetection version="2.0">
                      <enabled>{enXml}</enabled>
                      <enableHighlight>true</enableHighlight>
                      <samplingInterval>2</samplingInterval>
                      <startTriggerTime>500</startTriggerTime>
                      <endTriggerTime>500</endTriggerTime>
                      <regionType>grid</regionType>
                      <Grid>
                        <rowGranularity>18</rowGranularity>
                        <columnGranularity>22</columnGranularity>
                      </Grid>
                    </MotionDetection>
                    """;
                break;
            case "intrusion":
            case "field":
            case "region":
                path = $"/ISAPI/Smart/FieldDetection/{channel}";
                body = $"""
                    <FieldDetection>
                      <enabled>{enXml}</enabled>
                      <normalizedScreenSize><normalizedScreenWidth>1000</normalizedScreenWidth>
                        <normalizedScreenHeight>1000</normalizedScreenHeight></normalizedScreenSize>
                      <FieldDetectionRegionList>
                        <FieldDetectionRegion>
                          <id>1</id>
                          <enabled>{enXml}</enabled>
                          <sensitivityLevel>50</sensitivityLevel>
                          <RegionCoordinatesList>
                            <RegionCoordinates><positionX>{x}</positionX><positionY>{y}</positionY></RegionCoordinates>
                            <RegionCoordinates><positionX>{x + w}</positionX><positionY>{y}</positionY></RegionCoordinates>
                            <RegionCoordinates><positionX>{x + w}</positionX><positionY>{y + h}</positionY></RegionCoordinates>
                            <RegionCoordinates><positionX>{x}</positionX><positionY>{y + h}</positionY></RegionCoordinates>
                          </RegionCoordinatesList>
                        </FieldDetectionRegion>
                      </FieldDetectionRegionList>
                    </FieldDetection>
                    """;
                break;
            case "line_crossing":
            case "line":
                path = $"/ISAPI/Smart/LineDetection/{channel}";
                body = $"""
                    <LineDetection>
                      <enabled>{enXml}</enabled>
                      <LineItemList>
                        <LineItem>
                          <id>1</id>
                          <enabled>{enXml}</enabled>
                          <sensitivityLevel>50</sensitivityLevel>
                          <directionSensitivity>any</directionSensitivity>
                          <CoordinatesList>
                            <Coordinates><positionX>{x}</positionX><positionY>{y + h / 2}</positionY></Coordinates>
                            <Coordinates><positionX>{x + w}</positionX><positionY>{y + h / 2}</positionY></Coordinates>
                          </CoordinatesList>
                        </LineItem>
                      </LineItemList>
                    </LineDetection>
                    """;
                break;
            default:
                return DriverResult.Fail(
                    $"Regra VCA '{rule}' nao suportada no Hikvision (use motion, intrusion, line_crossing).");
        }

        var result = await PutXmlAsync(http, path, body, ct);
        if (!result.Ok) return result;
        return DriverResult.Success(new Dictionary<string, string>
        {
            ["rule"] = rule,
            ["enabled"] = enXml,
            ["path"] = path
        });
    }

    private static async Task<DriverResult> SnapshotAsync(HttpClient http, string channel, CancellationToken ct)
    {
        var res = await http.GetAsync($"/ISAPI/Streaming/channels/{channel}01/picture", ct);
        if (!res.IsSuccessStatusCode)
            return DriverResult.Fail($"ISAPI respondeu HTTP {(int)res.StatusCode}");

        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        return DriverResult.Success(new Dictionary<string, string>
        {
            ["contentType"] = "image/jpeg",
            ["base64"] = Convert.ToBase64String(bytes)
        });
    }

    private static async Task<DriverResult> PutXmlAsync(
        HttpClient http, string path, string xml, CancellationToken ct)
    {
        var content = new StringContent(xml, Encoding.UTF8, "application/xml");
        var res = await http.PutAsync(path, content, ct);
        return res.IsSuccessStatusCode
            ? DriverResult.Success()
            : DriverResult.Fail($"ISAPI respondeu HTTP {(int)res.StatusCode}");
    }

    /// <summary>
    /// Converte a velocidade normalizada da plataforma (-1..1) para a escala
    /// ISAPI (-100..100). Aceita tambem o valor ja em -100..100 vindo de
    /// integracao antiga: acima de 1 em modulo, assume que ja esta na escala
    /// do fabricante.
    /// </summary>
    private static string Num(IDictionary<string, string> p, string key)
    {
        if (!p.TryGetValue(key, out var v) ||
            !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return "0";

        var escala = Math.Abs(n) <= 1 ? n * 100 : n;
        return ((int)Math.Round(Math.Clamp(escala, -100, 100))).ToString(CultureInfo.InvariantCulture);
    }

    private static string Escape(string s) => new XText(s).ToString();

    // -------------------------------------------------------------- eventos

    /// <summary>Eventos nativos ISAPI mapeados para o vocabulário da plataforma.</summary>
    private static readonly Dictionary<string, (string Type, int Severity)> EventMap = new()
    {
        ["VMD"]             = ("motion", 2),
        ["linedetection"]   = ("line_crossing", 2),
        ["fielddetection"]  = ("intrusion", 3),
        ["regionEntrance"]  = ("region_entrance", 2),
        ["regionExiting"]   = ("region_exit", 2),
        ["tamperdetection"] = ("tamper", 3),
        ["videoloss"]       = ("video_loss", 3),
        ["facedetection"]   = ("face_detected", 2),
        ["ANPR"]            = ("lpr_detected", 2),
        ["shelteralarm"]    = ("tamper", 3),
        ["IO"]              = ("io_trigger", 2),
    };

    /// <summary>
    /// Assina o <c>alertStream</c> — conexão HTTP longa em que a câmera
    /// empurra os eventos. É o ganho central sobre ONVIF: chega o evento de
    /// analítico da própria câmera, sem polling.
    /// </summary>
    public async IAsyncEnumerable<DeviceEvent> StreamEventsAsync(
        Device device, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // C# nao permite `yield` dentro de try/catch, entao a conexao e a
        // leitura ficam em metodos separados que devolvem o resultado.
        while (!ct.IsCancellationRequested)
        {
            var reader = await OpenAlertStreamAsync(device, ct);

            if (reader is not null)
            {
                await foreach (var evt in ReadAlertStreamAsync(reader, device, ct))
                    yield return evt;
            }

            await Task.Delay(TimeSpan.FromSeconds(15), ct);   // reconexao
        }
    }

    private async Task<StreamReader?> OpenAlertStreamAsync(Device device, CancellationToken ct)
    {
        try
        {
            var http = Client(device, longLived: true);
            var res = await http.GetAsync(
                "/ISAPI/Event/notification/alertStream",
                HttpCompletionOption.ResponseHeadersRead, ct);

            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("alertStream de {Host} respondeu HTTP {Status}",
                    device.Host, (int)res.StatusCode);
                res.Dispose();
                return null;
            }

            log.LogInformation("alertStream conectado em {Host} (camera {Id})", device.Host, device.Id);
            return new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e)
        {
            log.LogWarning("alertStream de {Host} indisponivel: {Message}", device.Host, e.Message);
            return null;
        }
    }

    private async IAsyncEnumerable<DeviceEvent> ReadAlertStreamAsync(
        StreamReader reader, Device device, [EnumeratorCancellation] CancellationToken ct)
    {
        using var _ = reader;
        var buffer = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var (line, ended) = await ReadLineSafeAsync(reader, device, ct);
            if (ended) yield break;

            // O corpo vem em multipart; cada evento fecha em </EventNotificationAlert>.
            buffer.AppendLine(line);
            if (!line!.Contains("</EventNotificationAlert>")) continue;

            var payload = buffer.ToString();
            buffer.Clear();

            if (ParseAlert(payload, device) is { } evt) yield return evt;
        }
    }

    private async Task<(string? Line, bool Ended)> ReadLineSafeAsync(
        StreamReader reader, Device device, CancellationToken ct)
    {
        try
        {
            var line = await reader.ReadLineAsync(ct);
            return (line, line is null);          // null = camera encerrou a conexao
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, true);
        }
        catch (Exception e)
        {
            log.LogWarning("alertStream de {Host} caiu: {Message}", device.Host, e.Message);
            return (null, true);
        }
    }

    private DeviceEvent? ParseAlert(string payload, Device device)
    {
        var start = payload.IndexOf("<EventNotificationAlert", StringComparison.Ordinal);
        if (start < 0) return null;

        try
        {
            var doc = XDocument.Parse(payload[start..]);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            string V(string tag) => doc.Root?.Element(ns + tag)?.Value ?? "";

            var raw = V("eventType");
            if (string.IsNullOrEmpty(raw)) return null;

            // videoloss dispara continuamente; só interessa quando ativo.
            if (V("eventState") == "inactive") return null;

            var (type, severity) = EventMap.TryGetValue(raw, out var m)
                ? m
                : (raw.ToLowerInvariant(), 2);

            // Metadados estruturados (LPR, face, ROI) para o módulo Analítico.
            var meta = EventMetadata.FromHikvisionXml(doc, raw, ns);
            if (meta.Kind == "lpr" && type is "lpr_detected" or "anpr")
                type = "lpr_detected";
            if (meta.Kind == "face")
                type = "face_detected";

            return new DeviceEvent
            {
                TenantId = device.TenantId,
                DeviceId = device.Id,
                Type = type,
                Severity = severity,
                Payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    vendor = "hikvision",
                    isapiEvent = raw,
                    channel = V("channelID"),
                    description = V("eventDescription"),
                    count = V("activePostCount"),
                    meta
                })
            };
        }
        catch (Exception e)
        {
            log.LogDebug("alertStream com XML invalido de {Host}: {Message}", device.Host, e.Message);
            return null;
        }
    }
}

public record HikDeviceInfo(
    string Name,
    string Model,
    string Firmware,
    string SerialNumber,
    string MacAddress,
    string DeviceType,
    int Channels);
