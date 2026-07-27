using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;
using SecurityPlatform.Core.Events;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Motor IFTTT: SE evento ENTÃO ações — com agenda horária e cooldown (timer).
/// Ações de cliente (popup, som, live) são republicadas no barramento para o posto.
/// </summary>
public class AutomationEngine(
    IServiceScopeFactory scopes,
    IEventBus bus,
    ILogger<AutomationEngine> log) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> ClientKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "PopupVideo", "PlaySound", "PushNotification", "OpenLive", "OpenPlayback", "OpenMap"
    };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Motor de automação ativo");

        try
        {
            await foreach (var evt in bus.SubscribeAsync(ct))
            {
                // Evita loop: eventos gerados pela própria automação
                if (string.Equals(evt.Type, "client_action", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(evt.Type, "automation_fired", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    await HandleAsync(evt, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "Falha ao processar automação para evento {Type} #{Id}",
                        evt.Type, evt.Id);
                }
            }
        }
        catch (OperationCanceledException) { /* desligamento */ }
    }

    private async Task HandleAsync(DeviceEvent evt, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<DriverRegistry>();
        var runner = scope.ServiceProvider.GetRequiredService<EventActionRunner>();

        var regras = await db.AutomationRules
            .Where(r => r.Enabled && r.TenantId == evt.TenantId)
            .Where(r => r.MinSeverity <= evt.Severity)
            .Where(r => r.WhenDeviceId == null || r.WhenDeviceId == evt.DeviceId)
            .ToListAsync(ct);

        regras = regras.Where(r =>
            string.IsNullOrWhiteSpace(r.WhenEventType) ||
            r.WhenEventType == "*" ||
            string.Equals(r.WhenEventType, evt.Type, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (regras.Count == 0) return;

        var now = DateTime.UtcNow;

        foreach (var regra in regras)
        {
            if (!IsInSchedule(regra, now))
            {
                log.LogDebug("Regra {Id} fora da agenda — ignorada", regra.Id);
                continue;
            }

            if (regra.CooldownSeconds > 0 && regra.LastFiredAt is DateTime last)
            {
                var elapsed = (now - last).TotalSeconds;
                if (elapsed < regra.CooldownSeconds)
                {
                    log.LogDebug("Regra {Id} em cooldown ({Left:0}s) — ignorada",
                        regra.Id, regra.CooldownSeconds - elapsed);
                    continue;
                }
            }

            List<EventActionRunner.ActionDto> acoes;
            try
            {
                acoes = JsonSerializer.Deserialize<List<EventActionRunner.ActionDto>>(regra.Actions, JsonOpts) ?? [];
            }
            catch (JsonException e)
            {
                log.LogWarning(e, "Ações inválidas na regra {Id} ({Name})", regra.Id, regra.Name);
                continue;
            }

            if (acoes.Count == 0) continue;

            // Server-side
            await runner.RunAllAsync(acoes, evt, db, registry, ct);

            // Atualiza cooldown na entidade tracked
            var tracked = await db.AutomationRules.FindAsync([regra.Id], ct);
            if (tracked is not null)
            {
                tracked.LastFiredAt = now;
                await db.SaveChangesAsync(ct);
            }

            // Cliente: popup / som / live — publica evento paralelo no WS
            var clientActs = acoes
                .Where(a => ClientKinds.Contains(a.Kind ?? ""))
                .Select(a => new
                {
                    kind = a.Kind,
                    deviceId = a.DeviceId ?? evt.DeviceId,
                    sound = a.Title, // reusa Title como nome do som opcional
                    seconds = a.SecondsAfter ?? a.SecondsBefore ?? 20,
                    preset = a.Preset
                })
                .ToList();

            if (clientActs.Count > 0)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    sourceEventId = evt.Id,
                    sourceType = evt.Type,
                    ruleId = regra.Id,
                    ruleName = regra.Name,
                    actions = clientActs
                });

                await bus.PublishAsync(new DeviceEvent
                {
                    TenantId = evt.TenantId,
                    DeviceId = evt.DeviceId,
                    Type = "client_action",
                    Severity = evt.Severity,
                    Payload = payload,
                    CreatedAt = now
                }, ct);
            }

            log.LogInformation("Regra {Id} ({Name}) executada para evento {Type} da câmera {Cam}",
                regra.Id, regra.Name, evt.Type, evt.DeviceId);
        }
    }

    /// <summary>Verifica se agora (UTC) cai na janela de dias/horário da regra.</summary>
    internal static bool IsInSchedule(AutomationRule regra, DateTime utcNow)
    {
        DateTime local;
        try
        {
            var tzId = string.IsNullOrWhiteSpace(regra.TimeZone) ? "UTC" : regra.TimeZone;
            // Windows pode não ter IANA; tenta e cai para local do servidor.
            TimeZoneInfo tz;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(tzId); }
            catch (TimeZoneNotFoundException)
            {
                try { tz = TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time"); }
                catch { tz = TimeZoneInfo.Local; }
            }
            catch (InvalidTimeZoneException) { tz = TimeZoneInfo.Local; }

            local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz);
        }
        catch
        {
            local = utcNow.ToLocalTime();
        }

        // Dias
        var daysRaw = (regra.ScheduleDays ?? "*").Trim();
        if (!string.IsNullOrEmpty(daysRaw) && daysRaw != "*")
        {
            var allowed = daysRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var d) ? d : -1)
                .Where(d => d is >= 0 and <= 6)
                .ToHashSet();
            if (allowed.Count > 0 && !allowed.Contains((int)local.DayOfWeek))
                return false;
        }

        // Horário
        if (!TryParseHm(regra.ScheduleStart, out var start)) start = TimeSpan.Zero;
        if (!TryParseHm(regra.ScheduleEnd, out var end)) end = new TimeSpan(23, 59, 59);

        var t = local.TimeOfDay;
        if (end < start)
        {
            // Cruza meia-noite: 22:00–06:00
            return t >= start || t <= end;
        }

        return t >= start && t <= end;
    }

    private static bool TryParseHm(string? hm, out TimeSpan ts)
    {
        ts = default;
        if (string.IsNullOrWhiteSpace(hm)) return false;
        var parts = hm.Trim().Split(':');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m))
            return false;
        if (h is < 0 or > 23 || m is < 0 or > 59) return false;
        ts = new TimeSpan(h, m, 0);
        return true;
    }
}
