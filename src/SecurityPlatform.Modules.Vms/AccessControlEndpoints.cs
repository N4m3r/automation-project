using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Controle de acesso: pessoas, credenciais, portas, anti-passback, eclusa, visitantes.
/// </summary>
public static class AccessControlEndpoints
{
    public static void MapAccessControl(this RouteGroupBuilder g)
    {
        // ---- Pessoas ----
        g.MapGet("/access/people", async (HttpContext ctx, PlatformDbContext db) =>
        {
            var people = await db.AccessPeople.AsNoTracking()
                .Where(p => p.TenantId == ctx.User.TenantId())
                .Include(p => p.Credentials)
                .OrderBy(p => p.FullName)
                .ToListAsync();
            return people.Select(p => new
            {
                p.Id, p.FullName, p.Document, p.Company, p.Email, p.Phone, p.ScheduleId, p.Active, p.CreatedAt,
                credentials = p.Credentials.Select(c => new { c.Id, c.Kind, c.Value, c.Active, c.ValidFrom, c.ValidTo })
            });
        });

        g.MapPost("/access/people", async (PersonInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!await CanManage(ctx)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(input.FullName))
                return Results.BadRequest(new { error = "Nome obrigatório." });
            var p = new AccessPerson
            {
                TenantId = ctx.User.TenantId(),
                FullName = input.FullName.Trim(),
                Document = input.Document?.Trim() ?? "",
                Company = input.Company?.Trim() ?? "",
                Email = input.Email?.Trim() ?? "",
                Phone = input.Phone?.Trim() ?? "",
                ScheduleId = input.ScheduleId is > 0 ? input.ScheduleId : null,
                Active = input.Active ?? true
            };
            db.AccessPeople.Add(p);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "access.person.create", "person", p.Id.ToString(), true);
            return Results.Created($"/api/vms/access/people/{p.Id}", new { p.Id });
        });

        g.MapPost("/access/people/{id:int}/credentials", async (
            int id, CredInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!await CanManage(ctx)) return Results.Forbid();
            var p = await db.AccessPeople.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == ctx.User.TenantId());
            if (p is null) return Results.NotFound();
            var val = (input.Value ?? "").Trim();
            if (val.Length < 1) return Results.BadRequest(new { error = "Valor da credencial obrigatório." });
            var kind = string.IsNullOrWhiteSpace(input.Kind) ? "card" : input.Kind!.Trim().ToLowerInvariant();
            var c = new AccessCredential
            {
                PersonId = id,
                Kind = kind,
                Value = kind == "plate" ? EventMetadata.NormalizePlate(val) : val,
                Active = true,
                ValidFrom = input.ValidFrom,
                ValidTo = input.ValidTo
            };
            db.AccessCredentials.Add(c);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "access.credential.add", "person", id.ToString(), true);
            return Results.Ok(new { c.Id });
        });

        g.MapDelete("/access/people/{id:int}", async (int id, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!await CanManage(ctx)) return Results.Forbid();
            var p = await db.AccessPeople.Include(x => x.Credentials)
                .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == ctx.User.TenantId());
            if (p is null) return Results.NotFound();
            var presence = await db.AccessPresences.Where(x => x.PersonId == id).ToListAsync();
            db.AccessPresences.RemoveRange(presence);
            db.AccessPeople.Remove(p);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "access.person.delete", "person", id.ToString(), true);
            return Results.NoContent();
        });

        // ---- Portas ----
        g.MapGet("/access/doors", async (HttpContext ctx, PlatformDbContext db) =>
            await db.AccessDoors.AsNoTracking()
                .Where(d => d.TenantId == ctx.User.TenantId())
                .OrderBy(d => d.Name)
                .ToListAsync());

        g.MapPost("/access/doors", async (DoorInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!await CanManage(ctx)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(input.Name))
                return Results.BadRequest(new { error = "Nome obrigatório." });
            var d = ApplyDoor(new AccessDoor { TenantId = ctx.User.TenantId(), Active = true }, input);
            db.AccessDoors.Add(d);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "access.door.create", "door", d.Id.ToString(), true);
            return Results.Created($"/api/vms/access/doors/{d.Id}", new { d.Id });
        });

        g.MapPut("/access/doors/{id:int}", async (
            int id, DoorInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!await CanManage(ctx)) return Results.Forbid();
            var d = await db.AccessDoors.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == ctx.User.TenantId());
            if (d is null) return Results.NotFound();
            ApplyDoor(d, input);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "access.door.update", "door", id.ToString(), true);
            return Results.Ok(d);
        });

        g.MapPost("/access/doors/{id:int}/close", async (
            int id, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!await CanManage(ctx) && !await HasPtzOrConfig(ctx)) return Results.Forbid();
            var door = await db.AccessDoors.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == ctx.User.TenantId());
            if (door is null) return Results.NotFound();
            door.IsOpen = false;
            door.OpenUntil = null;
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "access.door.close", "door", id.ToString(), true);
            return Results.Ok(new { ok = true, door.IsOpen });
        });

        // Destrava com anti-passback + eclusa.
        g.MapPost("/access/doors/{id:int}/unlock", async (
            int id, UnlockInput? input, HttpContext ctx, PlatformDbContext db,
            DriverRegistry registry, AuditService audit) =>
        {
            var tenant = ctx.User.TenantId();
            var door = await db.AccessDoors.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenant);
            if (door is null) return Results.NotFound();
            if (!door.Active) return Results.BadRequest(new { error = "Porta inativa." });

            // Expira estado aberto se OpenUntil passou.
            if (door.IsOpen && door.OpenUntil is DateTime until && until < DateTime.UtcNow)
            {
                door.IsOpen = false;
                door.OpenUntil = null;
            }

            AccessPerson? person = null;
            AccessVisitor? visitor = null;
            var result = "granted";
            var reason = "remote";
            var zoneAfter = door.ZoneTo;

            if (!string.IsNullOrWhiteSpace(input?.Credential))
            {
                var val = input!.Credential!.Trim();
                var plateVal = EventMetadata.NormalizePlate(val);

                var cred = await db.AccessCredentials.Include(c => c.Person)
                    .FirstOrDefaultAsync(c => c.Active
                        && (c.Value == val || (c.Kind == "plate" && c.Value == plateVal))
                        && c.Person != null && c.Person.TenantId == tenant && c.Person.Active);

                if (cred is null)
                {
                    // Visitante temporário
                    visitor = await db.AccessVisitors.FirstOrDefaultAsync(v =>
                        v.TenantId == tenant && v.Active
                        && v.CredentialValue == val
                        && v.ValidFrom <= DateTime.UtcNow
                        && v.ValidTo >= DateTime.UtcNow);
                    if (visitor is null)
                    {
                        result = "denied";
                        reason = "credential_unknown";
                    }
                    else
                    {
                        reason = "visitor_ok";
                    }
                }
                else if (cred.ValidFrom is DateTime vf && vf > DateTime.UtcNow)
                {
                    result = "denied";
                    reason = "not_yet_valid";
                }
                else if (cred.ValidTo is DateTime vt && vt < DateTime.UtcNow)
                {
                    result = "denied";
                    reason = "expired";
                }
                else
                {
                    person = cred.Person;
                    reason = "credential_ok";
                }
            }
            else if (!ctx.User.IsInRole("admin") && !await HasPtzOrConfig(ctx))
            {
                return Results.Forbid();
            }

            // Horários de acesso (porta e/ou pessoa)
            if (result == "granted")
            {
                if (door.ScheduleId is int doorSchId)
                {
                    var sch = await db.AccessSchedules.AsNoTracking()
                        .FirstOrDefaultAsync(s => s.Id == doorSchId && s.TenantId == tenant && s.Active);
                    if (!AccessScheduleEvaluator.IsOpenNow(sch))
                    {
                        result = "denied";
                        reason = "outside_schedule_door";
                    }
                }
                if (result == "granted" && person?.ScheduleId is int personSchId)
                {
                    var sch = await db.AccessSchedules.AsNoTracking()
                        .FirstOrDefaultAsync(s => s.Id == personSchId && s.TenantId == tenant && s.Active);
                    if (!AccessScheduleEvaluator.IsOpenNow(sch))
                    {
                        result = "denied";
                        reason = "outside_schedule_person";
                    }
                }
            }

            // Eclusa / intertravamento
            if (result == "granted" && door.InterlockWithDoorId is int otherId && door.InterlockRequireClosed)
            {
                var other = await db.AccessDoors.FirstOrDefaultAsync(d => d.Id == otherId && d.TenantId == tenant);
                if (other is not null)
                {
                    if (other.IsOpen && other.OpenUntil is DateTime ou && ou < DateTime.UtcNow)
                    {
                        other.IsOpen = false;
                        other.OpenUntil = null;
                    }
                    if (other.IsOpen)
                    {
                        result = "denied";
                        reason = "interlock_open";
                    }
                }
            }

            // Anti-passback por zona: pessoa deve estar em ZoneFrom (ou ZoneTo se Direction=both → volta).
            if (result == "granted" && door.AntiPassback && person is not null)
            {
                var presence = await db.AccessPresences
                    .FirstOrDefaultAsync(p => p.TenantId == tenant && p.PersonId == person.Id);
                var current = presence?.CurrentZone ?? "outside";
                if (string.Equals(current, door.ZoneFrom, StringComparison.OrdinalIgnoreCase))
                {
                    zoneAfter = door.ZoneTo;
                }
                else if (string.Equals(door.Direction, "both", StringComparison.OrdinalIgnoreCase)
                         && string.Equals(current, door.ZoneTo, StringComparison.OrdinalIgnoreCase))
                {
                    zoneAfter = door.ZoneFrom;
                }
                else
                {
                    result = "denied";
                    reason = "antipassback";
                    zoneAfter = current;
                }
            }

            db.AccessLogs.Add(new AccessLog
            {
                TenantId = tenant,
                DoorId = door.Id,
                PersonId = person?.Id,
                CredentialValue = input?.Credential ?? (visitor is not null ? visitor.CredentialValue : ""),
                Result = result,
                Reason = reason,
                ZoneAfter = result == "granted" ? zoneAfter : ""
            });

            if (result == "granted")
            {
                door.IsOpen = true;
                door.OpenUntil = DateTime.UtcNow.AddSeconds(door.UnlockSeconds);

                if (person is not null && door.AntiPassback)
                {
                    var presence = await db.AccessPresences
                        .FirstOrDefaultAsync(p => p.TenantId == tenant && p.PersonId == person.Id);
                    if (presence is null)
                    {
                        presence = new AccessPresence
                        {
                            TenantId = tenant,
                            PersonId = person.Id,
                            CurrentZone = zoneAfter,
                            LastDoorId = door.Id,
                            UpdatedAt = DateTime.UtcNow
                        };
                        db.AccessPresences.Add(presence);
                    }
                    else
                    {
                        presence.CurrentZone = zoneAfter;
                        presence.LastDoorId = door.Id;
                        presence.UpdatedAt = DateTime.UtcNow;
                    }
                }

                if (door.DeviceId is int devId)
                {
                    var dev = await db.Devices.FindAsync(devId);
                    if (dev is not null)
                    {
                        try
                        {
                            await registry.Resolve(dev).CommandAsync(dev, door.RelayAction, new Dictionary<string, string>
                            {
                                ["seconds"] = door.UnlockSeconds.ToString()
                            });
                        }
                        catch (Exception e)
                        {
                            reason = "device_error:" + e.Message;
                        }
                    }
                }
            }

            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "access.unlock", "door", id.ToString(), result == "granted", detail: reason);
            return result == "granted"
                ? Results.Ok(new
                {
                    ok = true,
                    reason,
                    person = person?.FullName ?? visitor?.FullName,
                    zone = zoneAfter,
                    doorOpenUntil = door.OpenUntil
                })
                : Results.Json(new { ok = false, reason, zone = zoneAfter }, statusCode: 403);
        });

        g.MapGet("/access/logs", async (HttpContext ctx, PlatformDbContext db, int take = 100) =>
            await db.AccessLogs.AsNoTracking()
                .Where(l => l.TenantId == ctx.User.TenantId())
                .OrderByDescending(l => l.CreatedAt)
                .Take(Math.Clamp(take, 1, 500))
                .ToListAsync());

        // ---- Presença / anti-passback ----
        g.MapGet("/access/presence", async (HttpContext ctx, PlatformDbContext db) =>
        {
            var tenant = ctx.User.TenantId();
            var list = await db.AccessPresences.AsNoTracking()
                .Where(p => p.TenantId == tenant)
                .ToListAsync();
            var people = await db.AccessPeople.AsNoTracking()
                .Where(p => p.TenantId == tenant)
                .ToDictionaryAsync(p => p.Id, p => p.FullName);
            return list.Select(p => new
            {
                p.Id, p.PersonId,
                personName = people.GetValueOrDefault(p.PersonId, "?"),
                p.CurrentZone, p.LastDoorId, p.UpdatedAt
            });
        });

        g.MapPost("/access/presence/{personId:int}/reset", async (
            int personId, ResetZoneInput? input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!await CanManage(ctx)) return Results.Forbid();
            var tenant = ctx.User.TenantId();
            var person = await db.AccessPeople.FirstOrDefaultAsync(p => p.Id == personId && p.TenantId == tenant);
            if (person is null) return Results.NotFound();
            var zone = string.IsNullOrWhiteSpace(input?.Zone) ? "outside" : input!.Zone!.Trim();
            var presence = await db.AccessPresences
                .FirstOrDefaultAsync(p => p.TenantId == tenant && p.PersonId == personId);
            if (presence is null)
            {
                db.AccessPresences.Add(new AccessPresence
                {
                    TenantId = tenant,
                    PersonId = personId,
                    CurrentZone = zone,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                presence.CurrentZone = zone;
                presence.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "access.presence.reset", "person", personId.ToString(), true, detail: zone);
            return Results.Ok(new { ok = true, zone });
        });

        // ---- Visitantes ----
        g.MapGet("/access/visitors", async (HttpContext ctx, PlatformDbContext db) =>
            await db.AccessVisitors.AsNoTracking()
                .Where(v => v.TenantId == ctx.User.TenantId())
                .OrderByDescending(v => v.ValidTo)
                .Take(200)
                .ToListAsync());

        g.MapPost("/access/visitors", async (VisitorInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!await CanManage(ctx)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(input.FullName) || string.IsNullOrWhiteSpace(input.CredentialValue))
                return Results.BadRequest(new { error = "Nome e credencial obrigatórios." });
            var v = new AccessVisitor
            {
                TenantId = ctx.User.TenantId(),
                FullName = input.FullName.Trim(),
                HostName = input.HostName?.Trim() ?? "",
                CredentialValue = input.CredentialValue.Trim(),
                ValidFrom = input.ValidFrom ?? DateTime.UtcNow,
                ValidTo = input.ValidTo ?? DateTime.UtcNow.AddHours(8),
                Active = true,
                Notes = input.Notes?.Trim() ?? ""
            };
            db.AccessVisitors.Add(v);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "access.visitor.create", "visitor", v.Id.ToString(), true);
            return Results.Created($"/api/vms/access/visitors/{v.Id}", new { v.Id });
        });

        g.MapDelete("/access/visitors/{id:int}", async (int id, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!await CanManage(ctx)) return Results.Forbid();
            var v = await db.AccessVisitors.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == ctx.User.TenantId());
            if (v is null) return Results.NotFound();
            v.Active = false;
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "access.visitor.disable", "visitor", id.ToString(), true);
            return Results.NoContent();
        });

        // ---- Horários de acesso ----
        g.MapGet("/access/schedules", async (HttpContext ctx, PlatformDbContext db) =>
            await db.AccessSchedules.AsNoTracking()
                .Where(s => s.TenantId == ctx.User.TenantId())
                .OrderBy(s => s.Name)
                .ToListAsync());

        g.MapPost("/access/schedules", async (ScheduleInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!await CanManage(ctx)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(input.Name))
                return Results.BadRequest(new { error = "Nome do horário obrigatório." });
            if (!AccessScheduleEvaluator.TryParseHm(input.StartHm ?? "08:00", out _)
                || !AccessScheduleEvaluator.TryParseHm(input.EndHm ?? "18:00", out _))
                return Results.BadRequest(new { error = "StartHm/EndHm inválidos (use HH:mm)." });

            var s = new AccessSchedule
            {
                TenantId = ctx.User.TenantId(),
                Name = input.Name.Trim(),
                DaysOfWeek = string.IsNullOrWhiteSpace(input.DaysOfWeek) ? "1,2,3,4,5" : input.DaysOfWeek.Trim(),
                StartHm = (input.StartHm ?? "08:00").Trim(),
                EndHm = (input.EndHm ?? "18:00").Trim(),
                TimeZone = string.IsNullOrWhiteSpace(input.TimeZone) ? "America/Sao_Paulo" : input.TimeZone.Trim(),
                Active = input.Active ?? true
            };
            db.AccessSchedules.Add(s);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "access.schedule.create", "schedule", s.Id.ToString(), true);
            return Results.Created($"/api/vms/access/schedules/{s.Id}", s);
        });

        g.MapPut("/access/schedules/{id:int}", async (
            int id, ScheduleInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!await CanManage(ctx)) return Results.Forbid();
            var s = await db.AccessSchedules.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == ctx.User.TenantId());
            if (s is null) return Results.NotFound();
            if (!string.IsNullOrWhiteSpace(input.Name)) s.Name = input.Name.Trim();
            if (input.DaysOfWeek is not null) s.DaysOfWeek = input.DaysOfWeek.Trim();
            if (input.StartHm is not null) s.StartHm = input.StartHm.Trim();
            if (input.EndHm is not null) s.EndHm = input.EndHm.Trim();
            if (input.TimeZone is not null) s.TimeZone = input.TimeZone.Trim();
            if (input.Active is not null) s.Active = input.Active.Value;
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "access.schedule.update", "schedule", id.ToString(), true);
            return Results.Ok(s);
        });

        g.MapDelete("/access/schedules/{id:int}", async (int id, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (!await CanManage(ctx)) return Results.Forbid();
            var s = await db.AccessSchedules.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == ctx.User.TenantId());
            if (s is null) return Results.NotFound();
            db.AccessSchedules.Remove(s);
            // Desvincula portas/pessoas
            await db.AccessDoors.Where(d => d.ScheduleId == id).ExecuteUpdateAsync(u => u.SetProperty(d => d.ScheduleId, (int?)null));
            await db.AccessPeople.Where(p => p.ScheduleId == id).ExecuteUpdateAsync(u => u.SetProperty(p => p.ScheduleId, (int?)null));
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "access.schedule.delete", "schedule", id.ToString(), true);
            return Results.NoContent();
        });

        g.MapGet("/access/schedules/{id:int}/check", async (int id, HttpContext ctx, PlatformDbContext db) =>
        {
            var s = await db.AccessSchedules.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == ctx.User.TenantId());
            if (s is null) return Results.NotFound();
            var open = AccessScheduleEvaluator.IsOpenNow(s);
            var local = AccessScheduleEvaluator.ToLocal(DateTime.UtcNow, s.TimeZone);
            return Results.Ok(new
            {
                s.Id, s.Name, openNow = open,
                localNow = local.ToString("yyyy-MM-dd HH:mm"),
                dayOfWeek = (int)local.DayOfWeek,
                s.DaysOfWeek, s.StartHm, s.EndHm, s.TimeZone
            });
        });
    }

    private static AccessDoor ApplyDoor(AccessDoor d, DoorInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.Name)) d.Name = input.Name.Trim();
        if (input.DeviceId is not null) d.DeviceId = input.DeviceId == 0 ? null : input.DeviceId;
        if (!string.IsNullOrWhiteSpace(input.RelayAction)) d.RelayAction = input.RelayAction!;
        if (input.UnlockSeconds is > 0 and <= 60) d.UnlockSeconds = input.UnlockSeconds.Value;
        if (input.AntiPassback is not null) d.AntiPassback = input.AntiPassback.Value;
        if (!string.IsNullOrWhiteSpace(input.Direction)) d.Direction = input.Direction!.Trim().ToLowerInvariant();
        if (input.ZoneFrom is not null) d.ZoneFrom = input.ZoneFrom.Trim();
        if (input.ZoneTo is not null) d.ZoneTo = input.ZoneTo.Trim();
        if (input.InterlockWithDoorId is not null)
            d.InterlockWithDoorId = input.InterlockWithDoorId == 0 ? null : input.InterlockWithDoorId;
        if (input.InterlockRequireClosed is not null) d.InterlockRequireClosed = input.InterlockRequireClosed.Value;
        if (input.ScheduleId is not null) d.ScheduleId = input.ScheduleId == 0 ? null : input.ScheduleId;
        return d;
    }

    private static async Task<bool> CanManage(HttpContext ctx)
    {
        if (ctx.User.IsInRole("admin")) return true;
        var perms = ctx.RequestServices.GetService(typeof(PermissionService)) as PermissionService;
        if (perms is null) return false;
        return await perms.HasAsync(ctx.User.UserId(), Permissions.SystemConfig)
            || await perms.HasAsync(ctx.User.UserId(), Permissions.CameraConfig);
    }

    private static async Task<bool> HasPtzOrConfig(HttpContext ctx)
    {
        if (ctx.User.IsInRole("admin")) return true;
        var perms = ctx.RequestServices.GetService(typeof(PermissionService)) as PermissionService;
        if (perms is null) return false;
        var uid = ctx.User.UserId();
        return await perms.HasAsync(uid, Permissions.CameraPtz)
            || await perms.HasAsync(uid, Permissions.CameraConfig)
            || await perms.HasAsync(uid, Permissions.SystemConfig)
            || await perms.HasAsync(uid, Permissions.EventAck);
    }
}

public record PersonInput(string? FullName, string? Document, string? Company, string? Email, string? Phone, bool? Active, int? ScheduleId);
public record CredInput(string? Kind, string? Value, DateTime? ValidFrom, DateTime? ValidTo);
public record DoorInput(
    string? Name, int? DeviceId, string? RelayAction, int? UnlockSeconds, bool? AntiPassback,
    string? Direction, string? ZoneFrom, string? ZoneTo,
    int? InterlockWithDoorId, bool? InterlockRequireClosed, int? ScheduleId);
public record UnlockInput(string? Credential);
public record VisitorInput(string? FullName, string? HostName, string? CredentialValue, DateTime? ValidFrom, DateTime? ValidTo, string? Notes);
public record ResetZoneInput(string? Zone);
public record ScheduleInput(
    string? Name, string? DaysOfWeek, string? StartHm, string? EndHm, string? TimeZone, bool? Active);
