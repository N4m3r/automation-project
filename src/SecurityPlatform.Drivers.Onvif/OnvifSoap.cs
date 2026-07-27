using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SecurityPlatform.Drivers.Onvif;

/// <summary>
/// Cliente SOAP ONVIF mínimo (Device / Media / PTZ) com WS-Security UsernameToken.
/// Sem SDK externo: suficiente para ContinuousMove, Stop e presets.
/// </summary>
internal static class OnvifSoap
{
    internal static readonly XNamespace SoapEnv = "http://www.w3.org/2003/05/soap-envelope";
    internal static readonly XNamespace Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    internal static readonly XNamespace Wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    internal static readonly XNamespace Tds = "http://www.onvif.org/ver10/device/wsdl";
    internal static readonly XNamespace Trt = "http://www.onvif.org/ver10/media/wsdl";
    internal static readonly XNamespace Tptz = "http://www.onvif.org/ver20/ptz/wsdl";
    internal static readonly XNamespace Tt = "http://www.onvif.org/ver10/schema";

    public static string BuildEnvelope(string bodyXml, string username, string password)
    {
        var created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var nonceBytes = RandomNumberGenerator.GetBytes(16);
        var nonceB64 = Convert.ToBase64String(nonceBytes);
        var digest = PasswordDigest(nonceBytes, created, password);

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
                        xmlns:tds="http://www.onvif.org/ver10/device/wsdl"
                        xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                        xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"
                        xmlns:tt="http://www.onvif.org/ver10/schema">
              <s:Header>
                <Security xmlns="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd" s:mustUnderstand="1">
                  <UsernameToken>
                    <Username>{Xml(username)}</Username>
                    <Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest">{digest}</Password>
                    <Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary">{nonceB64}</Nonce>
                    <Created xmlns="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">{created}</Created>
                  </UsernameToken>
                </Security>
              </s:Header>
              <s:Body>
                {bodyXml}
              </s:Body>
            </s:Envelope>
            """;
    }

    /// <summary>PasswordDigest = Base64(SHA1(nonce + created + password)).</summary>
    public static string PasswordDigest(byte[] nonce, string createdUtc, string password)
    {
        var createdBytes = Encoding.UTF8.GetBytes(createdUtc);
        var passBytes = Encoding.UTF8.GetBytes(password ?? "");
        var raw = new byte[nonce.Length + createdBytes.Length + passBytes.Length];
        Buffer.BlockCopy(nonce, 0, raw, 0, nonce.Length);
        Buffer.BlockCopy(createdBytes, 0, raw, nonce.Length, createdBytes.Length);
        Buffer.BlockCopy(passBytes, 0, raw, nonce.Length + createdBytes.Length, passBytes.Length);
        return Convert.ToBase64String(SHA1.HashData(raw));
    }

    public static string Xml(string? s) =>
        System.Security.SecurityElement.Escape(s ?? "") ?? "";

    public static async Task<(bool Ok, XDocument? Doc, string Error)> PostAsync(
        HttpClient http, string url, string action, string bodyXml,
        string username, string password, CancellationToken ct)
    {
        try
        {
            var envelope = BuildEnvelope(bodyXml, username, password);
            using var content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml")
            {
                CharSet = "utf-8",
                Parameters = { new NameValueHeaderValue("action", $"\"{action}\"") }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
                return (false, null, $"HTTP {(int)res.StatusCode}: {Truncate(text)}");

            var doc = XDocument.Parse(text);
            if (HasFault(doc, out var fault))
                return (false, doc, fault);

            return (true, doc, "");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or XmlException)
        {
            return (false, null, e.Message);
        }
    }

    public static bool HasFault(XDocument doc, out string message)
    {
        var fault = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName is "Fault" or "faultstring" or "Reason");
        if (fault is null)
        {
            message = "";
            return false;
        }

        var text = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName is "Text" or "faultstring" or "Reason")
            ?.Value?.Trim();
        message = string.IsNullOrWhiteSpace(text) ? "SOAP Fault" : text;
        return true;
    }

    public static string? LocalValue(XDocument doc, string localName) =>
        doc.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value?.Trim();

    public static IEnumerable<XElement> Locals(XDocument doc, string localName) =>
        doc.Descendants().Where(e => e.Name.LocalName == localName);

    public static string DeviceServiceUrl(string host, int port)
    {
        var p = port <= 0 ? 80 : port;
        return $"http://{host}:{p}/onvif/device_service";
    }

    public static string RewriteHost(string xaddr, string host, int port)
    {
        if (string.IsNullOrWhiteSpace(xaddr)) return xaddr;
        try
        {
            var u = new Uri(xaddr);
            var builder = new UriBuilder(u)
            {
                Host = host,
                Port = port <= 0 ? (u.IsDefaultPort ? -1 : u.Port) : port
            };
            // Câmeras frequentemente anunciam 127.0.0.1 ou IP interno errado.
            if (u.Host is "127.0.0.1" or "localhost" or "::1" || IsPrivateMismatch(u.Host, host))
                builder.Host = host;
            return builder.Uri.ToString();
        }
        catch
        {
            return xaddr;
        }
    }

    private static bool IsPrivateMismatch(string announced, string real)
        => !string.Equals(announced, real, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string s) =>
        s.Length <= 200 ? s : s[..200] + "…";
}
