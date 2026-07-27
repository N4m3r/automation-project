using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Mapas sinópticos 2D/3D: plantas com marcadores de câmeras/dispositivos e
/// status em tempo real (Online/Offline + eventos recentes).
/// </summary>
public static class MapEndpoints
{
    public static void MapSynopticMaps(this RouteGroupBuilder g)
    {
        // Paleta ANTES de /maps/{id} para não colidir com rotas genéricas.
        g.MapGet("/maps/palette/cameras", async (
            HttpContext ctx, PlatformDbContext db, PermissionService perms) =>
        {
            var uid = ctx.User.UserId();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uid);
            var visible = await perms.VisibleCameraIdsAsync(uid, Permissions.CameraView);
            var q = db.Devices.AsNoTracking().Where(d => d.Kind == DeviceKind.Camera);
            if (user is not { IsAdmin: true })
            {
                if (visible.Count == 0) return Results.Ok(Array.Empty<object>());
                q = q.Where(d => visible.Contains(d.Id));
            }

            var list = await q.OrderByDescending(d => d.Status == DeviceStatus.Online).ThenBy(d => d.Name)
                .Select(d => new
                {
                    d.Id, d.Name, d.Host, d.Status, d.Driver, d.LastSeen,
                    online = d.Status == DeviceStatus.Online
                })
                .ToListAsync();
            return Results.Ok(list);
        });

        // Posições cadastradas no mapa analítico (todas as plantas ativas do tenant).
        // Usado pelo painel do posto para localizar câmera → (mapa, x%, y%).
        g.MapGet("/maps/placements", async (
            HttpContext ctx, PlatformDbContext db, PermissionService perms) =>
        {
            var tenant = ctx.User.TenantId();
            var uid = ctx.User.UserId();
            var isAdmin = ctx.User.IsInRole("admin") || ctx.User.IsInRole("Administrador");
            var visible = await perms.VisibleCameraIdsAsync(uid);

            if (!isAdmin)
            {
                var canView = await perms.HasAsync(uid, Permissions.CameraView);
                if (!canView) return Results.Ok(Array.Empty<object>());
            }

            var maps = await db.SynopticMaps.AsNoTracking()
                .Where(m => m.TenantId == tenant && m.Active)
                .Select(m => new { m.Id, m.Name })
                .ToListAsync();
            var mapIds = maps.Select(m => m.Id).ToList();
            if (mapIds.Count == 0) return Results.Ok(Array.Empty<object>());

            var mapName = maps.ToDictionary(m => m.Id, m => m.Name);
            var markers = await db.MapMarkers.AsNoTracking()
                .Where(m => mapIds.Contains(m.MapId) && m.DeviceId != null)
                .Select(m => new
                {
                    m.Id,
                    m.MapId,
                    m.DeviceId,
                    m.Label,
                    m.X,
                    m.Y,
                    m.Kind,
                    m.Icon
                })
                .ToListAsync();

            var filtered = isAdmin
                ? markers
                : markers.Where(m => m.DeviceId is int did && visible.Contains(did)).ToList();

            return Results.Ok(filtered.Select(m => new
            {
                markerId = m.Id,
                mapId = m.MapId,
                mapName = mapName.GetValueOrDefault(m.MapId, ""),
                deviceId = m.DeviceId,
                label = m.Label,
                x = m.X,
                y = m.Y,
                kind = m.Kind,
                icon = m.Icon
            }));
        });

        // Lista (operador com camera.view).
        g.MapGet("/maps", async (HttpContext ctx, PlatformDbContext db, PermissionService perms) =>
        {
            // Qualquer autenticado com view em alguma câmera, ou admin, pode listar mapas.
            var uid = ctx.User.UserId();
            var tenant = ctx.User.TenantId();
            var isAdmin = ctx.User.IsInRole("admin") || ctx.User.IsInRole("Administrador");

            var maps = await db.SynopticMaps.AsNoTracking()
                .Where(m => m.TenantId == tenant && m.Active)
                .OrderBy(m => m.SortOrder).ThenBy(m => m.Name)
                .Select(m => new
                {
                    m.Id, m.Name, m.Description, m.Mode,
                    m.BackgroundUrl, m.BackgroundColor,
                    m.Width, m.Height, m.PerspectiveDeg,
                    m.SortOrder, m.UpdatedAt,
                    markers = m.Markers.Count
                })
                .ToListAsync();

            // Sem permissão de ver câmeras e não-admin: vazio.
            if (!isAdmin)
            {
                var canView = await perms.HasAsync(uid, Permissions.CameraView);
                if (!canView) return Results.Ok(Array.Empty<object>());
            }

            return Results.Ok(maps);
        });

        // Detalhe com marcadores + status dos dispositivos.
        g.MapGet("/maps/{id:int}", async (
            int id, HttpContext ctx, PlatformDbContext db, PermissionService perms) =>
        {
            var tenant = ctx.User.TenantId();
            var map = await db.SynopticMaps.AsNoTracking()
                .Include(m => m.Markers)
                .FirstOrDefaultAsync(m => m.Id == id && m.TenantId == tenant);
            if (map is null) return Results.NotFound();

            var deviceIds = map.Markers.Where(x => x.DeviceId != null).Select(x => x.DeviceId!.Value).Distinct().ToList();
            var devices = await db.Devices.AsNoTracking()
                .Where(d => deviceIds.Contains(d.Id))
                .Select(d => new { d.Id, d.Name, d.Kind, d.Status, d.Host, d.LastSeen, d.Driver })
                .ToListAsync();
            var byId = devices.ToDictionary(d => d.Id);

            // Eventos recentes (15 min) para piscar alarme no mapa.
            var since = DateTime.UtcNow.AddMinutes(-15);
            var recentRaw = deviceIds.Count == 0
                ? new List<(int deviceId, string? lastType, DateTime lastAt, int count)>()
                : (await db.Events.AsNoTracking()
                    .Where(e => e.DeviceId != null
                        && deviceIds.Contains(e.DeviceId.Value)
                        && e.CreatedAt >= since)
                    .GroupBy(e => e.DeviceId!.Value)
                    .Select(g => new
                    {
                        deviceId = g.Key,
                        lastType = g.OrderByDescending(x => x.CreatedAt).Select(x => x.Type).FirstOrDefault(),
                        lastAt = g.Max(x => x.CreatedAt),
                        count = g.Count()
                    })
                    .ToListAsync())
                  .Select(x => (x.deviceId, x.lastType, x.lastAt, x.count))
                  .ToList();
            var evByDev = recentRaw.ToDictionary(x => x.deviceId);

            var markers = map.Markers.Select(m =>
            {
                object? dev = null;
                string status = "unknown";
                string? lastEvent = null;
                DateTime? lastEventAt = null;
                int eventCount = 0;
                if (m.DeviceId is int did && byId.TryGetValue(did, out var d))
                {
                    dev = d;
                    status = d.Status.ToString().ToLowerInvariant();
                    if (evByDev.TryGetValue(did, out var ev))
                    {
                        lastEvent = ev.lastType;
                        lastEventAt = ev.lastAt;
                        eventCount = ev.count;
                        if (status != "offline" && !string.IsNullOrEmpty(lastEvent)
                            && (lastEvent.Contains("motion", StringComparison.OrdinalIgnoreCase)
                                || lastEvent.Contains("alarm", StringComparison.OrdinalIgnoreCase)
                                || lastEvent.Contains("intrusion", StringComparison.OrdinalIgnoreCase)
                                || lastEvent.Contains("video_loss", StringComparison.OrdinalIgnoreCase)))
                            status = "alarm";
                    }
                }

                return new
                {
                    m.Id, m.MapId, m.DeviceId, m.Label, m.Kind, m.Icon,
                    m.X, m.Y, m.Z, m.Rotation, m.Color, m.MetaJson,
                    status,
                    lastEvent,
                    lastEventAt,
                    eventCount,
                    device = dev
                };
            }).ToList();

            return Results.Ok(new
            {
                map.Id, map.Name, map.Description, map.Mode,
                map.BackgroundUrl, map.BackgroundColor,
                map.Width, map.Height, map.PerspectiveDeg,
                map.SortOrder, map.Active, map.UpdatedAt,
                markers
            });
        });

        // CRUD — exige camera.config (admin de câmeras / planta).
        g.MapPost("/maps", async (
            MapWrite input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!await CanConfigMaps(ctx, db))
                return Results.Forbid();

            if (string.IsNullOrWhiteSpace(input.Name))
                return Results.BadRequest(new { error = "Nome obrigatório." });

            var map = new SynopticMap
            {
                TenantId = ctx.User.TenantId(),
                Name = input.Name.Trim(),
                Description = input.Description?.Trim() ?? "",
                Mode = NormalizeMode(input.Mode),
                BackgroundUrl = input.BackgroundUrl,
                BackgroundColor = string.IsNullOrWhiteSpace(input.BackgroundColor) ? "#0a0e14" : input.BackgroundColor!,
                Width = input.Width is > 100 and <= 8000 ? input.Width.Value : 1280,
                Height = input.Height is > 100 and <= 8000 ? input.Height.Value : 720,
                PerspectiveDeg = input.PerspectiveDeg is >= 20 and <= 80 ? input.PerspectiveDeg.Value : 55,
                SortOrder = input.SortOrder ?? 0,
                Active = input.Active ?? true
            };
            db.SynopticMaps.Add(map);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "map.create", "map", map.Id.ToString(), true);
            return Results.Created($"/api/vms/maps/{map.Id}", new { map.Id, map.Name });
        });

        g.MapPut("/maps/{id:int}", async (
            int id, MapWrite input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!await CanConfigMaps(ctx, db))
                return Results.Forbid();

            var map = await db.SynopticMaps.FirstOrDefaultAsync(m => m.Id == id && m.TenantId == ctx.User.TenantId());
            if (map is null) return Results.NotFound();

            if (!string.IsNullOrWhiteSpace(input.Name)) map.Name = input.Name.Trim();
            if (input.Description is not null) map.Description = input.Description.Trim();
            if (input.Mode is not null) map.Mode = NormalizeMode(input.Mode);
            if (input.BackgroundUrl is not null) map.BackgroundUrl = input.BackgroundUrl;
            if (input.BackgroundColor is not null) map.BackgroundColor = input.BackgroundColor;
            if (input.Width is > 100 and <= 8000) map.Width = input.Width.Value;
            if (input.Height is > 100 and <= 8000) map.Height = input.Height.Value;
            if (input.PerspectiveDeg is >= 20 and <= 80) map.PerspectiveDeg = input.PerspectiveDeg.Value;
            if (input.SortOrder is not null) map.SortOrder = input.SortOrder.Value;
            if (input.Active is not null) map.Active = input.Active.Value;
            map.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "map.update", "map", id.ToString(), true);
            return Results.Ok(new { map.Id, map.Name, map.UpdatedAt });
        });

        g.MapDelete("/maps/{id:int}", async (
            int id, HttpContext ctx, PlatformDbContext db, AuditService audit, IHostEnvironment env) =>
        {
            if (!await CanConfigMaps(ctx, db))
                return Results.Forbid();

            var map = await db.SynopticMaps.Include(m => m.Markers)
                .FirstOrDefaultAsync(m => m.Id == id && m.TenantId == ctx.User.TenantId());
            if (map is null) return Results.NotFound();

            // Remove fundo se for arquivo local.
            TryDeleteBackground(env, map.BackgroundUrl);

            db.SynopticMaps.Remove(map);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "map.delete", "map", id.ToString(), true);
            return Results.NoContent();
        });

        // Upload de planta: PNG/JPEG/WebP/GIF/SVG ou DWG/DXF (convertido p/ SVG).
        g.MapPost("/maps/{id:int}/background", async (
            int id, HttpRequest req, HttpContext ctx, PlatformDbContext db,
            AuditService audit, IHostEnvironment env,
            Microsoft.Extensions.Logging.ILoggerFactory logFactory) =>
        {
            if (!await CanConfigMaps(ctx, db))
                return Results.Forbid();

            var map = await db.SynopticMaps.FirstOrDefaultAsync(m => m.Id == id && m.TenantId == ctx.User.TenantId());
            if (map is null) return Results.NotFound();

            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "Envie multipart/form-data com campo 'file'." });

            var form = await req.ReadFormAsync();
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "Arquivo ausente." });
            if (file.Length > 40 * 1024 * 1024)
                return Results.BadRequest(new { error = "Arquivo maior que 40 MB." });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            var raster = ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".svg";
            var cad = CadPlanConverter.IsCadExtension(ext);
            if (!raster && !cad)
                return Results.BadRequest(new { error = "Use PNG, JPEG, WebP, GIF, SVG, DWG ou DXF." });

            var dir = Path.Combine(env.ContentRootPath, "wwwroot", "maps", "bg");
            Directory.CreateDirectory(dir);
            var guid = Guid.NewGuid().ToString("N");
            string displayUrl;
            string? sourceUrl = null;
            int? newW = null, newH = null;

            if (cad)
            {
                var srcName = $"map{id}_{guid}_source{ext}";
                var srcFull = Path.Combine(dir, srcName);
                await using (var fs = File.Create(srcFull))
                    await file.CopyToAsync(fs);

                var svgName = $"map{id}_{guid}.svg";
                var svgFull = Path.Combine(dir, svgName);
                try
                {
                    var log = logFactory.CreateLogger("CadPlanConverter");
                    var (w, h) = CadPlanConverter.ConvertToSvg(srcFull, svgFull, log);
                    newW = w; newH = h;
                    displayUrl = $"/maps/bg/{svgName}";
                    sourceUrl = $"/maps/bg/{srcName}";
                }
                catch (Exception ex)
                {
                    try { File.Delete(srcFull); } catch { /* */ }
                    try { if (File.Exists(svgFull)) File.Delete(svgFull); } catch { /* */ }
                    return Results.BadRequest(new
                    {
                        error = "Falha ao converter DWG/DXF: " + ex.Message,
                        hint = "Exporte a planta como DXF R2000+ ou PNG e tente de novo."
                    });
                }
            }
            else
            {
                var name = $"map{id}_{guid}{ext}";
                var full = Path.Combine(dir, name);
                await using (var fs = File.Create(full))
                    await file.CopyToAsync(fs);
                displayUrl = $"/maps/bg/{name}";
            }

            TryDeleteBackground(env, map.BackgroundUrl);
            map.BackgroundUrl = displayUrl;
            if (newW is > 100) map.Width = newW.Value;
            if (newH is > 100) map.Height = newH.Value;
            map.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "map.background", "map", id.ToString(), true,
                detail: $"{displayUrl}" + (sourceUrl is null ? "" : $" source={sourceUrl}"));

            return Results.Ok(new
            {
                map.BackgroundUrl,
                sourceUrl,
                map.Width,
                map.Height,
                format = cad ? ext.TrimStart('.') : ext.TrimStart('.'),
                converted = cad
            });
        });

        // Marcadores — se a câmera já estiver no mapa, reposiciona (fixar).
        g.MapPost("/maps/{id:int}/markers", async (
            int id, MarkerWrite input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!await CanConfigMaps(ctx, db))
                return Results.Forbid();

            var map = await db.SynopticMaps.FirstOrDefaultAsync(m => m.Id == id && m.TenantId == ctx.User.TenantId());
            if (map is null) return Results.NotFound();

            if (input.DeviceId is int did)
            {
                var exists = await db.Devices.AnyAsync(d => d.Id == did);
                if (!exists) return Results.BadRequest(new { error = "Dispositivo inexistente." });

                var existing = await db.MapMarkers.FirstOrDefaultAsync(x => x.MapId == id && x.DeviceId == did);
                if (existing is not null)
                {
                    if (input.X is not null) existing.X = Clamp(input.X.Value, 0, 100);
                    if (input.Y is not null) existing.Y = Clamp(input.Y.Value, 0, 100);
                    if (input.Z is not null) existing.Z = Clamp(input.Z.Value, 0, 100);
                    if (!string.IsNullOrWhiteSpace(input.Label)) existing.Label = input.Label.Trim();
                    existing.UpdatedAt = DateTime.UtcNow;
                    map.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return Results.Ok(new { existing.Id, fixedExisting = true, existing.X, existing.Y });
                }
            }

            var m = new MapMarker
            {
                MapId = id,
                DeviceId = input.DeviceId,
                Label = input.Label?.Trim() ?? "",
                Kind = string.IsNullOrWhiteSpace(input.Kind) ? "camera" : input.Kind!.Trim(),
                Icon = string.IsNullOrWhiteSpace(input.Icon) ? "camera" : input.Icon!.Trim(),
                X = Clamp(input.X ?? 50, 0, 100),
                Y = Clamp(input.Y ?? 50, 0, 100),
                Z = Clamp(input.Z ?? 0, 0, 100),
                Rotation = input.Rotation ?? 0,
                Color = input.Color,
                MetaJson = string.IsNullOrWhiteSpace(input.MetaJson) ? "{}" : input.MetaJson!
            };
            db.MapMarkers.Add(m);
            map.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "map.marker.add", "map", id.ToString(), true, detail: m.Id.ToString());
            return Results.Created($"/api/vms/maps/{id}/markers/{m.Id}", new { m.Id, fixedExisting = false });
        });

        g.MapPut("/maps/{mapId:int}/markers/{markerId:int}", async (
            int mapId, int markerId, MarkerWrite input, HttpContext ctx,
            PlatformDbContext db, AuditService audit) =>
        {
            if (!await CanConfigMaps(ctx, db))
                return Results.Forbid();

            var m = await db.MapMarkers
                .Include(x => x.Map)
                .FirstOrDefaultAsync(x => x.Id == markerId && x.MapId == mapId
                    && x.Map != null && x.Map.TenantId == ctx.User.TenantId());
            if (m is null) return Results.NotFound();

            if (input.DeviceId is not null)
            {
                if (input.DeviceId == 0) m.DeviceId = null;
                else
                {
                    var exists = await db.Devices.AnyAsync(d => d.Id == input.DeviceId);
                    if (!exists) return Results.BadRequest(new { error = "Dispositivo inexistente." });
                    m.DeviceId = input.DeviceId;
                }
            }
            if (input.Label is not null) m.Label = input.Label.Trim();
            if (input.Kind is not null) m.Kind = input.Kind.Trim();
            if (input.Icon is not null) m.Icon = input.Icon.Trim();
            if (input.X is not null) m.X = Clamp(input.X.Value, 0, 100);
            if (input.Y is not null) m.Y = Clamp(input.Y.Value, 0, 100);
            if (input.Z is not null) m.Z = Clamp(input.Z.Value, 0, 100);
            if (input.Rotation is not null) m.Rotation = input.Rotation.Value;
            if (input.Color is not null) m.Color = input.Color;
            if (input.MetaJson is not null) m.MetaJson = input.MetaJson;
            m.UpdatedAt = DateTime.UtcNow;
            if (m.Map is not null) m.Map.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "map.marker.update", "map", mapId.ToString(), true, detail: markerId.ToString());
            return Results.Ok(new { m.Id, m.X, m.Y, m.Z });
        });

        g.MapDelete("/maps/{mapId:int}/markers/{markerId:int}", async (
            int mapId, int markerId, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!await CanConfigMaps(ctx, db))
                return Results.Forbid();

            var m = await db.MapMarkers
                .Include(x => x.Map)
                .FirstOrDefaultAsync(x => x.Id == markerId && x.MapId == mapId
                    && x.Map != null && x.Map.TenantId == ctx.User.TenantId());
            if (m is null) return Results.NotFound();

            db.MapMarkers.Remove(m);
            if (m.Map is not null) m.Map.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "map.marker.delete", "map", mapId.ToString(), true, detail: markerId.ToString());
            return Results.NoContent();
        });
    }

    private static async Task<bool> CanConfigMaps(HttpContext ctx, PlatformDbContext db)
    {
        if (ctx.User.IsInRole("admin"))
            return true;
        var perms = ctx.RequestServices.GetService(typeof(PermissionService)) as PermissionService;
        if (perms is null) return false;
        var uid = ctx.User.UserId();
        return await perms.HasAsync(uid, Permissions.CameraConfig)
            || await perms.HasAsync(uid, Permissions.SystemConfig);
    }

    private static string NormalizeMode(string? mode)
        => string.Equals(mode, "3d", StringComparison.OrdinalIgnoreCase) ? "3d" : "2d";

    private static double Clamp(double v, double min, double max)
        => v < min ? min : v > max ? max : v;

    private static void TryDeleteBackground(IHostEnvironment env, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("/maps/bg/", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            var name = Path.GetFileName(url);
            if (string.IsNullOrEmpty(name)) return;
            var full = Path.Combine(env.ContentRootPath, "wwwroot", "maps", "bg", name);
            if (File.Exists(full)) File.Delete(full);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public record MapWrite(
    string? Name,
    string? Description,
    string? Mode,
    string? BackgroundUrl,
    string? BackgroundColor,
    int? Width,
    int? Height,
    double? PerspectiveDeg,
    int? SortOrder,
    bool? Active);

public record MarkerWrite(
    int? DeviceId,
    string? Label,
    string? Kind,
    string? Icon,
    double? X,
    double? Y,
    double? Z,
    double? Rotation,
    string? Color,
    string? MetaJson);
