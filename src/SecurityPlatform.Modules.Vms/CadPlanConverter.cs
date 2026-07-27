using System.Globalization;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using Microsoft.Extensions.Logging;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Converte plantas CAD (DWG/DXF) em SVG para o mapa sinóptico no browser.
/// Usa ACadSharp (sem AutoCAD instalado). Lê Model Space completo: blocos (INSERT),
/// polilinhas com bulge, arcos, elipses, splines, solids, hatches, etc.
/// </summary>
public static class CadPlanConverter
{
    private const int MaxInsertDepth = 24;
    private const int MaxPaths = 250_000;
    private const int ArcSegmentsMin = 8;
    private const int ArcSegmentsMax = 72;
    private const int EllipseSegments = 48;
    private const int SplineSegments = 36;

    /// <summary>Descarta coordenadas absurdas (lixo de transform/hatch/arc).</summary>
    private const double AbsCoordLimit = 1e10;

    public static bool IsCadExtension(string ext)
    {
        ext = ext.ToLowerInvariant();
        return ext is ".dwg" or ".dxf";
    }

    public static (int Width, int Height) ConvertToSvg(
        string cadPath, string svgPath, ILogger? log = null)
    {
        CadDocument doc;
        var ext = Path.GetExtension(cadPath).ToLowerInvariant();
        if (ext == ".dwg")
            doc = DwgReader.Read(cadPath, (_, e) => log?.LogDebug("CAD: {Msg}", e.Message));
        else if (ext == ".dxf")
            doc = DxfReader.Read(cadPath, (_, e) => log?.LogDebug("CAD: {Msg}", e.Message));
        else
            throw new InvalidOperationException("Formato CAD não suportado: " + ext);

        var modelEntities = doc.ModelSpace?.Entities?.ToList()
            ?? doc.Entities?.ToList()
            ?? [];

        if (modelEntities.Count == 0)
            throw new InvalidOperationException("O arquivo CAD não contém entidades no Model Space.");

        var items = new List<PathItem>(Math.Min(modelEntities.Count * 4, 65_536));
        var stats = new CollectStats();

        foreach (var ent in modelEntities)
        {
            try
            {
                Collect(ent, items, depth: 0, stats);
            }
            catch (Exception ex)
            {
                stats.Errors++;
                log?.LogDebug(ex, "Entidade CAD ignorada: {Type}", ent.GetType().Name);
            }

            if (items.Count >= MaxPaths)
            {
                log?.LogWarning("CAD: limite de {Max} caminhos SVG atingido; truncando.", MaxPaths);
                break;
            }
        }

        // Filtra caminhos com bbox inválido / outliers que destroem a view da planta
        var valid = items.Where(i => i.IsValid).ToList();
        var filteredOut = 0;
        if (valid.Count >= 8)
        {
            var kept = FilterOutliers(valid);
            filteredOut = valid.Count - kept.Count;
            valid = kept;
        }

        log?.LogInformation(
            "CAD convert: source={Source} model={Model} drawn={Drawn} inserts={Ins} skippedLayer={SkipL} skippedInv={SkipI} unknown={Unk} errors={Err} paths={Paths} outliers={Out}",
            Path.GetFileName(cadPath), modelEntities.Count, stats.Drawn, stats.Inserts,
            stats.SkippedLayer, stats.SkippedInvisible, stats.Unknown, stats.Errors, valid.Count, filteredOut);

        if (valid.Count == 0)
            throw new InvalidOperationException(
                "Não foi possível extrair geometria desenhável do DWG/DXF " +
                $"(model={modelEntities.Count}, inserts={stats.Inserts}, skipLayer={stats.SkippedLayer}).");

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var it in valid)
        {
            if (it.MinX < minX) minX = it.MinX;
            if (it.MinY < minY) minY = it.MinY;
            if (it.MaxX > maxX) maxX = it.MaxX;
            if (it.MaxY > maxY) maxY = it.MaxY;
        }

        var w = Math.Max(maxX - minX, 1e-6);
        var h = Math.Max(maxY - minY, 1e-6);
        var pad = Math.Max(w, h) * 0.02;
        minX -= pad; minY -= pad; maxX += pad; maxY += pad;
        w = maxX - minX;
        h = maxY - minY;

        // Com vector-effect=non-scaling-stroke o valor é efetivamente em px de tela.
        const double stroke = 0.9;

        var sb = new StringBuilder(Math.Min(valid.Count * 80 + 512, 16 * 1024 * 1024));
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.AppendLine(FormattableString.Invariant(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{minX:0.###} {-maxY:0.###} {w:0.###} {h:0.###}\" width=\"100%\" height=\"100%\" preserveAspectRatio=\"xMidYMid meet\">"));
        sb.AppendLine("""  <rect width="100%" height="100%" fill="#0a0e14"/>""");
        sb.AppendLine(FormattableString.Invariant(
            $"  <g fill=\"none\" stroke=\"#c9d1d9\" stroke-width=\"{stroke:0.####}\" stroke-linecap=\"round\" stroke-linejoin=\"round\" vector-effect=\"non-scaling-stroke\">"));
        foreach (var it in valid)
            sb.Append("    ").AppendLine(it.Svg);
        sb.AppendLine("  </g>");
        sb.AppendLine("</svg>");

        Directory.CreateDirectory(Path.GetDirectoryName(svgPath)!);
        File.WriteAllText(svgPath, sb.ToString(), new UTF8Encoding(false));

        var aspect = h / w;
        var pxW = 1600;
        var pxH = (int)Math.Clamp(Math.Round(pxW * aspect), 480, 2400);
        if (aspect > 1.5) { pxH = 1400; pxW = (int)Math.Clamp(Math.Round(pxH / aspect), 640, 2400); }
        return (pxW, pxH);
    }

    /// <summary>
    /// Mantém o cluster principal da planta: descarta caminhos cujo centro ou tamanho
    /// estão muito longe da mediana (hatches/arcos com transform corrompido, etc.).
    /// </summary>
    private static List<PathItem> FilterOutliers(List<PathItem> items)
    {
        // Centros e semi-diagonais
        var cx = new double[items.Count];
        var cy = new double[items.Count];
        var half = new double[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            cx[i] = (it.MinX + it.MaxX) * 0.5;
            cy[i] = (it.MinY + it.MaxY) * 0.5;
            var dw = it.MaxX - it.MinX;
            var dh = it.MaxY - it.MinY;
            half[i] = Math.Sqrt(dw * dw + dh * dh) * 0.5;
        }

        var medCx = Median(cx);
        var medCy = Median(cy);
        var medHalf = Math.Max(Median(half), 1e-6);

        // Raio de aceitação: 80× o tamanho mediano do elemento (plantas grandes + blocos distantes)
        // e tamanho individual até 200× a mediana
        var maxDist = medHalf * 80;
        var maxSize = medHalf * 200;

        // Se a mediana for minúscula (só pontos), usa desvio dos centros
        var dist = new double[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var dx = cx[i] - medCx;
            var dy = cy[i] - medCy;
            dist[i] = Math.Sqrt(dx * dx + dy * dy);
        }
        var medDist = Median(dist);
        // Envelope típico da planta ~ percentil alto dos centros
        var sortedDist = (double[])dist.Clone();
        Array.Sort(sortedDist);
        var p95 = sortedDist[Math.Min(sortedDist.Length - 1, (int)(sortedDist.Length * 0.95))];
        var plantRadius = Math.Max(Math.Max(p95 * 1.5, medDist * 8), medHalf * 20);
        // Limite duro razoável
        plantRadius = Math.Min(Math.Max(plantRadius, p95 * 1.2), Math.Max(maxDist * 2, p95 * 4));

        var kept = new List<PathItem>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            if (half[i] > maxSize && half[i] > plantRadius * 0.5)
                continue; // elemento gigante (arco/hatch ruim)
            if (dist[i] > plantRadius * 1.2 && half[i] < medHalf * 0.01)
                continue; // ponto isolado longe
            if (dist[i] > plantRadius * 2.5)
                continue; // fora do envelope da planta
            kept.Add(items[i]);
        }

        // Se filtrou demais, devolve quase tudo menos os absurdos de tamanho
        if (kept.Count < Math.Max(8, items.Count / 20))
        {
            kept.Clear();
            for (var i = 0; i < items.Count; i++)
            {
                if (half[i] <= maxSize * 5)
                    kept.Add(items[i]);
            }
        }

        return kept.Count > 0 ? kept : items;
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0) return 0;
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        var m = copy.Length / 2;
        return copy.Length % 2 == 0 ? (copy[m - 1] + copy[m]) * 0.5 : copy[m];
    }

    private sealed class CollectStats
    {
        public int Drawn;
        public int Inserts;
        public int SkippedLayer;
        public int SkippedInvisible;
        public int Unknown;
        public int Errors;
    }

    private sealed class PathItem
    {
        public required string Svg;
        public double MinX, MinY, MaxX, MaxY;
        public bool IsValid =>
            !double.IsInfinity(MinX) && !double.IsInfinity(MinY) &&
            !double.IsInfinity(MaxX) && !double.IsInfinity(MaxY) &&
            MaxX >= MinX && MaxY >= MinY &&
            Math.Abs(MinX) < AbsCoordLimit && Math.Abs(MaxX) < AbsCoordLimit &&
            Math.Abs(MinY) < AbsCoordLimit && Math.Abs(MaxY) < AbsCoordLimit;
    }

    private sealed class Bounds
    {
        public double MinX = double.PositiveInfinity;
        public double MinY = double.PositiveInfinity;
        public double MaxX = double.NegativeInfinity;
        public double MaxY = double.NegativeInfinity;
        public bool Any => !double.IsInfinity(MinX);

        public void Expand(double x, double y)
        {
            if (!IsFiniteCoord(x, y)) return;
            if (x < MinX) MinX = x;
            if (y < MinY) MinY = y;
            if (x > MaxX) MaxX = x;
            if (y > MaxY) MaxY = y;
        }

        public void Merge(Bounds o)
        {
            if (!o.Any) return;
            Expand(o.MinX, o.MinY);
            Expand(o.MaxX, o.MaxY);
        }
    }

    private static bool IsFiniteCoord(double x, double y) =>
        !double.IsNaN(x) && !double.IsNaN(y) &&
        !double.IsInfinity(x) && !double.IsInfinity(y) &&
        Math.Abs(x) < AbsCoordLimit && Math.Abs(y) < AbsCoordLimit;

    private static void Collect(Entity ent, List<PathItem> items, int depth, CollectStats stats)
    {
        if (items.Count >= MaxPaths) return;
        if (ent is null) return;

        if (ent.IsInvisible)
        {
            stats.SkippedInvisible++;
            return;
        }

        if (!IsLayerDrawable(ent.Layer))
        {
            stats.SkippedLayer++;
            return;
        }

        if (ent is Insert ins)
        {
            stats.Inserts++;
            CollectInsert(ins, items, depth, stats);
            return;
        }

        var stroke = ColorToSvg(ent);

        if (ent is Line ln)
        {
            if (!IsFiniteCoord(ln.StartPoint.X, ln.StartPoint.Y) ||
                !IsFiniteCoord(ln.EndPoint.X, ln.EndPoint.Y))
                return;
            var b = new Bounds();
            b.Expand(ln.StartPoint.X, ln.StartPoint.Y);
            b.Expand(ln.EndPoint.X, ln.EndPoint.Y);
            items.Add(new PathItem
            {
                Svg = S($"<line x1=\"{ln.StartPoint.X:0.###}\" y1=\"{-ln.StartPoint.Y:0.###}\" x2=\"{ln.EndPoint.X:0.###}\" y2=\"{-ln.EndPoint.Y:0.###}\"{stroke}/>"),
                MinX = b.MinX, MinY = b.MinY, MaxX = b.MaxX, MaxY = b.MaxY
            });
            stats.Drawn++;
            return;
        }

        if (ent is LwPolyline lw && lw.Vertices.Count >= 2)
        {
            if (TryAddPolylineWithBulges(
                    lw.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)),
                    lw.IsClosed, stroke, items))
                stats.Drawn++;
            return;
        }

        if (ent is IPolyline ipl)
        {
            var verts = ipl.Vertices?.ToList();
            if (verts is { Count: >= 2 })
            {
                if (TryAddPolylineWithBulges(
                        verts.Select(v =>
                        {
                            double x = 0, y = 0;
                            try
                            {
                                var loc = v.Location;
                                x = loc[0];
                                y = loc.Dimension > 1 ? loc[1] : 0;
                            }
                            catch { /* */ }
                            return (x, y, v.Bulge);
                        }),
                        ipl.IsClosed, stroke, items))
                    stats.Drawn++;
            }
            return;
        }

        if (ent is Circle c && ent is not Arc)
        {
            if (c.Radius > 0 && c.Radius < AbsCoordLimit &&
                IsFiniteCoord(c.Center.X, c.Center.Y))
            {
                items.Add(new PathItem
                {
                    Svg = S($"<circle cx=\"{c.Center.X:0.###}\" cy=\"{-c.Center.Y:0.###}\" r=\"{c.Radius:0.###}\"{stroke}/>"),
                    MinX = c.Center.X - c.Radius, MinY = c.Center.Y - c.Radius,
                    MaxX = c.Center.X + c.Radius, MaxY = c.Center.Y + c.Radius
                });
                stats.Drawn++;
            }
            return;
        }

        if (ent is Arc a)
        {
            if (a.Radius > 0 && a.Radius < AbsCoordLimit * 0.1 &&
                IsFiniteCoord(a.Center.X, a.Center.Y))
            {
                try
                {
                    // PolygonalVertexes do ACadSharp respeita Normal e ângulos
                    var pts = a.PolygonalVertexes(Math.Clamp(
                        (int)(Math.Abs(NormalizeSweep(a.StartAngle, a.EndAngle)) / (Math.PI * 2) * 48),
                        ArcSegmentsMin, ArcSegmentsMax));
                    if (TryAddPolyPath(pts.Select(p => (p.X, p.Y)), closed: false, stroke, items))
                        stats.Drawn++;
                }
                catch
                {
                    if (TryAddArcManual(a.Center.X, a.Center.Y, a.Radius, a.StartAngle, a.EndAngle, stroke, items))
                        stats.Drawn++;
                }
            }
            return;
        }

        if (ent is Ellipse ell)
        {
            try
            {
                if (ell.MajorAxis > AbsCoordLimit) return;
                var pts = ell.PolygonalVertexes(EllipseSegments);
                if (TryAddPolyPath(pts.Select(p => (p.X, p.Y)), ell.IsFullEllipse, stroke, items))
                    stats.Drawn++;
            }
            catch { /* elipse inválida */ }
            return;
        }

        if (ent is Spline sp)
        {
            List<XYZ>? pts = null;
            try
            {
                var poly = sp.PolygonalVertexes(SplineSegments);
                if (poly is { Count: >= 2 }) pts = poly;
            }
            catch { /* */ }

            if (pts is null && sp.FitPoints.Count >= 2)
                pts = sp.FitPoints;
            if (pts is null && sp.ControlPoints.Count >= 2)
                pts = sp.ControlPoints;

            if (pts is { Count: >= 2 } &&
                TryAddPolyPath(pts.Select(p => (p.X, p.Y)), sp.IsClosed, stroke, items))
                stats.Drawn++;
            return;
        }

        if (ent is Solid sol)
        {
            var pts = new (double X, double Y)[]
            {
                (sol.FirstCorner.X, sol.FirstCorner.Y),
                (sol.SecondCorner.X, sol.SecondCorner.Y),
                (sol.FourthCorner.X, sol.FourthCorner.Y),
                (sol.ThirdCorner.X, sol.ThirdCorner.Y),
            };
            if (TryAddPolyPath(pts, closed: true, stroke + " fill=\"#58a6ff\" fill-opacity=\"0.18\"", items))
                stats.Drawn++;
            return;
        }

        if (ent is Face3D face)
        {
            var pts = new[] { face.FirstCorner, face.SecondCorner, face.ThirdCorner, face.FourthCorner };
            if (TryAddPolyPath(pts.Select(p => (p.X, p.Y)), closed: true, stroke, items))
                stats.Drawn++;
            return;
        }

        if (ent is Hatch hatch)
        {
            try
            {
                foreach (var edgeEnt in hatch.Explode())
                {
                    try { Collect(edgeEnt, items, depth, stats); }
                    catch { /* */ }
                }
            }
            catch { /* hatch ignorado */ }
            return;
        }

        if (ent is Point pt)
        {
            if (!IsFiniteCoord(pt.Location.X, pt.Location.Y)) return;
            items.Add(new PathItem
            {
                Svg = S($"<circle cx=\"{pt.Location.X:0.###}\" cy=\"{-pt.Location.Y:0.###}\" r=\"0.5\" fill=\"#c9d1d9\" stroke=\"none\"/>"),
                MinX = pt.Location.X, MinY = pt.Location.Y,
                MaxX = pt.Location.X, MaxY = pt.Location.Y
            });
            stats.Drawn++;
            return;
        }

        if (ent is Leader leader)
        {
            try
            {
                if (leader.Vertices is { Count: >= 2 } &&
                    TryAddPolyPath(leader.Vertices.Select(v => (v.X, v.Y)), closed: false, stroke, items))
                    stats.Drawn++;
            }
            catch { /* */ }
            return;
        }

        stats.Unknown++;
    }

    private static void CollectInsert(Insert ins, List<PathItem> items, int depth, CollectStats stats)
    {
        if (depth >= MaxInsertDepth) return;
        if (ins.Block is null) return;
        if (!IsFiniteCoord(ins.InsertPoint.X, ins.InsertPoint.Y)) return;

        // Escala absurda → skip
        if (Math.Abs(ins.XScale) > 1e6 || Math.Abs(ins.YScale) > 1e6) return;

        List<Entity> exploded = [];
        try
        {
            var t = ins.GetTransform();
            foreach (var be in ins.Block.Entities)
            {
                try
                {
                    if (be is null || be.IsInvisible) continue;
                    if (!IsLayerDrawable(be.Layer)) continue;
                    var clone = be.Clone() as Entity;
                    if (clone is null) continue;
                    clone.ApplyTransform(t);
                    exploded.Add(clone);
                }
                catch { /* */ }
            }
        }
        catch
        {
            return;
        }

        if (exploded.Count == 0) return;

        var rows = Math.Max(1, (int)ins.RowCount);
        var cols = Math.Max(1, (int)ins.ColumnCount);

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                IEnumerable<Entity> cellEntities = exploded;
                if (r != 0 || c != 0)
                {
                    var lx = c * ins.ColumnSpacing * ins.XScale;
                    var ly = r * ins.RowSpacing * ins.YScale;
                    var cos = Math.Cos(ins.Rotation);
                    var sin = Math.Sin(ins.Rotation);
                    var offset = new XYZ(lx * cos - ly * sin, lx * sin + ly * cos, 0);
                    var shifted = new List<Entity>(exploded.Count);
                    foreach (var e in exploded)
                    {
                        try
                        {
                            var clone = e.Clone() as Entity;
                            if (clone is null) continue;
                            clone.ApplyTranslation(offset);
                            shifted.Add(clone);
                        }
                        catch { /* */ }
                    }
                    cellEntities = shifted;
                }

                foreach (var e in cellEntities)
                {
                    try { Collect(e, items, depth + 1, stats); }
                    catch { stats.Errors++; }
                    if (items.Count >= MaxPaths) return;
                }
            }
        }
    }

    private static bool IsLayerDrawable(Layer? layer)
    {
        if (layer is null) return true;
        if (!layer.IsOn) return false;
        try
        {
            if (layer.Flags.HasFlag(LayerFlags.Frozen)) return false;
        }
        catch { /* Flags type ambiguity — ignore */ }
        return true;
    }

    private static string ColorToSvg(Entity ent)
    {
        try
        {
            var col = ent.GetActiveColor();
            if (col.IsByLayer || col.IsByBlock) return "";
            if (col.IsTrueColor)
            {
                var r = col.R; var g = col.G; var b = col.B;
                if (r < 20 && g < 20 && b < 20) return "";
                return S($" stroke=\"#{r:X2}{g:X2}{b:X2}\"");
            }
            return col.Index switch
            {
                1 => " stroke=\"#FF6B6B\"",
                2 => " stroke=\"#F0E68C\"",
                3 => " stroke=\"#7CFC00\"",
                4 => " stroke=\"#40E0D0\"",
                5 => " stroke=\"#6CB4EE\"",
                6 => " stroke=\"#DA70D6\"",
                8 => " stroke=\"#9AA4B2\"",
                9 => " stroke=\"#C0C0C0\"",
                _ => ""
            };
        }
        catch { return ""; }
    }

    private static double NormalizeSweep(double start, double end)
    {
        if (end < start) end += Math.PI * 2;
        return end - start;
    }

    private static bool TryAddArcManual(
        double cx, double cy, double radius, double startAng, double endAng,
        string stroke, List<PathItem> items)
    {
        var start = startAng;
        var end = endAng;
        if (end < start) end += Math.PI * 2;
        var sweep = end - start;
        if (sweep <= 1e-12) return false;

        var steps = Math.Clamp((int)(Math.Abs(sweep) / (Math.PI * 2) * 48), ArcSegmentsMin, ArcSegmentsMax);
        var d = new StringBuilder();
        var b = new Bounds();
        for (var i = 0; i <= steps; i++)
        {
            var t = start + sweep * i / steps;
            var x = cx + radius * Math.Cos(t);
            var y = cy + radius * Math.Sin(t);
            if (!IsFiniteCoord(x, y)) return false;
            b.Expand(x, y);
            d.Append(i == 0 ? S($"M {x:0.###} {-y:0.###}") : S($" L {x:0.###} {-y:0.###}"));
        }
        if (!b.Any) return false;
        items.Add(new PathItem
        {
            Svg = $"<path d=\"{d}\"{stroke}/>",
            MinX = b.MinX, MinY = b.MinY, MaxX = b.MaxX, MaxY = b.MaxY
        });
        return true;
    }

    private static bool TryAddPolylineWithBulges(
        IEnumerable<(double X, double Y, double Bulge)> vertices,
        bool closed, string stroke, List<PathItem> items)
    {
        var list = vertices.ToList();
        if (list.Count < 2) return false;

        var d = new StringBuilder();
        var b = new Bounds();
        var count = list.Count;
        var segments = closed ? count : count - 1;

        for (var i = 0; i < segments; i++)
        {
            var a = list[i];
            var c = list[(i + 1) % count];
            if (!IsFiniteCoord(a.X, a.Y) || !IsFiniteCoord(c.X, c.Y))
                return false;
            b.Expand(a.X, a.Y);

            if (i == 0)
                d.Append(S($"M {a.X:0.###} {-a.Y:0.###}"));

            if (Math.Abs(a.Bulge) < 1e-10)
            {
                b.Expand(c.X, c.Y);
                d.Append(S($" L {c.X:0.###} {-c.Y:0.###}"));
            }
            else if (!AppendBulgeArc(d, b, a.X, a.Y, c.X, c.Y, a.Bulge))
            {
                b.Expand(c.X, c.Y);
                d.Append(S($" L {c.X:0.###} {-c.Y:0.###}"));
            }
        }

        if (closed) d.Append(" Z");
        if (!b.Any) return false;
        items.Add(new PathItem
        {
            Svg = $"<path d=\"{d}\"{stroke}/>",
            MinX = b.MinX, MinY = b.MinY, MaxX = b.MaxX, MaxY = b.MaxY
        });
        return true;
    }

    private static bool AppendBulgeArc(
        StringBuilder d, Bounds b,
        double x1, double y1, double x2, double y2, double bulge)
    {
        try
        {
            var p1 = new XY(x1, y1);
            var p2 = new XY(x2, y2);
            var center = Arc.GetCenter(p1, p2, bulge, out var radius);
            if (radius <= 0 || radius > AbsCoordLimit * 0.1 ||
                !IsFiniteCoord(center.X, center.Y))
                return false;

            var included = 4.0 * Math.Atan(Math.Abs(bulge));
            if (included > Math.PI * 2) included = Math.PI * 2;
            var steps = Math.Clamp((int)(included / (Math.PI * 2) * 48), 4, ArcSegmentsMax);
            var a0 = Math.Atan2(y1 - center.Y, x1 - center.X);
            var dir = bulge >= 0 ? 1.0 : -1.0;

            for (var i = 1; i <= steps; i++)
            {
                var t = a0 + dir * included * i / steps;
                var x = i == steps ? x2 : center.X + radius * Math.Cos(t);
                var y = i == steps ? y2 : center.Y + radius * Math.Sin(t);
                if (!IsFiniteCoord(x, y)) return false;
                b.Expand(x, y);
                d.Append(S($" L {x:0.###} {-y:0.###}"));
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryAddPolyPath(
        IEnumerable<(double X, double Y)> points,
        bool closed, string stroke, List<PathItem> items)
    {
        var first = true;
        var d = new StringBuilder();
        var b = new Bounds();
        var n = 0;
        foreach (var (x, y) in points)
        {
            if (!IsFiniteCoord(x, y)) continue;
            b.Expand(x, y);
            d.Append(first
                ? S($"M {x:0.###} {-y:0.###}")
                : S($" L {x:0.###} {-y:0.###}"));
            first = false;
            n++;
        }
        if (n < 2 || !b.Any) return false;
        // Descarta polilinhas com span absurdo (elipse/spline corrompida)
        var span = Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY);
        if (span > AbsCoordLimit * 0.05) return false;
        if (closed) d.Append(" Z");
        items.Add(new PathItem
        {
            Svg = $"<path d=\"{d}\"{stroke}/>",
            MinX = b.MinX, MinY = b.MinY, MaxX = b.MaxX, MaxY = b.MaxY
        });
        return true;
    }

    private static string S(FormattableString fs) => FormattableString.Invariant(fs);
}
