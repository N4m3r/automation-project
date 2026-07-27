using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Drivers.Onvif;

namespace SecurityPlatform.Modules.Admin;

/// <summary>
/// Enriquece a descoberta ONVIF: descarta câmeras já cadastradas ou já
/// vinculadas como canal de NVR, e tenta obter o nome OSD da câmera avulsa.
/// </summary>
public static class CameraDiscoveryEnricher
{
    public record NvrChannelSource(
        string SourceIp,
        int NvrDeviceId,
        string NvrHost,
        string NvrName,
        int ChannelId,
        string ChannelName);

    public record DiscoveredCameraRow(
        string Host,
        int Port,
        string Name,
        string? OsdName,
        string XAddrs,
        string Scopes,
        string Driver,
        bool AlreadyRegistered,
        int? RegisteredDeviceId,
        string? RegisteredName,
        bool OnNvr,
        int? NvrDeviceId,
        string? NvrHost,
        string? NvrName,
        int? NvrChannelId,
        string? NvrChannelName);

    public record DiscoveryResult(
        IReadOnlyList<DiscoveredCameraRow> Standalone,
        IReadOnlyList<DiscoveredCameraRow> Skipped,
        int FoundTotal,
        int NvrSourcesKnown);

    /// <summary>
    /// Filtra o resultado do WS-Discovery e enriquece com OSD quando houver
    /// credenciais de sonda (opcionais).
    /// </summary>
    public static async Task<DiscoveryResult> EnrichAsync(
        IReadOnlyList<OnvifDiscovery.DiscoveredDevice> found,
        IReadOnlyList<Device> registered,
        string? probeUsername,
        string? probePassword,
        CancellationToken ct = default)
    {
        var registeredByHost = new Dictionary<string, Device>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in registered)
        {
            var host = NormalizeHost(d.Host);
            if (string.IsNullOrEmpty(host)) continue;
            registeredByHost.TryAdd(host, d);
        }

        // IPs de câmeras IP já puxadas pelos NVRs cadastrados (Hikvision/Dahua).
        var nvrSources = await CollectNvrSourcesAsync(registered, ct);
        var nvrByIp = new Dictionary<string, NvrChannelSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var src in nvrSources)
        {
            var ip = NormalizeHost(src.SourceIp);
            if (string.IsNullOrEmpty(ip)) continue;
            nvrByIp.TryAdd(ip, src);
        }

        var standalone = new List<DiscoveredCameraRow>();
        var skipped = new List<DiscoveredCameraRow>();

        foreach (var d in found)
        {
            var host = NormalizeHost(d.Host);
            var already = registeredByHost.TryGetValue(host, out var reg);
            var onNvr = nvrByIp.TryGetValue(host, out var nvr);

            // Nome base: scope ONVIF; OSD será tentado só para avulsas.
            var baseName = string.IsNullOrWhiteSpace(d.Name) ? d.Host : d.Name;
            string? osd = null;

            if (!already && !onNvr)
            {
                osd = await TryFetchOsdNameAsync(
                    d.Host, d.Port, probeUsername, probePassword, ct);
            }

            var displayName = !string.IsNullOrWhiteSpace(osd) ? osd!
                : baseName;

            var row = new DiscoveredCameraRow(
                Host: d.Host,
                Port: d.Port,
                Name: displayName,
                OsdName: osd,
                XAddrs: d.XAddrs,
                Scopes: d.Scopes,
                Driver: GuessDriver(d.Scopes, d.XAddrs),
                AlreadyRegistered: already,
                RegisteredDeviceId: reg?.Id,
                RegisteredName: reg?.Name,
                OnNvr: onNvr,
                NvrDeviceId: nvr?.NvrDeviceId,
                NvrHost: nvr?.NvrHost,
                NvrName: nvr?.NvrName,
                NvrChannelId: nvr?.ChannelId,
                NvrChannelName: nvr?.ChannelName);

            if (already || onNvr) skipped.Add(row);
            else standalone.Add(row);
        }

        return new DiscoveryResult(
            Standalone: standalone.OrderBy(x => x.Host).ToList(),
            Skipped: skipped.OrderBy(x => x.Host).ToList(),
            FoundTotal: found.Count,
            NvrSourcesKnown: nvrByIp.Count);
    }

    // ---------------------------------------------------------------- NVR

    internal static async Task<IReadOnlyList<NvrChannelSource>> CollectNvrSourcesAsync(
        IReadOnlyList<Device> registered, CancellationToken ct)
    {
        var list = new List<NvrChannelSource>();
        // Em paralelo limitado: cada NVR cadastrado pode listar canais IP.
        var tasks = registered.Select(async dev =>
        {
            try
            {
                return await ListSourcesForDeviceAsync(dev, ct);
            }
            catch
            {
                return (IReadOnlyList<NvrChannelSource>)Array.Empty<NvrChannelSource>();
            }
        });

        foreach (var batch in await Task.WhenAll(tasks))
            list.AddRange(batch);

        return list;
    }

    private static async Task<IReadOnlyList<NvrChannelSource>> ListSourcesForDeviceAsync(
        Device dev, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dev.Host)) return Array.Empty<NvrChannelSource>();

        // Hikvision ISAPI InputProxy (canais IP do NVR).
        var hik = await TryHikvisionInputProxyAsync(dev, ct);
        if (hik.Count > 0) return hik;

        // Dahua / Intelbras RemoteDevice.
        var dahua = await TryDahuaRemoteDevicesAsync(dev, ct);
        if (dahua.Count > 0) return dahua;

        return Array.Empty<NvrChannelSource>();
    }

    private static async Task<IReadOnlyList<NvrChannelSource>> TryHikvisionInputProxyAsync(
        Device dev, CancellationToken ct)
    {
        using var http = CreateHttp(dev);
        try
        {
            using var res = await http.GetAsync("/ISAPI/ContentMgmt/InputProxy/channels", ct);
            if (!res.IsSuccessStatusCode) return Array.Empty<NvrChannelSource>();
            var xml = await res.Content.ReadAsStringAsync(ct);
            return ParseHikvisionInputProxy(xml, dev);
        }
        catch
        {
            return Array.Empty<NvrChannelSource>();
        }
    }

    /// <summary>Parser público para testes — lista IPs das câmeras IP no NVR Hikvision.</summary>
    public static IReadOnlyList<NvrChannelSource> ParseHikvisionInputProxy(string xml, Device nvr)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return Array.Empty<NvrChannelSource>(); }

        var list = new List<NvrChannelSource>();
        foreach (var ch in doc.Descendants().Where(e => e.Name.LocalName is "InputProxyChannel"))
        {
            var idText = ch.Elements().FirstOrDefault(e => e.Name.LocalName == "id")?.Value;
            var name = ch.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value
                       ?? ch.Descendants().FirstOrDefault(e => e.Name.LocalName == "name")?.Value
                       ?? "";
            var ip = ch.Descendants()
                .FirstOrDefault(e => e.Name.LocalName is "ipAddress" or "ipv6Address")?.Value;

            if (string.IsNullOrWhiteSpace(ip)) continue;
            _ = int.TryParse(idText, out var chId);

            list.Add(new NvrChannelSource(
                SourceIp: ip.Trim(),
                NvrDeviceId: nvr.Id,
                NvrHost: nvr.Host,
                NvrName: nvr.Name,
                ChannelId: chId,
                ChannelName: string.IsNullOrWhiteSpace(name) ? $"Canal {chId}" : name.Trim()));
        }

        return list;
    }

    private static async Task<IReadOnlyList<NvrChannelSource>> TryDahuaRemoteDevicesAsync(
        Device dev, CancellationToken ct)
    {
        using var http = CreateHttp(dev);
        try
        {
            using var res = await http.GetAsync(
                "/cgi-bin/configManager.cgi?action=getConfig&name=RemoteDevice", ct);
            if (!res.IsSuccessStatusCode) return Array.Empty<NvrChannelSource>();
            var text = await res.Content.ReadAsStringAsync(ct);
            return ParseDahuaRemoteDevices(text, dev);
        }
        catch
        {
            return Array.Empty<NvrChannelSource>();
        }
    }

    /// <summary>Parser público para testes — RemoteDevice Dahua/Intelbras.</summary>
    public static IReadOnlyList<NvrChannelSource> ParseDahuaRemoteDevices(string text, Device nvr)
    {
        // table.RemoteDevice[0].Address=192.168.1.64
        // table.RemoteDevice[0].Name=Cam01
        // table.RemoteDevice[0].Enable=true
        var byIndex = new Dictionary<int, (string? Addr, string? Name, bool? Enable)>();

        foreach (Match m in Regex.Matches(text,
                     @"RemoteDevice\[(\d+)\]\.(Address|Name|Enable|DeviceName|IPAddress)\s*=\s*(.+)",
                     RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            var idx = int.Parse(m.Groups[1].Value);
            var key = m.Groups[2].Value;
            var val = m.Groups[3].Value.Trim().TrimEnd('\r');
            byIndex.TryGetValue(idx, out var cur);
            byIndex[idx] = key.ToLowerInvariant() switch
            {
                "address" or "ipaddress" => (val, cur.Name, cur.Enable),
                "name" or "devicename" => (cur.Addr, val, cur.Enable),
                "enable" => (cur.Addr, cur.Name,
                    val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1"),
                _ => cur
            };
        }

        var list = new List<NvrChannelSource>();
        foreach (var (idx, v) in byIndex.OrderBy(x => x.Key))
        {
            if (string.IsNullOrWhiteSpace(v.Addr)) continue;
            // Enable=false: ainda conta como cadastrada no NVR (não puxar avulsa).
            list.Add(new NvrChannelSource(
                SourceIp: v.Addr.Trim(),
                NvrDeviceId: nvr.Id,
                NvrHost: nvr.Host,
                NvrName: nvr.Name,
                ChannelId: idx + 1,
                ChannelName: string.IsNullOrWhiteSpace(v.Name) ? $"Canal {idx + 1}" : v.Name!.Trim()));
        }

        return list;
    }

    // ---------------------------------------------------------------- OSD

    private static async Task<string?> TryFetchOsdNameAsync(
        string host, int port, string? user, string? pass, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user)) return null;

        var probe = new Device
        {
            Host = host,
            Port = port <= 0 ? 80 : port,
            Username = user,
            Password = pass ?? ""
        };

        // Hikvision: texto OSD e, se vazio, nome do canal / deviceName.
        var hik = await TryHikvisionOsdAsync(probe, ct);
        if (!string.IsNullOrWhiteSpace(hik)) return hik;

        // Dahua: ChannelTitle.
        var dahua = await TryDahuaChannelTitleAsync(probe, ct);
        if (!string.IsNullOrWhiteSpace(dahua)) return dahua;

        return null;
    }

    private static async Task<string?> TryHikvisionOsdAsync(Device dev, CancellationToken ct)
    {
        using var http = CreateHttp(dev);
        try
        {
            // 1) Texto OSD (displayText)
            using (var res = await http.GetAsync(
                       "/ISAPI/System/Video/inputs/channels/1/overlays", ct))
            {
                if (res.IsSuccessStatusCode)
                {
                    var xml = await res.Content.ReadAsStringAsync(ct);
                    var osd = ParseHikvisionOsd(xml);
                    if (!string.IsNullOrWhiteSpace(osd)) return osd;
                }
            }

            // 2) Nome do canal de vídeo
            using (var res = await http.GetAsync(
                       "/ISAPI/System/Video/inputs/channels/1", ct))
            {
                if (res.IsSuccessStatusCode)
                {
                    var xml = await res.Content.ReadAsStringAsync(ct);
                    var name = ParseXmlLocal(xml, "name");
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }

            // 3) deviceName
            using (var res = await http.GetAsync("/ISAPI/System/deviceInfo", ct))
            {
                if (!res.IsSuccessStatusCode) return null;
                var xml = await res.Content.ReadAsStringAsync(ct);
                return ParseXmlLocal(xml, "deviceName");
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extrai o primeiro displayText útil de overlays Hikvision.</summary>
    public static string? ParseHikvisionOsd(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return null; }

        foreach (var text in doc.Descendants()
                     .Where(e => e.Name.LocalName is "displayText" or "channelName"))
        {
            var v = text.Value?.Trim();
            if (string.IsNullOrWhiteSpace(v)) continue;
            // Ignora placeholders genéricos sem valor.
            if (v is "Camera 01" or "Camera01" or "IPCamera 01") continue;
            return v;
        }

        return null;
    }

    private static async Task<string?> TryDahuaChannelTitleAsync(Device dev, CancellationToken ct)
    {
        using var http = CreateHttp(dev);
        try
        {
            using var res = await http.GetAsync(
                "/cgi-bin/configManager.cgi?action=getConfig&name=ChannelTitle", ct);
            if (!res.IsSuccessStatusCode) return null;
            var text = await res.Content.ReadAsStringAsync(ct);
            return ParseDahuaChannelTitle(text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>table.ChannelTitle[0].Name=PORTARIA</summary>
    public static string? ParseDahuaChannelTitle(string text)
    {
        var m = Regex.Match(text,
            @"ChannelTitle\[0\]\.Name\s*=\s*(.+)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (!m.Success) return null;
        var name = m.Groups[1].Value.Trim().TrimEnd('\r');
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string? ParseXmlLocal(string xml, string localName)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            return doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == localName)?.Value?.Trim();
        }
        catch
        {
            return null;
        }
    }

    // --------------------------------------------------------------- utils

    private static HttpClient CreateHttp(Device d)
    {
        var handler = new SocketsHttpHandler
        {
            Credentials = string.IsNullOrEmpty(d.Username)
                ? null
                : new NetworkCredential(d.Username, d.Password),
            PreAuthenticate = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://{d.Host}:{(d.Port <= 0 ? 80 : d.Port)}"),
            Timeout = TimeSpan.FromSeconds(4)
        };
    }

    public static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "";
        var h = host.Trim();
        // Remove colchetes IPv6 e porta embutida em host:port (IPv4).
        if (h.StartsWith('['))
        {
            var end = h.IndexOf(']');
            if (end > 0) return h[1..end];
        }
        // IPv4 com porta
        if (h.Count(c => c == ':') == 1)
        {
            var parts = h.Split(':');
            if (int.TryParse(parts[1], out _)) return parts[0];
        }
        return h;
    }

    private static string GuessDriver(string scopes, string xaddrs)
    {
        var blob = (scopes + " " + xaddrs).ToLowerInvariant();
        if (blob.Contains("hikvision") || blob.Contains("hik")) return "hikvision";
        if (blob.Contains("dahua")) return "dahua";
        if (blob.Contains("intelbras")) return "intelbras";
        if (blob.Contains("axis")) return "axis";
        return "onvif";
    }
}
