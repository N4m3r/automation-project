using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Modules.Vms;

public static class AlarmEndpoints
{
    public static void MapAlarms(this RouteGroupBuilder g)
    {
        g.MapGet("/alarms/panels", async (HttpContext ctx, PlatformDbContext db) =>
            await db.AlarmPanels.AsNoTracking()
                .Where(p => p.TenantId == ctx.User.TenantId())
                .OrderBy(p => p.Name)
                .ToListAsync());

        g.MapPost("/alarms/panels", async (PanelInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!ctx.User.IsInRole("admin")) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Account))
                return Results.BadRequest(new { error = "Nome e conta SIA obrigatórios." });
            var p = new AlarmPanel
            {
                TenantId = ctx.User.TenantId(),
                Name = input.Name.Trim(),
                Account = input.Account.Trim(),
                Protocol = input.Protocol ?? "SIA-DC09",
                Active = true
            };
            db.AlarmPanels.Add(p);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "alarm.panel.create", "alarm", p.Id.ToString(), true);
            return Results.Created($"/api/vms/alarms/panels/{p.Id}", new { p.Id });
        });

        g.MapGet("/alarms/zones", async (HttpContext ctx, PlatformDbContext db, int? panelId) =>
        {
            var tenant = ctx.User.TenantId();
            var panelIds = await db.AlarmPanels.AsNoTracking()
                .Where(p => p.TenantId == tenant).Select(p => p.Id).ToListAsync();
            var q = db.AlarmZones.AsNoTracking().Where(z => panelIds.Contains(z.PanelId));
            if (panelId is not null) q = q.Where(z => z.PanelId == panelId);
            return await q.OrderBy(z => z.ZoneCode).ToListAsync();
        });

        g.MapPost("/alarms/zones", async (ZoneInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!ctx.User.IsInRole("admin")) return Results.Forbid();
            var panel = await db.AlarmPanels.FirstOrDefaultAsync(p => p.Id == input.PanelId && p.TenantId == ctx.User.TenantId());
            if (panel is null) return Results.BadRequest(new { error = "Painel inválido." });
            var z = new AlarmZone
            {
                PanelId = panel.Id,
                ZoneCode = input.ZoneCode?.Trim() ?? "",
                Name = input.Name?.Trim() ?? "",
                CameraId = input.CameraId,
                MapId = input.MapId,
                Notes = input.Notes?.Trim() ?? ""
            };
            db.AlarmZones.Add(z);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "alarm.zone.create", "alarm", z.Id.ToString(), true);
            return Results.Ok(new { z.Id });
        });

        g.MapGet("/alarms/events", async (
            HttpContext ctx, PlatformDbContext db, int take = 100, bool? unacked = null, string? status = null) =>
        {
            var q = db.AlarmEvents.AsNoTracking().Where(e => e.TenantId == ctx.User.TenantId());
            if (unacked == true) q = q.Where(e => !e.Acknowledged);
            if (!string.IsNullOrWhiteSpace(status))
                q = q.Where(e => e.Status == status);
            return await q.OrderByDescending(e => e.CreatedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync();
        });

        // Fila de tratamento (abertos + em tratamento, por severidade).
        g.MapGet("/alarms/queue", async (HttpContext ctx, PlatformDbContext db, int take = 50) =>
        {
            var tenant = ctx.User.TenantId();
            var list = await db.AlarmEvents.AsNoTracking()
                .Where(e => e.TenantId == tenant && e.Status != "resolved")
                .OrderByDescending(e => e.Severity)
                .ThenBy(e => e.CreatedAt)
                .Take(Math.Clamp(take, 1, 200))
                .ToListAsync();

            var codes = list.Select(e => e.Code).Distinct().ToList();
            var templates = await db.AlarmPopTemplates.AsNoTracking()
                .Where(t => t.TenantId == tenant && t.Active)
                .ToListAsync();

            return list.Select(e =>
            {
                var pop = templates
                    .Where(t => t.CodePrefix == "*" || e.Code.StartsWith(t.CodePrefix, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(t => t.CodePrefix.Length)
                    .FirstOrDefault();
                return new
                {
                    e.Id, e.Account, e.Code, e.Zone, e.Severity, e.Status,
                    e.Acknowledged, e.AssignedUserId, e.TreatmentNotes, e.PopProgressJson,
                    e.CreatedAt, e.ResolvedAt, e.Raw,
                    pop = pop is null ? null : new
                    {
                        pop.Id, pop.Title, pop.CodePrefix,
                        steps = ParseSteps(pop.StepsJson)
                    }
                };
            });
        });

        g.MapPost("/alarms/events/{id:long}/ack", async (long id, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            var e = await db.AlarmEvents.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == ctx.User.TenantId());
            if (e is null) return Results.NotFound();
            e.Acknowledged = true;
            if (e.Status == "open") e.Status = "treating";
            if (e.AssignedUserId is null) e.AssignedUserId = ctx.User.UserId();
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "alarm.ack", "alarm", id.ToString(), true);
            return Results.NoContent();
        });

        g.MapPost("/alarms/events/{id:long}/assign", async (
            long id, AssignInput? input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            var e = await db.AlarmEvents.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == ctx.User.TenantId());
            if (e is null) return Results.NotFound();
            e.AssignedUserId = input?.UserId ?? ctx.User.UserId();
            e.Acknowledged = true;
            if (e.Status == "open") e.Status = "treating";
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "alarm.assign", "alarm", id.ToString(), true,
                detail: e.AssignedUserId?.ToString() ?? "");
            return Results.Ok(new { e.Id, e.AssignedUserId, e.Status });
        });

        g.MapPost("/alarms/events/{id:long}/pop-step", async (
            long id, PopStepInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            var e = await db.AlarmEvents.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == ctx.User.TenantId());
            if (e is null) return Results.NotFound();
            var done = ParseIntList(e.PopProgressJson);
            if (!done.Contains(input.StepIndex))
                done.Add(input.StepIndex);
            done.Sort();
            e.PopProgressJson = JsonSerializer.Serialize(done);
            e.Acknowledged = true;
            if (e.Status == "open") e.Status = "treating";
            if (e.AssignedUserId is null) e.AssignedUserId = ctx.User.UserId();
            if (!string.IsNullOrWhiteSpace(input.Note))
                e.TreatmentNotes = (e.TreatmentNotes + "\n" + input.Note).Trim();
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "alarm.pop.step", "alarm", id.ToString(), true,
                detail: input.StepIndex.ToString());
            return Results.Ok(new { e.Id, progress = done, e.Status });
        });

        g.MapPost("/alarms/events/{id:long}/resolve", async (
            long id, ResolveInput? input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            var e = await db.AlarmEvents.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == ctx.User.TenantId());
            if (e is null) return Results.NotFound();
            e.Status = "resolved";
            e.Acknowledged = true;
            e.ResolvedAt = DateTime.UtcNow;
            if (e.AssignedUserId is null) e.AssignedUserId = ctx.User.UserId();
            if (!string.IsNullOrWhiteSpace(input?.Notes))
                e.TreatmentNotes = (e.TreatmentNotes + "\n" + input!.Notes).Trim();
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "alarm.resolve", "alarm", id.ToString(), true);
            return Results.Ok(new { e.Id, e.Status, e.ResolvedAt });
        });

        // POP templates
        g.MapGet("/alarms/pop", async (HttpContext ctx, PlatformDbContext db) =>
        {
            var list = await db.AlarmPopTemplates.AsNoTracking()
                .Where(t => t.TenantId == ctx.User.TenantId())
                .OrderBy(t => t.CodePrefix)
                .ToListAsync();
            return list.Select(t => new
            {
                t.Id, t.CodePrefix, t.Title, t.StepsJson, t.Active,
                steps = ParseSteps(t.StepsJson)
            });
        });

        g.MapPost("/alarms/pop", async (PopTemplateInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!ctx.User.IsInRole("admin")) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(input.Title))
                return Results.BadRequest(new { error = "Título obrigatório." });
            var steps = input.Steps ?? [];
            var t = new AlarmPopTemplate
            {
                TenantId = ctx.User.TenantId(),
                CodePrefix = string.IsNullOrWhiteSpace(input.CodePrefix) ? "*" : input.CodePrefix!.Trim().ToUpperInvariant(),
                Title = input.Title.Trim(),
                StepsJson = JsonSerializer.Serialize(steps),
                Active = true
            };
            db.AlarmPopTemplates.Add(t);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "alarm.pop.create", "alarm", t.Id.ToString(), true);
            return Results.Created($"/api/vms/alarms/pop/{t.Id}", new { t.Id });
        });

        g.MapDelete("/alarms/pop/{id:int}", async (int id, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!ctx.User.IsInRole("admin")) return Results.Forbid();
            var t = await db.AlarmPopTemplates.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == ctx.User.TenantId());
            if (t is null) return Results.NotFound();
            db.AlarmPopTemplates.Remove(t);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "alarm.pop.delete", "alarm", id.ToString(), true);
            return Results.NoContent();
        });

        g.MapPost("/alarms/ingest", async (SiaIngest input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!ctx.User.IsInRole("admin")) return Results.Forbid();
            var ev = await SiaReceiverService.PersistAsync(db, input.Account ?? "", input.Code ?? "",
                input.Zone ?? "", input.Raw ?? "", ctx.User.TenantId());
            await audit.WriteAsync(ctx, "alarm.ingest", "alarm", ev.Id.ToString(), true);
            return Results.Ok(ev);
        });
    }

    private static List<string> ParseSteps(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch { return []; }
    }

    private static List<int> ParseIntList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<int>>(json) ?? [];
        }
        catch { return []; }
    }
}

public record PanelInput(string? Name, string? Account, string? Protocol);
public record ZoneInput(int PanelId, string? ZoneCode, string? Name, int? CameraId, int? MapId, string? Notes);
public record SiaIngest(string? Account, string? Code, string? Zone, string? Raw);
public record AssignInput(int? UserId);
public record PopStepInput(int StepIndex, string? Note);
public record ResolveInput(string? Notes);
public record PopTemplateInput(string? CodePrefix, string? Title, string[]? Steps);
