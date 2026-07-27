using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Modules.Vms;

/// <summary>Analítico embarcado: eventos tipados + listas LPR.</summary>
public static class AnalyticsEndpoints
{
    public static void MapAnalytics(this RouteGroupBuilder g)
    {
        // Resumo por tipo (dashboard Analítico).
        g.MapGet("/analytics/summary", async (
            HttpContext ctx, PlatformDbContext db, PermissionService perms,
            DateTime? from, DateTime? to) =>
        {
            var visible = await perms.VisibleCameraIdsAsync(ctx.User.UserId());
            var tenant = ctx.User.TenantId();
            var inicio = from ?? DateTime.UtcNow.AddDays(-1);
            var fim = to ?? DateTime.UtcNow;

            var q = db.Events.AsNoTracking()
                .Where(e => e.TenantId == tenant && e.CreatedAt >= inicio && e.CreatedAt <= fim)
                .Where(e => e.DeviceId == null || visible.Contains(e.DeviceId.Value));

            var byType = await q.GroupBy(e => e.Type)
                .Select(g => new { type = g.Key, count = g.Count(), last = g.Max(x => x.CreatedAt) })
                .OrderByDescending(x => x.count)
                .Take(50)
                .ToListAsync();

            var lpr = await q.Where(e => e.Type == "lpr_detected" || e.Type == "anpr")
                .OrderByDescending(e => e.CreatedAt).Take(20).ToListAsync();

            return Results.Ok(new
            {
                from = inicio, to = fim,
                total = byType.Sum(x => x.count),
                byType,
                recentLpr = lpr.Select(e => new
                {
                    e.Id, e.DeviceId, e.Type, e.Severity, e.CreatedAt, e.Acknowledged,
                    meta = EventMetadata.TryParseFromPayload(e.Payload)
                })
            });
        });

        // Eventos com metadados parseados (filtros analíticos).
        g.MapGet("/analytics/events", async (
            HttpContext ctx, PlatformDbContext db, PermissionService perms,
            string? type, string? plate, int? deviceId, DateTime? from, int take = 100) =>
        {
            var visible = await perms.VisibleCameraIdsAsync(ctx.User.UserId());
            var tenant = ctx.User.TenantId();
            var inicio = from ?? DateTime.UtcNow.AddHours(-24);
            take = Math.Clamp(take, 1, 500);

            var q = db.Events.AsNoTracking()
                .Where(e => e.TenantId == tenant && e.CreatedAt >= inicio)
                .Where(e => e.DeviceId == null || visible.Contains(e.DeviceId.Value));
            if (deviceId is not null) q = q.Where(e => e.DeviceId == deviceId);
            if (!string.IsNullOrWhiteSpace(type)) q = q.Where(e => e.Type.Contains(type));

            var list = await q.OrderByDescending(e => e.CreatedAt).Take(take).ToListAsync();
            var plateN = EventMetadata.NormalizePlate(plate);
            var rows = list.Select(e =>
            {
                var meta = EventMetadata.TryParseFromPayload(e.Payload);
                return new
                {
                    e.Id, e.DeviceId, e.Type, e.Severity, e.Acknowledged, e.CreatedAt,
                    e.Payload,
                    meta
                };
            });
            if (!string.IsNullOrEmpty(plateN))
                rows = rows.Where(r => r.meta?.Plate == plateN);
            return Results.Ok(rows.ToList());
        });

        // ---- LPR rules ----
        g.MapGet("/lpr/plates", async (HttpContext ctx, PlatformDbContext db) =>
        {
            var tenant = ctx.User.TenantId();
            return await db.LicensePlateRules.AsNoTracking()
                .Where(r => r.TenantId == tenant)
                .OrderBy(r => r.ListType).ThenBy(r => r.Plate)
                .ToListAsync();
        });

        g.MapPost("/lpr/plates", async (
            PlateRuleInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!ctx.User.IsInRole("admin")
                && !await HasConfig(ctx, db))
                return Results.Forbid();

            var plate = EventMetadata.NormalizePlate(input.Plate);
            if (plate.Length < 3)
                return Results.BadRequest(new { error = "Placa inválida." });
            var listType = (input.ListType ?? "watch").ToLowerInvariant();
            if (listType is not ("allow" or "deny" or "watch"))
                listType = "watch";

            var r = new LicensePlateRule
            {
                TenantId = ctx.User.TenantId(),
                Plate = plate,
                ListType = listType,
                OwnerName = input.OwnerName?.Trim() ?? "",
                Notes = input.Notes?.Trim() ?? "",
                Active = input.Active ?? true,
                ExpiresAt = input.ExpiresAt
            };
            db.LicensePlateRules.Add(r);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "lpr.plate.add", "lpr", r.Id.ToString(), true, detail: plate);
            return Results.Created($"/api/vms/lpr/plates/{r.Id}", r);
        });

        g.MapPut("/lpr/plates/{id:int}", async (
            int id, PlateRuleInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!ctx.User.IsInRole("admin") && !await HasConfig(ctx, db))
                return Results.Forbid();
            var r = await db.LicensePlateRules.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == ctx.User.TenantId());
            if (r is null) return Results.NotFound();
            if (!string.IsNullOrWhiteSpace(input.Plate))
                r.Plate = EventMetadata.NormalizePlate(input.Plate);
            if (!string.IsNullOrWhiteSpace(input.ListType))
                r.ListType = input.ListType.ToLowerInvariant();
            if (input.OwnerName is not null) r.OwnerName = input.OwnerName.Trim();
            if (input.Notes is not null) r.Notes = input.Notes.Trim();
            if (input.Active is not null) r.Active = input.Active.Value;
            if (input.ExpiresAt is not null) r.ExpiresAt = input.ExpiresAt;
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "lpr.plate.update", "lpr", id.ToString(), true);
            return Results.Ok(r);
        });

        g.MapDelete("/lpr/plates/{id:int}", async (
            int id, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!ctx.User.IsInRole("admin") && !await HasConfig(ctx, db))
                return Results.Forbid();
            var r = await db.LicensePlateRules.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == ctx.User.TenantId());
            if (r is null) return Results.NotFound();
            db.LicensePlateRules.Remove(r);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "lpr.plate.delete", "lpr", id.ToString(), true);
            return Results.NoContent();
        });
    }

    private static async Task<bool> HasConfig(HttpContext ctx, PlatformDbContext db)
    {
        var perms = ctx.RequestServices.GetService(typeof(PermissionService)) as PermissionService;
        if (perms is null) return false;
        return await perms.HasAsync(ctx.User.UserId(), Permissions.CameraConfig)
            || await perms.HasAsync(ctx.User.UserId(), Permissions.SystemConfig);
    }
}

public record PlateRuleInput(
    string? Plate,
    string? ListType,
    string? OwnerName,
    string? Notes,
    bool? Active,
    DateTime? ExpiresAt);
