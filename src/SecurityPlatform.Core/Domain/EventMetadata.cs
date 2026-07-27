using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace SecurityPlatform.Core.Domain;

/// <summary>
/// Metadados estruturados de eventos de analítico embarcado (borda).
/// Serializado dentro de <see cref="DeviceEvent.Payload"/> sob a chave "meta".
/// </summary>
public sealed class EventMetadata
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";
    // motion | intrusion | line_crossing | lpr | face | tamper
    // | people_counting | thermal | abandoned | loitering | other

    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("plate")]
    public string? Plate { get; set; }

    [JsonPropertyName("plateCountry")]
    public string? PlateCountry { get; set; }

    [JsonPropertyName("vehicleType")]
    public string? VehicleType { get; set; }

    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    [JsonPropertyName("faceId")]
    public string? FaceId { get; set; }

    [JsonPropertyName("personName")]
    public string? PersonName { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("roi")]
    public EventRoi? Roi { get; set; }

    [JsonPropertyName("vendor")]
    public string? Vendor { get; set; }

    [JsonPropertyName("rawType")]
    public string? RawType { get; set; }

    [JsonPropertyName("listMatch")]
    public string? ListMatch { get; set; } // allow | deny | watch | null

    public static string NormalizePlate(string? plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return "";
        var chars = plate.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToUpperInvariant();
    }

    /// <summary>Extrai metadados de XML ISAPI Hikvision EventNotificationAlert.</summary>
    public static EventMetadata FromHikvisionXml(XDocument doc, string rawEventType, XNamespace ns)
    {
        string V(string tag) => doc.Root?.Element(ns + tag)?.Value
            ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == tag)?.Value
            ?? "";

        var meta = new EventMetadata
        {
            Vendor = "hikvision",
            RawType = rawEventType,
            Channel = V("channelID"),
            Description = V("eventDescription")
        };

        var plate = V("licensePlate") is { Length: > 0 } p ? p
            : V("plateNumber") is { Length: > 0 } p2 ? p2
            : ExtractDeep(doc, "licensePlate", "plateNumber", "plate");
        if (!string.IsNullOrEmpty(plate))
        {
            meta.Kind = "lpr";
            meta.Plate = NormalizePlate(plate);
            meta.PlateCountry = V("country") is { Length: > 0 } c ? c : ExtractDeep(doc, "country");
            meta.VehicleType = ExtractDeep(doc, "vehicleType", "vehicleTypeByFunc");
            meta.Direction = ExtractDeep(doc, "direction", "vehicleDirection");
            if (double.TryParse(ExtractDeep(doc, "confidence", "plateConfidence"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var conf))
                meta.Confidence = conf > 1 ? conf / 100.0 : conf;
        }
        else if (rawEventType.Contains("face", StringComparison.OrdinalIgnoreCase)
                 || !string.IsNullOrEmpty(ExtractDeep(doc, "faceId", "//Id")))
        {
            meta.Kind = "face";
            meta.FaceId = ExtractDeep(doc, "faceId", "//Id", "targetAttrs");
            meta.PersonName = ExtractDeep(doc, "name", "personName");
        }
        else if (rawEventType is "linedetection")
            meta.Kind = "line_crossing";
        else if (rawEventType is "fielddetection" or "regionEntrance" or "regionExiting")
            meta.Kind = "intrusion";
        else if (rawEventType is "VMD")
            meta.Kind = "motion";
        else if (rawEventType.Contains("tamper", StringComparison.OrdinalIgnoreCase)
                 || rawEventType is "shelteralarm")
            meta.Kind = "tamper";
        else if (rawEventType.Contains("people", StringComparison.OrdinalIgnoreCase)
                 || rawEventType.Contains("counting", StringComparison.OrdinalIgnoreCase)
                 || rawEventType is "framesPeopleCounting" or "personDensityDetection")
        {
            meta.Kind = "people_counting";
            if (int.TryParse(ExtractDeep(doc, "enter", "enterNum", "peopleNum", "number"), out var n))
                meta.Count = n;
        }
        else if (rawEventType.Contains("therm", StringComparison.OrdinalIgnoreCase)
                 || rawEventType.Contains("fire", StringComparison.OrdinalIgnoreCase)
                 || rawEventType.Contains("smoke", StringComparison.OrdinalIgnoreCase)
                 || rawEventType is "temperatureAlarm" or "heatImagingTemper")
        {
            meta.Kind = "thermal";
            if (double.TryParse(ExtractDeep(doc, "temperature", "currTemperature", "ruleTemperature"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var t))
                meta.Temperature = t;
        }
        else if (rawEventType.Contains("left", StringComparison.OrdinalIgnoreCase)
                 || rawEventType.Contains("abandon", StringComparison.OrdinalIgnoreCase)
                 || rawEventType is "unattendedBaggage" or "attendedBaggage")
            meta.Kind = "abandoned";
        else if (rawEventType.Contains("loiter", StringComparison.OrdinalIgnoreCase)
                 || rawEventType is "parking" or "wanderDetection")
            meta.Kind = "loitering";
        else
            meta.Kind = "other";

        // ROI normalizado 0–1 se a câmera enviar retângulo
        var x = ParseD(ExtractDeep(doc, "x", "positionX"));
        var y = ParseD(ExtractDeep(doc, "y", "positionY"));
        var w = ParseD(ExtractDeep(doc, "width", "w"));
        var h = ParseD(ExtractDeep(doc, "height", "h"));
        if (w is > 0 && h is > 0)
            meta.Roi = new EventRoi(x ?? 0, y ?? 0, w.Value, h.Value);

        return meta;
    }

    public static EventMetadata? TryParseFromPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("meta", out var m))
                return JsonSerializer.Deserialize<EventMetadata>(m.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { /* */ }
        return null;
    }

    private static string ExtractDeep(XDocument doc, params string[] localNames)
    {
        foreach (var name in localNames)
        {
            var el = doc.Descendants().FirstOrDefault(e =>
                e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (el is not null && !string.IsNullOrWhiteSpace(el.Value))
                return el.Value.Trim();
        }
        return "";
    }

    private static double? ParseD(string s)
        => double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
}

public record EventRoi(double X, double Y, double W, double H);
