using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;

namespace SecurityPlatform.Drivers.Onvif;

/// <summary>
/// Sessão PTZ ONVIF: resolve endpoints, profile token e executa move/stop/presets.
/// </summary>
internal sealed class OnvifPtzClient
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.ExpectContinue = false;
        return h;
    }

    private sealed class Session
    {
        public required string DeviceUrl { get; init; }
        public required string MediaUrl { get; init; }
        public required string PtzUrl { get; init; }
        public required string ProfileToken { get; init; }
        public DateTime LoadedAt { get; init; } = DateTime.UtcNow;
    }

    private static string Key(Device d) =>
        $"{d.Id}|{d.Host}:{d.Port}|{d.Username}";

    public async Task<DriverResult> CommandAsync(
        Device device, string action, IDictionary<string, string>? parameters, CancellationToken ct)
    {
        try
        {
            var session = await GetSessionAsync(device, ct);
            if (session is null)
                return DriverResult.Fail("Câmera ONVIF sem serviço PTZ (ou inacessível).");

            return action switch
            {
                "ptz_move" => await ContinuousMoveAsync(device, session, parameters, ct),
                "ptz_stop" => await StopAsync(device, session, ct),
                "ptz_preset" => await GotoPresetAsync(device, session, parameters, ct),
                "ptz_preset_set" or "ptz_save_preset" => await SetPresetAsync(device, session, parameters, ct),
                "ptz_preset_list" => await GetPresetsAsync(device, session, ct),
                _ => DriverResult.Fail($"ação PTZ '{action}' não suportada no ONVIF")
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _sessions.TryRemove(Key(device), out _);
            return DriverResult.Fail($"ONVIF PTZ: {e.Message}");
        }
    }

    private async Task<Session?> GetSessionAsync(Device device, CancellationToken ct)
    {
        var key = Key(device);
        if (_sessions.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.LoadedAt < TimeSpan.FromMinutes(10))
            return cached;

        var deviceUrl = OnvifSoap.DeviceServiceUrl(device.Host, device.Port);
        var user = device.Username;
        var pass = device.Password;

        // GetCapabilities → PTZ / Media XAddr
        var (ok, doc, err) = await OnvifSoap.PostAsync(Http, deviceUrl,
            "http://www.onvif.org/ver10/device/wsdl/GetCapabilities",
            """<tds:GetCapabilities><tds:Category>All</tds:Category></tds:GetCapabilities>""",
            user, pass, ct);

        if (!ok || doc is null)
            return null;

        var ptzX = FindXAddr(doc, "PTZ");
        var mediaX = FindXAddr(doc, "Media");
        if (string.IsNullOrWhiteSpace(ptzX))
            return null;

        ptzX = OnvifSoap.RewriteHost(ptzX!, device.Host, device.Port);
        mediaX = string.IsNullOrWhiteSpace(mediaX)
            ? $"http://{device.Host}:{(device.Port <= 0 ? 80 : device.Port)}/onvif/media_service"
            : OnvifSoap.RewriteHost(mediaX, device.Host, device.Port);

        var profile = await ResolveProfileTokenAsync(device, mediaX, user, pass, ct);
        if (string.IsNullOrWhiteSpace(profile))
            profile = "Profile_1"; // fallback comum

        var session = new Session
        {
            DeviceUrl = deviceUrl,
            MediaUrl = mediaX,
            PtzUrl = ptzX,
            ProfileToken = profile!
        };
        _sessions[key] = session;
        return session;
    }

    private static string? FindXAddr(XDocument doc, string capabilityLocal)
    {
        // <tt:PTZ><tt:XAddr>http://...</tt:XAddr></tt:PTZ>
        var cap = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == capabilityLocal);
        return cap?.Elements().FirstOrDefault(e => e.Name.LocalName == "XAddr")?.Value?.Trim()
            ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "XAddr"
                && e.Parent?.Name.LocalName == capabilityLocal)?.Value?.Trim();
    }

    private static async Task<string?> ResolveProfileTokenAsync(
        Device device, string mediaUrl, string user, string pass, CancellationToken ct)
    {
        var (ok, doc, _) = await OnvifSoap.PostAsync(Http, mediaUrl,
            "http://www.onvif.org/ver10/media/wsdl/GetProfiles",
            "<trt:GetProfiles/>", user, pass, ct);
        if (!ok || doc is null) return null;

        var profiles = OnvifSoap.Locals(doc, "Profiles").ToList();
        if (profiles.Count == 0)
            profiles = OnvifSoap.Locals(doc, "Profile").ToList();

        // Prefere perfil com PTZConfiguration
        foreach (var p in profiles)
        {
            var hasPtz = p.Descendants().Any(e => e.Name.LocalName == "PTZConfiguration");
            var token = p.Attributes().FirstOrDefault(a => a.Name.LocalName == "token")?.Value
                ?? p.Elements().FirstOrDefault(e => e.Name.LocalName == "token")?.Value
                ?? p.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
            if (hasPtz && !string.IsNullOrWhiteSpace(token))
                return token;
        }

        var first = profiles.FirstOrDefault();
        return first?.Attributes().FirstOrDefault(a => a.Name.LocalName == "token")?.Value
            ?? first?.Elements().FirstOrDefault(e => e.Name.LocalName == "token")?.Value;
    }

    private async Task<DriverResult> ContinuousMoveAsync(
        Device device, Session session, IDictionary<string, string>? p, CancellationToken ct)
    {
        var pan = ParseNorm(p, "pan");
        var tilt = ParseNorm(p, "tilt");
        var zoom = ParseNorm(p, "zoom");
        var timeout = 2;
        if (p is not null && p.TryGetValue("timeout", out var t) && int.TryParse(t, out var sec))
            timeout = Math.Clamp(sec, 1, 30);

        // Espaço normalizado ONVIF: -1..1
        var inv = CultureInfo.InvariantCulture;
        var body = $"""
            <tptz:ContinuousMove>
              <tptz:ProfileToken>{OnvifSoap.Xml(session.ProfileToken)}</tptz:ProfileToken>
              <tptz:Velocity>
                <tt:PanTilt x="{pan.ToString("0.###", inv)}" y="{tilt.ToString("0.###", inv)}" space="http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace"/>
                <tt:Zoom x="{zoom.ToString("0.###", inv)}" space="http://www.onvif.org/ver10/tptz/ZoomSpaces/VelocityGenericSpace"/>
              </tptz:Velocity>
              <tptz:Timeout>PT{timeout}S</tptz:Timeout>
            </tptz:ContinuousMove>
            """;

        var (ok, _, err) = await OnvifSoap.PostAsync(Http, session.PtzUrl,
            "http://www.onvif.org/ver20/ptz/wsdl/ContinuousMove",
            body, device.Username, device.Password, ct);

        return ok ? DriverResult.Success() : DriverResult.Fail(err);
    }

    private async Task<DriverResult> StopAsync(Device device, Session session, CancellationToken ct)
    {
        var body = $"""
            <tptz:Stop>
              <tptz:ProfileToken>{OnvifSoap.Xml(session.ProfileToken)}</tptz:ProfileToken>
              <tptz:PanTilt>true</tptz:PanTilt>
              <tptz:Zoom>true</tptz:Zoom>
            </tptz:Stop>
            """;

        var (ok, _, err) = await OnvifSoap.PostAsync(Http, session.PtzUrl,
            "http://www.onvif.org/ver20/ptz/wsdl/Stop",
            body, device.Username, device.Password, ct);

        return ok ? DriverResult.Success() : DriverResult.Fail(err);
    }

    private async Task<DriverResult> GotoPresetAsync(
        Device device, Session session, IDictionary<string, string>? p, CancellationToken ct)
    {
        var preset = p is not null && p.TryGetValue("preset", out var pr) ? pr : "1";
        var token = await ResolvePresetTokenAsync(device, session, preset, ct) ?? preset;

        var body = $"""
            <tptz:GotoPreset>
              <tptz:ProfileToken>{OnvifSoap.Xml(session.ProfileToken)}</tptz:ProfileToken>
              <tptz:PresetToken>{OnvifSoap.Xml(token)}</tptz:PresetToken>
            </tptz:GotoPreset>
            """;

        var (ok, _, err) = await OnvifSoap.PostAsync(Http, session.PtzUrl,
            "http://www.onvif.org/ver20/ptz/wsdl/GotoPreset",
            body, device.Username, device.Password, ct);

        return ok
            ? DriverResult.Success(new Dictionary<string, string> { ["preset"] = preset })
            : DriverResult.Fail(err);
    }

    private async Task<DriverResult> SetPresetAsync(
        Device device, Session session, IDictionary<string, string>? p, CancellationToken ct)
    {
        var preset = p is not null && p.TryGetValue("preset", out var pr) ? pr : "1";
        var name = p is not null && p.TryGetValue("name", out var n) ? n : $"Preset {preset}";

        var body = $"""
            <tptz:SetPreset>
              <tptz:ProfileToken>{OnvifSoap.Xml(session.ProfileToken)}</tptz:ProfileToken>
              <tptz:PresetName>{OnvifSoap.Xml(name)}</tptz:PresetName>
              <tptz:PresetToken>{OnvifSoap.Xml(preset)}</tptz:PresetToken>
            </tptz:SetPreset>
            """;

        var (ok, _, err) = await OnvifSoap.PostAsync(Http, session.PtzUrl,
            "http://www.onvif.org/ver20/ptz/wsdl/SetPreset",
            body, device.Username, device.Password, ct);

        return ok
            ? DriverResult.Success(new Dictionary<string, string> { ["preset"] = preset, ["name"] = name })
            : DriverResult.Fail(err);
    }

    private async Task<DriverResult> GetPresetsAsync(Device device, Session session, CancellationToken ct)
    {
        var map = await LoadPresetsAsync(device, session, ct);
        return map is null
            ? DriverResult.Fail("Não foi possível listar presets ONVIF.")
            : DriverResult.Success(map);
    }

    private async Task<Dictionary<string, string>?> LoadPresetsAsync(
        Device device, Session session, CancellationToken ct)
    {
        var body = $"""
            <tptz:GetPresets>
              <tptz:ProfileToken>{OnvifSoap.Xml(session.ProfileToken)}</tptz:ProfileToken>
            </tptz:GetPresets>
            """;

        var (ok, doc, _) = await OnvifSoap.PostAsync(Http, session.PtzUrl,
            "http://www.onvif.org/ver20/ptz/wsdl/GetPresets",
            body, device.Username, device.Password, ct);
        if (!ok || doc is null) return null;

        return ParsePresets(doc);
    }

    /// <summary>Parse público para testes unitários.</summary>
    internal static Dictionary<string, string> ParsePresets(XDocument doc)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in OnvifSoap.Locals(doc, "Preset"))
        {
            var token = preset.Attributes().FirstOrDefault(a => a.Name.LocalName == "token")?.Value
                ?? preset.Elements().FirstOrDefault(e => e.Name.LocalName == "token")?.Value
                ?? preset.Elements().FirstOrDefault(e => e.Name.LocalName == "PresetToken")?.Value;
            var name = preset.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value
                ?? preset.Elements().FirstOrDefault(e => e.Name.LocalName == "PresetName")?.Value
                ?? token;
            if (!string.IsNullOrWhiteSpace(token))
                map[token!] = string.IsNullOrWhiteSpace(name) ? token! : name!;
        }
        return map;
    }

    private async Task<string?> ResolvePresetTokenAsync(
        Device device, Session session, string preset, CancellationToken ct)
    {
        var map = await LoadPresetsAsync(device, session, ct);
        if (map is null || map.Count == 0) return preset;
        if (map.ContainsKey(preset)) return preset;

        // Operador manda "1","2" — tenta casar por nome ou por índice 1-based.
        if (int.TryParse(preset, out var idx) && idx >= 1 && idx <= map.Count)
            return map.Keys.ElementAt(idx - 1);

        var byName = map.FirstOrDefault(kv =>
            string.Equals(kv.Value, preset, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrEmpty(byName.Key) ? preset : byName.Key;
    }

    private static double ParseNorm(IDictionary<string, string>? p, string key)
    {
        if (p is null || !p.TryGetValue(key, out var v)) return 0;
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return 0;
        return Math.Clamp(d, -1, 1);
    }
}
