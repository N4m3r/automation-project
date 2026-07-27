using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Polígonos de máscara de privacidade (coords normalizadas 0–1).
/// JSON: <c>[{"points":[[x,y],...]}, ...]</c>
/// </summary>
public static class PrivacyMaskHelper
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static IReadOnlyList<MaskPolygon> Parse(string? polygonsJson)
    {
        if (string.IsNullOrWhiteSpace(polygonsJson) || polygonsJson.Trim() is "[]" or "null")
            return [];

        try
        {
            var list = JsonSerializer.Deserialize<List<MaskPolygonDto>>(polygonsJson, JsonOpts);
            if (list is null) return [];

            var result = new List<MaskPolygon>();
            foreach (var dto in list)
            {
                var pts = dto.Points ?? dto.Coords;
                if (pts is null || pts.Count < 3) continue;
                var normalized = new List<(double X, double Y)>();
                foreach (var p in pts)
                {
                    if (p is not { Count: >= 2 }) continue;
                    // Aceita 0–1 (normalizado) ou 0–1000 (viewBox do monitor).
                    var x = p[0]; var y = p[1];
                    if (x > 1 || y > 1) { x /= 1000.0; y /= 1000.0; }
                    normalized.Add((Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1)));
                }
                if (normalized.Count < 3) continue;
                result.Add(new MaskPolygon(normalized));
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Bounding boxes axis-aligned (para FFmpeg drawbox).
    /// Valores em fração 0–1.
    /// </summary>
    public static IReadOnlyList<(double X, double Y, double W, double H)> ToBoundingBoxes(
        IReadOnlyList<MaskPolygon> polys)
    {
        var boxes = new List<(double, double, double, double)>();
        foreach (var poly in polys)
        {
            if (poly.Points.Count == 0) continue;
            var minX = poly.Points.Min(p => p.X);
            var minY = poly.Points.Min(p => p.Y);
            var maxX = poly.Points.Max(p => p.X);
            var maxY = poly.Points.Max(p => p.Y);
            var w = Math.Max(0.01, maxX - minX);
            var h = Math.Max(0.01, maxY - minY);
            boxes.Add((minX, minY, w, h));
        }
        return boxes;
    }

    /// <summary>
    /// Filtro FFmpeg: caixas pretas opacas sobre o vídeo.
    /// Usa expressões relativas à resolução (iw/ih).
    /// </summary>
    public static string? BuildDrawboxFilter(IReadOnlyList<(double X, double Y, double W, double H)> boxes)
    {
        if (boxes.Count == 0) return null;
        var parts = new List<string>();
        foreach (var (x, y, w, h) in boxes.Take(16))
        {
            // drawbox=x=iw*0.1:y=ih*0.2:w=iw*0.3:h=ih*0.2:color=black@1:t=fill
            parts.Add(
                $"drawbox=x=iw*{x.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                $":y=ih*{y.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                $":w=iw*{w.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                $":h=ih*{h.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                $":color=black@1:t=fill");
        }
        return string.Join(",", parts);
    }

    private sealed class MaskPolygonDto
    {
        public List<List<double>>? Points { get; set; }
        public List<List<double>>? Coords { get; set; }
    }
}

public sealed record MaskPolygon(IReadOnlyList<(double X, double Y)> Points);
