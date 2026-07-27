using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SecurityPlatform.Drivers.Onvif;

/// <summary>
/// WS-Discovery (Probe) em UDP 3702 — lista câmeras ONVIF na LAN.
/// Multicast pode falhar em VLANs/redes isoladas; cadastro manual continua disponível.
/// </summary>
public static class OnvifDiscovery
{
    private static readonly IPAddress Multicast = IPAddress.Parse("239.255.255.250");
    private const int Port = 3702;

    public record DiscoveredDevice(string Host, int Port, string Name, string XAddrs, string Scopes);

    public static async Task<IReadOnlyList<DiscoveredDevice>> ProbeAsync(
        TimeSpan timeout, CancellationToken ct = default)
    {
        var messageId = $"uuid:{Guid.NewGuid()}";
        var probe = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope"
                        xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                        xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
                        xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
              <e:Header>
                <w:MessageID>{messageId}</w:MessageID>
                <w:To e:mustUnderstand="true">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>
                <w:Action a:mustUnderstand="true" xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>
              </e:Header>
              <e:Body>
                <d:Probe>
                  <d:Types>dn:NetworkVideoTransmitter</d:Types>
                </d:Probe>
              </e:Body>
            </e:Envelope>
            """;

        var found = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        udp.JoinMulticastGroup(Multicast);

        var bytes = Encoding.UTF8.GetBytes(probe);
        await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(Multicast, Port));

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            var receiveTask = udp.ReceiveAsync(ct).AsTask();
            var delayTask = Task.Delay(remaining, ct);
            var done = await Task.WhenAny(receiveTask, delayTask);
            if (done != receiveTask) break;

            UdpReceiveResult res;
            try { res = await receiveTask; }
            catch { break; }

            foreach (var d in ParseProbeMatches(Encoding.UTF8.GetString(res.Buffer)))
            {
                var key = $"{d.Host}:{d.Port}";
                found.TryAdd(key, d);
            }
        }

        try { udp.DropMulticastGroup(Multicast); } catch { /* ignore */ }
        return found.Values.OrderBy(d => d.Host).ToList();
    }

    internal static IEnumerable<DiscoveredDevice> ParseProbeMatches(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { yield break; }

        foreach (var match in doc.Descendants().Where(e => e.Name.LocalName == "ProbeMatch"))
        {
            var xaddrs = match.Descendants().FirstOrDefault(e => e.Name.LocalName == "XAddrs")?.Value?.Trim() ?? "";
            var scopes = match.Descendants().FirstOrDefault(e => e.Name.LocalName == "Scopes")?.Value?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(xaddrs)) continue;

            foreach (var addr in xaddrs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Uri.TryCreate(addr, UriKind.Absolute, out var uri)) continue;
                if (uri.Scheme is not ("http" or "https")) continue;

                var name = ExtractScopeName(scopes) ?? uri.Host;
                yield return new DiscoveredDevice(
                    uri.Host,
                    uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port,
                    name,
                    xaddrs,
                    scopes);
            }
        }
    }

    private static string? ExtractScopeName(string scopes)
    {
        // onvif://www.onvif.org/name/Camera_1
        var m = Regex.Match(scopes, @"onvif://www\.onvif\.org/name/([^\s]+)", RegexOptions.IgnoreCase);
        if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value.Replace("%20", " "));
        m = Regex.Match(scopes, @"name/([^\s]+)", RegexOptions.IgnoreCase);
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }
}
