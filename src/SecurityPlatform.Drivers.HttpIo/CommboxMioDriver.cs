using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;

namespace SecurityPlatform.Drivers.HttpIo;

/// <summary>
/// Driver nativo para módulos <b>Commbox Multi I/O Series</b>
/// (MIO-100, MIO-400, MIO-800, MIO-0816, MIO-2408 e afins).
///
/// Integração no estilo Digifort / Safe I/O: cadastro por IP + porta + modelo,
/// com comandos de saída (relé), leitura de entradas e eventos de mudança de input.
///
/// Protocolos suportados (auto ou forçado via config em <see cref="Device.StreamUrl"/>):
/// <list type="bullet">
/// <item><c>http</c> — API web do módulo (porta 80, MIO v3+)</item>
/// <item><c>tcp</c> — protocolo nativo TCP (porta típica 1024, integração Digifort)</item>
/// <item><c>auto</c> — tenta HTTP, depois TCP</item>
/// </list>
///
/// Config opcional em StreamUrl (JSON):
/// <code>
/// {"protocol":"auto","model":"mio0816","outputs":8,"inputs":16,"tcpPort":1024}
/// </code>
/// Se StreamUrl for um path (ex.: <c>/api/output</c>), usa HTTP com esse path-base.
/// </summary>
public sealed class CommboxMioDriver : IDeviceDriver
{
    public string Name => "commbox";
    public DeviceKind[] Supports => [DeviceKind.AccessPoint, DeviceKind.AlarmPanel];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    // Contagem de I/O por modelo da linha Multi I/O Series.
    private static readonly Dictionary<string, (int Outputs, int Inputs)> Models = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mio100"] = (1, 0),
        ["mio-100"] = (1, 0),
        ["mio400"] = (4, 0),
        ["mio-400"] = (4, 0),
        ["mio800"] = (8, 0),
        ["mio-800"] = (8, 0),
        ["mio0816"] = (8, 16),
        ["mio-0816"] = (8, 16),
        ["mio816"] = (8, 16),
        ["mio2408"] = (24, 8),
        ["mio-2408"] = (24, 8),
        ["default"] = (8, 16),
    };

    public async Task<bool> ConnectAsync(Device device, CancellationToken ct = default)
    {
        var cfg = ParseConfig(device);
        if (cfg.Protocol is "http" or "auto")
        {
            if (await TryHttpPingAsync(device, cfg, ct)) return true;
            if (cfg.Protocol == "http") return false;
        }

        return await TryTcpConnectAsync(device, cfg, ct);
    }

    public Task<string> GetStreamUrlAsync(Device device, CancellationToken ct = default)
        => Task.FromResult(""); // I/O — sem vídeo

    public async Task<DriverResult> CommandAsync(
        Device device, string action,
        IDictionary<string, string>? parameters = null, CancellationToken ct = default)
    {
        parameters ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cfg = ParseConfig(device);
        var channel = ParseChannel(parameters);
        var act = action.ToLowerInvariant();

        return act switch
        {
            "relay_on" or "output_on" or "output" or "open_door" or "door_open" =>
                await SetOutputAsync(device, cfg, channel, on: true, pulseMs: null, ct),

            "relay_off" or "output_off" or "door_close" =>
                await SetOutputAsync(device, cfg, channel, on: false, pulseMs: null, ct),

            "relay_pulse" or "output_pulse" or "door_pulse" =>
                await SetOutputAsync(device, cfg, channel, on: true,
                    pulseMs: parameters.TryGetValue("ms", out var ms) && int.TryParse(ms, out var n) ? n : 1000, ct),

            "get_inputs" or "inputs" or "read_inputs" =>
                await ReadInputsAsync(device, cfg, ct),

            "get_outputs" or "outputs" or "read_outputs" =>
                await ReadOutputsAsync(device, cfg, ct),

            "device_info" or "info" =>
                DriverResult.Success(new Dictionary<string, string>
                {
                    ["driver"] = Name,
                    ["vendor"] = "Commbox",
                    ["model"] = cfg.Model,
                    ["protocol"] = cfg.Protocol,
                    ["outputs"] = cfg.Outputs.ToString(),
                    ["inputs"] = cfg.Inputs.ToString(),
                    ["host"] = device.Host,
                    ["port"] = (device.Port <= 0 ? DefaultPort(cfg) : device.Port).ToString()
                }),

            _ => DriverResult.Fail($"Ação '{action}' não suportada pelo driver Commbox Multi I/O")
        };
    }

    public async IAsyncEnumerable<DeviceEvent> StreamEventsAsync(
        Device device, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var cfg = ParseConfig(device);
        bool? wasOnline = null;
        string? lastInputs = null;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);

            var online = await ConnectAsync(device, ct);
            if (wasOnline is null) wasOnline = online;
            else if (online != wasOnline)
            {
                wasOnline = online;
                yield return Ev(device, online ? "device_online" : "device_offline", online ? 1 : 2,
                    $"{{\"host\":\"{device.Host}\",\"driver\":\"commbox\"}}");
            }

            if (!online) continue;

            List<DeviceEvent>? inputEvents = null;
            try
            {
                var r = await ReadInputsAsync(device, cfg, ct);
                if (r.Ok && r.Data is not null && r.Data.TryGetValue("inputs", out var mask))
                {
                    if (lastInputs is null)
                    {
                        lastInputs = mask;
                    }
                    else if (!string.Equals(lastInputs, mask, StringComparison.Ordinal)
                             && ulong.TryParse(lastInputs, out var prev)
                             && ulong.TryParse(mask, out var curr))
                    {
                        inputEvents = [];
                        var changed = prev ^ curr;
                        for (var i = 0; i < cfg.Inputs && i < 64; i++)
                        {
                            var bit = 1UL << i;
                            if ((changed & bit) == 0) continue;
                            var active = (curr & bit) != 0;
                            inputEvents.Add(Ev(device, active ? "io_input_on" : "io_input_off", 2,
                                $"{{\"channel\":{i + 1},\"state\":\"{(active ? "on" : "off")}\",\"driver\":\"commbox\",\"model\":\"{cfg.Model}\"}}"));
                        }
                        lastInputs = mask;
                    }
                }
            }
            catch
            {
                // polling silencioso
            }

            if (inputEvents is not null)
            {
                foreach (var ev in inputEvents)
                    yield return ev;
            }
        }
    }

    // ------------------------------------------------------------------ config

    private sealed class MioConfig
    {
        public string Protocol { get; set; } = "auto"; // auto|http|tcp
        public string Model { get; set; } = "mio0816";
        public int Outputs { get; set; } = 8;
        public int Inputs { get; set; } = 16;
        public int TcpPort { get; set; } = 1024;
        public string? HttpPath { get; set; }
    }

    private static MioConfig ParseConfig(Device d)
    {
        var cfg = new MioConfig();
        var raw = (d.StreamUrl ?? "").Trim();
        if (string.IsNullOrEmpty(raw))
        {
            ApplyModel(cfg, "mio0816");
            return cfg;
        }

        if (raw.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var r = doc.RootElement;
                if (r.TryGetProperty("protocol", out var p)) cfg.Protocol = p.GetString() ?? "auto";
                if (r.TryGetProperty("model", out var m)) cfg.Model = m.GetString() ?? "mio0816";
                if (r.TryGetProperty("tcpPort", out var tp) && tp.TryGetInt32(out var tpn)) cfg.TcpPort = tpn;
                if (r.TryGetProperty("outputs", out var o) && o.TryGetInt32(out var on)) cfg.Outputs = on;
                if (r.TryGetProperty("inputs", out var i) && i.TryGetInt32(out var inn)) cfg.Inputs = inn;
            }
            catch { /* usa default */ }
            ApplyModel(cfg, cfg.Model);
            return cfg;
        }

        // Path HTTP simples
        cfg.HttpPath = raw.StartsWith('/') ? raw : "/" + raw;
        cfg.Protocol = "http";
        ApplyModel(cfg, "mio0816");
        return cfg;
    }

    private static void ApplyModel(MioConfig cfg, string model)
    {
        cfg.Model = model;
        if (Models.TryGetValue(model, out var counts))
        {
            if (cfg.Outputs <= 0) cfg.Outputs = counts.Outputs;
            if (cfg.Inputs <= 0) cfg.Inputs = counts.Inputs;
            // se defaults ainda zerados
            if (cfg.Outputs == 0) cfg.Outputs = counts.Outputs;
            if (cfg.Inputs == 0 && counts.Inputs > 0) cfg.Inputs = counts.Inputs;
        }
        else if (Models.TryGetValue("default", out var def))
        {
            if (cfg.Outputs <= 0) cfg.Outputs = def.Outputs;
            if (cfg.Inputs <= 0) cfg.Inputs = def.Inputs;
        }
        if (cfg.Outputs <= 0) cfg.Outputs = 8;
    }

    private static int DefaultPort(MioConfig cfg)
        => cfg.Protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase) ? cfg.TcpPort : 80;

    private static int ParseChannel(IDictionary<string, string> p)
    {
        if (p.TryGetValue("channel", out var ch) && int.TryParse(ch, out var n))
            return Math.Max(1, n);
        if (p.TryGetValue("output", out var o) && int.TryParse(o, out var n2))
            return Math.Max(1, n2);
        return 1;
    }

    // ------------------------------------------------------------------ HTTP

    private static string HttpBase(Device d)
        => $"http://{d.Host}:{(d.Port <= 0 ? 80 : d.Port)}";

    private static void ApplyAuth(HttpRequestMessage req, Device d)
    {
        if (string.IsNullOrEmpty(d.Username)) return;
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{d.Username}:{d.Password}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    private static async Task<bool> TryHttpPingAsync(Device device, MioConfig cfg, CancellationToken ct)
    {
        // Probes comuns em MIO web / gateways Commbox
        string[] paths = ["/api/status", "/api/info", "/status", "/cgi-bin/status", "/", cfg.HttpPath ?? ""];
        foreach (var path in paths.Where(x => !string.IsNullOrEmpty(x)).Distinct())
        {
            try
            {
                var url = new Uri(new Uri(HttpBase(device).TrimEnd('/') + "/"), path.TrimStart('/'));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyAuth(req, device);
                using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)res.StatusCode < 500) return true;
            }
            catch { /* próximo */ }
        }
        return false;
    }

    private async Task<DriverResult> SetOutputAsync(
        Device device, MioConfig cfg, int channel, bool on, int? pulseMs, CancellationToken ct)
    {
        if (channel < 1 || channel > Math.Max(cfg.Outputs, 64))
            return DriverResult.Fail($"Canal {channel} inválido (saídas do modelo: {cfg.Outputs}).");

        if (cfg.Protocol is "http" or "auto")
        {
            var http = await TryHttpSetOutputAsync(device, cfg, channel, on, pulseMs, ct);
            if (http.Ok || cfg.Protocol == "http") return http;
        }

        return await TryTcpSetOutputAsync(device, cfg, channel, on, pulseMs, ct);
    }

    private static async Task<DriverResult> TryHttpSetOutputAsync(
        Device device, MioConfig cfg, int channel, bool on, int? pulseMs, CancellationToken ct)
    {
        // Paths usados por integrações HTTP de Multi I/O / gateways.
        var basePath = string.IsNullOrWhiteSpace(cfg.HttpPath) ? "/api/output" : cfg.HttpPath.TrimEnd('/');
        var candidates = new List<(HttpMethod Method, string Path, string? Body)>
        {
            (HttpMethod.Post, $"{basePath}/{channel}", JsonBody(on, channel, pulseMs)),
            (HttpMethod.Post, $"/api/outputs/{channel}", JsonBody(on, channel, pulseMs)),
            (HttpMethod.Post, $"/relay/{channel - 1}", JsonBody(on, channel, pulseMs)),
            (HttpMethod.Get,  $"/cgi-bin/output?ch={channel}&state={(on ? 1 : 0)}" +
                              (pulseMs is int ms ? $"&pulse={ms}" : ""), null),
            (HttpMethod.Get,  $"/setout.cgi?out={channel}&val={(on ? 1 : 0)}", null),
        };

        Exception? last = null;
        foreach (var (method, path, body) in candidates)
        {
            try
            {
                var url = new Uri(new Uri(HttpBase(device).TrimEnd('/') + "/"), path.TrimStart('/'));
                using var req = new HttpRequestMessage(method, url);
                ApplyAuth(req, device);
                if (body is not null)
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using var res = await Http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                {
                    return DriverResult.Success(new Dictionary<string, string>
                    {
                        ["action"] = on ? (pulseMs is null ? "relay_on" : "relay_pulse") : "relay_off",
                        ["channel"] = channel.ToString(),
                        ["protocol"] = "http",
                        ["http"] = ((int)res.StatusCode).ToString()
                    });
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException)
            {
                last = e;
            }
        }

        return DriverResult.Fail(last is null
            ? "Commbox HTTP: módulo não aceitou comando de saída"
            : $"Commbox HTTP inacessível: {last.Message}");
    }

    private static string JsonBody(bool on, int channel, int? pulseMs)
    {
        var doc = new Dictionary<string, object?>
        {
            ["on"] = on,
            ["state"] = on ? 1 : 0,
            ["channel"] = channel,
            ["output"] = channel
        };
        if (pulseMs is int ms) doc["pulse_ms"] = ms;
        return JsonSerializer.Serialize(doc);
    }

    private static async Task<DriverResult> ReadInputsAsync(Device device, MioConfig cfg, CancellationToken ct)
    {
        if (cfg.Protocol is "http" or "auto")
        {
            var http = await TryHttpReadMaskAsync(device, new[]
            {
                "/api/inputs", "/api/input", "/cgi-bin/inputs", "/inputs", "/status"
            }, "inputs", ct);
            if (http.Ok || cfg.Protocol == "http") return http;
        }

        return await TryTcpReadInputsAsync(device, cfg, ct);
    }

    private static async Task<DriverResult> ReadOutputsAsync(Device device, MioConfig cfg, CancellationToken ct)
    {
        if (cfg.Protocol is "http" or "auto")
        {
            var http = await TryHttpReadMaskAsync(device, new[]
            {
                "/api/outputs", "/api/output", "/cgi-bin/outputs", "/outputs", "/status"
            }, "outputs", ct);
            if (http.Ok || cfg.Protocol == "http") return http;
        }

        return DriverResult.Success(new Dictionary<string, string>
        {
            ["outputs"] = "0",
            ["protocol"] = "tcp",
            ["note"] = "leitura de saídas via TCP não exposta; use get_inputs"
        });
    }

    private static async Task<DriverResult> TryHttpReadMaskAsync(
        Device device, string[] paths, string key, CancellationToken ct)
    {
        foreach (var path in paths)
        {
            try
            {
                var url = new Uri(new Uri(HttpBase(device).TrimEnd('/') + "/"), path.TrimStart('/'));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyAuth(req, device);
                using var res = await Http.SendAsync(req, ct);
                if (!res.IsSuccessStatusCode) continue;
                var text = await res.Content.ReadAsStringAsync(ct);
                var mask = ParseMaskFromPayload(text);
                return DriverResult.Success(new Dictionary<string, string>
                {
                    [key] = mask.ToString(),
                    ["protocol"] = "http",
                    ["raw"] = text.Length > 200 ? text[..200] : text
                });
            }
            catch { /* próximo */ }
        }
        return DriverResult.Fail($"Commbox HTTP: não foi possível ler {key}");
    }

    private static ulong ParseMaskFromPayload(string text)
    {
        text = text.Trim();
        if (ulong.TryParse(text, out var n)) return n;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "inputs", "outputs", "mask", "value", "state", "bits" })
                {
                    if (!r.TryGetProperty(name, out var p)) continue;
                    if (p.ValueKind == JsonValueKind.Number && p.TryGetUInt64(out var u)) return u;
                    if (p.ValueKind == JsonValueKind.String && ulong.TryParse(p.GetString(), out var us)) return us;
                    if (p.ValueKind == JsonValueKind.Array)
                    {
                        ulong m = 0;
                        var i = 0;
                        foreach (var el in p.EnumerateArray())
                        {
                            var on = el.ValueKind switch
                            {
                                JsonValueKind.True => true,
                                JsonValueKind.Number => el.GetInt32() != 0,
                                JsonValueKind.String => el.GetString() is "1" or "on" or "true",
                                _ => false
                            };
                            if (on) m |= 1UL << i;
                            i++;
                        }
                        return m;
                    }
                }
            }
            if (r.ValueKind == JsonValueKind.Array)
            {
                ulong m = 0;
                var i = 0;
                foreach (var el in r.EnumerateArray())
                {
                    var on = el.ValueKind == JsonValueKind.True
                             || (el.ValueKind == JsonValueKind.Number && el.GetInt32() != 0);
                    if (on) m |= 1UL << i;
                    i++;
                }
                return m;
            }
        }
        catch { /* not json */ }

        // bits ASCII "1010..."
        if (text.All(c => c is '0' or '1') && text.Length is > 0 and <= 64)
        {
            ulong m = 0;
            for (var i = 0; i < text.Length; i++)
                if (text[i] == '1') m |= 1UL << i;
            return m;
        }

        return 0;
    }

    // ------------------------------------------------------------------ TCP nativo (Digifort-style)

    private static async Task<bool> TryTcpConnectAsync(Device device, MioConfig cfg, CancellationToken ct)
    {
        var port = device.Port > 0 && !cfg.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase)
            ? device.Port
            : cfg.TcpPort;
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(device.Host, port, cts.Token);
            return tcp.Connected;
        }
        catch { return false; }
    }

    /// <summary>
    /// Comandos TCP nativos usados por integrações Digifort / Multi I/O:
    /// linhas ASCII terminadas em CR/LF.
    /// <list type="bullet">
    /// <item><c>OUT n s</c> — saída n (1-based) estado s (0/1)</item>
    /// <item><c>PULSE n ms</c> — pulso na saída n</item>
    /// <item><c>IN?</c> — lê máscara de entradas</item>
    /// </list>
    /// </summary>
    private static async Task<DriverResult> TryTcpSetOutputAsync(
        Device device, MioConfig cfg, int channel, bool on, int? pulseMs, CancellationToken ct)
    {
        var port = ResolveTcpPort(device, cfg);
        try
        {
            string cmd;
            if (pulseMs is int ms && on)
                cmd = $"PULSE {channel} {ms}\r\n";
            else
                cmd = $"OUT {channel} {(on ? 1 : 0)}\r\n";

            var resp = await TcpExchangeAsync(device.Host, port, cmd, ct);
            // Alternativas se o módulo não reconhecer o primeiro formato
            if (LooksLikeError(resp))
            {
                cmd = on
                    ? (pulseMs is int pms ? $"SET OUT{channel} PULSE {pms}\r\n" : $"SET OUT{channel} ON\r\n")
                    : $"SET OUT{channel} OFF\r\n";
                resp = await TcpExchangeAsync(device.Host, port, cmd, ct);
            }

            return DriverResult.Success(new Dictionary<string, string>
            {
                ["action"] = on ? (pulseMs is null ? "relay_on" : "relay_pulse") : "relay_off",
                ["channel"] = channel.ToString(),
                ["protocol"] = "tcp",
                ["port"] = port.ToString(),
                ["response"] = (resp ?? "").Trim()
            });
        }
        catch (Exception e) when (e is SocketException or IOException or TaskCanceledException or ObjectDisposedException)
        {
            return DriverResult.Fail($"Commbox TCP inacessível em {device.Host}:{port}: {e.Message}");
        }
    }

    private static async Task<DriverResult> TryTcpReadInputsAsync(Device device, MioConfig cfg, CancellationToken ct)
    {
        var port = ResolveTcpPort(device, cfg);
        try
        {
            var resp = await TcpExchangeAsync(device.Host, port, "IN?\r\n", ct);
            if (string.IsNullOrWhiteSpace(resp) || LooksLikeError(resp))
                resp = await TcpExchangeAsync(device.Host, port, "GET IN\r\n", ct);

            var mask = ParseMaskFromPayload((resp ?? "0").Trim());
            return DriverResult.Success(new Dictionary<string, string>
            {
                ["inputs"] = mask.ToString(),
                ["protocol"] = "tcp",
                ["raw"] = (resp ?? "").Trim()
            });
        }
        catch (Exception e) when (e is SocketException or IOException or TaskCanceledException)
        {
            return DriverResult.Fail($"Commbox TCP (inputs) falhou: {e.Message}");
        }
    }

    private static int ResolveTcpPort(Device device, MioConfig cfg)
    {
        if (device.Port > 0 && device.Port != 80 && device.Port != 443)
            return device.Port;
        return cfg.TcpPort > 0 ? cfg.TcpPort : 1024;
    }

    private static bool LooksLikeError(string? resp)
    {
        if (string.IsNullOrWhiteSpace(resp)) return true;
        var t = resp.Trim();
        return t.StartsWith("ERR", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("?", StringComparison.Ordinal);
    }

    private static async Task<string> TcpExchangeAsync(string host, int port, string command, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        await tcp.ConnectAsync(host, port, cts.Token);
        await using var stream = tcp.GetStream();

        var bytes = Encoding.ASCII.GetBytes(command);
        await stream.WriteAsync(bytes, cts.Token);
        await stream.FlushAsync(cts.Token);

        // Leitura curta (módulos respondem rápido ou fecham)
        var buf = new byte[512];
        stream.ReadTimeout = 1500;
        try
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            readCts.CancelAfter(1500);
            var n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), readCts.Token);
            return n > 0 ? Encoding.ASCII.GetString(buf, 0, n) : "OK";
        }
        catch (OperationCanceledException)
        {
            // Sem resposta — muitos módulos só executam sem ACK
            return "OK";
        }
        catch (IOException)
        {
            return "OK";
        }
    }

    private static DeviceEvent Ev(Device d, string type, int severity, string payload) => new()
    {
        TenantId = d.TenantId,
        DeviceId = d.Id,
        Type = type,
        Severity = severity,
        Payload = payload
    };
}
