using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;
using SecurityPlatform.Core.Events;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Saúde por câmera: online/offline, silêncio de gravação e último evento.
///
/// Publica <c>device_offline</c> / <c>device_online</c> na transição de status
/// (WebSocket do monitor e automação reagem na hora) e
/// <c>recording_stalled</c> quando a câmera deveria gravar mas nenhum segmento
/// novo aparece no disco há mais de
/// <see cref="VmsOptions.SilentRecordingMinutes"/> minutos.
/// </summary>
public class CameraHealthService(
    IServiceScopeFactory scopes,
    IEventBus bus,
    IOptions<VmsOptions> options,
    VmsMetrics metrics,
    ILogger<CameraHealthService> log) : BackgroundService
{
    private readonly VmsOptions _opt = options.Value;
    private readonly Dictionary<int, DateTime> _ultimoAlarme = new();
    private readonly Dictionary<int, DateTime> _ultimoGap = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Aguarda o gravador e a sincronização de mídia subirem.
        await Task.Delay(TimeSpan.FromSeconds(20), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Falha na verificação de saúde das câmeras");
            }

            // 20s: offline vira evento em tempo útil sem martelar o equipamento.
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<DriverRegistry>();

        var cameras = await db.Devices
            .Where(d => d.Kind == DeviceKind.Camera)
            .AsNoTracking()
            .ToListAsync(ct);

        var minhas = cameras.Where(c => _opt.OwnsDevice(c.Id)).ToList();
        if (minhas.Count == 0) return;

        var ids = minhas.Select(c => c.Id).ToList();
        var agora = DateTime.UtcNow;
        var limiteSilencio = TimeSpan.FromMinutes(Math.Max(_opt.SilentRecordingMinutes, 2));

        // Último segmento indexado por câmera.
        var ultimasGravacoes = await db.Recordings.AsNoTracking()
            .Where(r => ids.Contains(r.DeviceId))
            .GroupBy(r => r.DeviceId)
            .Select(g => new { DeviceId = g.Key, Last = g.Max(r => r.StartedAt) })
            .ToDictionaryAsync(x => x.DeviceId, x => x.Last, ct);

        var slots = await db.ScheduleSlots.AsNoTracking()
            .Where(s => ids.Contains(s.DeviceId) && s.Kind == ScheduleKind.Recording && s.Enabled)
            .ToListAsync(ct);
        var slotsPorCam = slots.GroupBy(s => s.DeviceId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ScheduleSlot>)g.ToList());

        var onlineCount = 0;
        var offlineCount = 0;

        foreach (var cam in minhas)
        {
            // Probe de conectividade (leve — TCP / ISAPI conforme o driver).
            var online = false;
            try
            {
                online = await registry.Resolve(cam).ConnectAsync(cam, ct);
            }
            catch (Exception e)
            {
                log.LogDebug(e, "Probe de saúde falhou para câmera {Id}", cam.Id);
            }

            if (online) onlineCount++; else offlineCount++;

            var statusAnterior = cam.Status;
            if (online)
            {
                await db.Devices.Where(d => d.Id == cam.Id)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(d => d.Status, DeviceStatus.Online)
                        .SetProperty(d => d.LastSeen, agora), ct);

                if (statusAnterior != DeviceStatus.Online)
                {
                    await PublicarStatusAsync(db, cam, "device_online", severity: 1, ct);
                    log.LogInformation("Câmera {Id} ({Name}) online", cam.Id, cam.Name);
                }
            }
            else
            {
                await db.Devices.Where(d => d.Id == cam.Id)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(d => d.Status, DeviceStatus.Offline), ct);

                if (statusAnterior != DeviceStatus.Offline)
                {
                    await PublicarStatusAsync(db, cam, "device_offline", severity: 3, ct);
                    log.LogWarning("Câmera {Id} ({Name}) offline", cam.Id, cam.Name);
                }
            }

            if (cam.Recording == RecordingMode.Off) continue;
            if (cam.Recording == RecordingMode.Continuous)
            {
                var faixas = slotsPorCam.GetValueOrDefault(cam.Id) ?? Array.Empty<ScheduleSlot>();
                if (!RecordingSchedule.IsActive(faixas, ScheduleKind.Recording, agora))
                    continue;
            }
            else if (cam.Recording == RecordingMode.OnEvent)
            {
                // No modo por evento, silêncio longo é normal.
                continue;
            }

            if (!ultimasGravacoes.TryGetValue(cam.Id, out var ultimo))
                ultimo = cam.CreatedAt;

            if (agora - ultimo < limiteSilencio) continue;

            // Evita spam: no máximo um alarme a cada janela de silêncio.
            if (_ultimoAlarme.TryGetValue(cam.Id, out var ja) && agora - ja < limiteSilencio)
                continue;

            _ultimoAlarme[cam.Id] = agora;

            var evt = new DeviceEvent
            {
                TenantId = cam.TenantId,
                DeviceId = cam.Id,
                Type = "recording_stalled",
                Severity = 3,
                Payload = $"{{\"minutesSilent\":{(int)(agora - ultimo).TotalMinutes},\"lastSegment\":\"{ultimo:o}\"}}"
            };

            db.Events.Add(evt);
            await db.SaveChangesAsync(ct);
            await bus.PublishAsync(evt, ct);

            log.LogWarning(
                "Câmera {Id} ({Name}) sem gravação há {Min} min — evento recording_stalled",
                cam.Id, cam.Name, (int)(agora - ultimo).TotalMinutes);
        }

        // Detecta buracos recentes (gap entre segmentos) em câmeras contínuas.
        await DetectGapsAsync(db, minhas, agora, ct);

        metrics.SetCamerasOnline(onlineCount);
        metrics.SetCamerasOffline(offlineCount);
    }

    private async Task DetectGapsAsync(
        PlatformDbContext db, List<Device> cams, DateTime agora, CancellationToken ct)
    {
        var gapMin = Math.Max(_opt.GapAlertMinutes, 2);
        var gap = TimeSpan.FromMinutes(gapMin);
        var lookback = agora.AddHours(-6);

        foreach (var cam in cams.Where(c => c.Recording == RecordingMode.Continuous))
        {
            var segs = await db.Recordings.AsNoTracking()
                .Where(r => r.DeviceId == cam.Id && r.StartedAt >= lookback)
                .OrderBy(r => r.StartedAt)
                .Select(r => new { r.StartedAt, r.EndedAt })
                .ToListAsync(ct);

            if (segs.Count < 2) continue;

            for (var i = 0; i < segs.Count - 1; i++)
            {
                var fim = segs[i].EndedAt ?? segs[i].StartedAt;
                var buraco = segs[i + 1].StartedAt - fim;
                if (buraco < gap) continue;

                // Anti-spam por câmera.
                if (_ultimoGap.TryGetValue(cam.Id, out var ja) && agora - ja < gap)
                    break;

                _ultimoGap[cam.Id] = agora;
                metrics.IncGap();

                var evt = new DeviceEvent
                {
                    TenantId = cam.TenantId,
                    DeviceId = cam.Id,
                    Type = "recording_gap",
                    Severity = 2,
                    Payload = $"{{\"from\":\"{fim:o}\",\"to\":\"{segs[i + 1].StartedAt:o}\",\"minutes\":{(int)buraco.TotalMinutes}}}"
                };
                db.Events.Add(evt);
                await db.SaveChangesAsync(ct);
                await bus.PublishAsync(evt, ct);
                log.LogWarning(
                    "Câmera {Id} gap de gravação {Min} min ({From:o} → {To:o})",
                    cam.Id, (int)buraco.TotalMinutes, fim, segs[i + 1].StartedAt);
                break;
            }
        }
    }

    private async Task PublicarStatusAsync(
        PlatformDbContext db, Device cam, string type, int severity, CancellationToken ct)
    {
        var evt = new DeviceEvent
        {
            TenantId = cam.TenantId,
            DeviceId = cam.Id,
            Type = type,
            Severity = severity,
            Payload = $"{{\"host\":\"{cam.Host}\",\"port\":{cam.Port},\"previous\":\"{cam.Status}\"}}"
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync(ct);
        await bus.PublishAsync(evt, ct);
    }

    /// <summary>
    /// Snapshot de saúde usado pela API (sem side-effects de gravação).
    /// </summary>
    public static async Task<IReadOnlyList<CameraHealth>> SnapshotAsync(
        PlatformDbContext db, VmsOptions opt, CancellationToken ct = default)
    {
        var cameras = await db.Devices.AsNoTracking()
            .Where(d => d.Kind == DeviceKind.Camera)
            .ToListAsync(ct);

        var ids = cameras.Select(c => c.Id).ToList();
        var ultimas = await db.Recordings.AsNoTracking()
            .Where(r => ids.Contains(r.DeviceId))
            .GroupBy(r => r.DeviceId)
            .Select(g => new { DeviceId = g.Key, Last = g.Max(r => r.StartedAt), Bytes = g.Sum(r => r.SizeBytes) })
            .ToDictionaryAsync(x => x.DeviceId, x => x, ct);

        var agora = DateTime.UtcNow;
        var limite = TimeSpan.FromMinutes(Math.Max(opt.SilentRecordingMinutes, 2));

        return cameras.Select(c =>
        {
            ultimas.TryGetValue(c.Id, out var u);
            var last = u?.Last;
            var gravando = c.Recording != RecordingMode.Off && last is not null && agora - last < limite;
            var silencioso = c.Recording == RecordingMode.Continuous
                && (last is null || agora - last >= limite);

            var st = RecorderService.GetStats(c.Id);
            return new CameraHealth(
                c.Id,
                c.Name,
                c.Status.ToString(),
                c.Recording.ToString(),
                c.LastSeen,
                last,
                u?.Bytes ?? 0,
                gravando,
                silencioso,
                c.Recording == RecordingMode.Continuous && silencioso
                    ? "recording_stalled"
                    : c.Status == DeviceStatus.Offline ? "offline" : "ok",
                st?.Fps,
                st?.BitrateKbps,
                st?.At);
        }).ToList();
    }
}

public record CameraHealth(
    int DeviceId,
    string Name,
    string Status,
    string RecordingMode,
    DateTime? LastSeen,
    DateTime? LastSegmentAt,
    long StorageBytes,
    bool IsRecording,
    bool IsSilent,
    string Health,
    double? Fps = null,
    double? BitrateKbps = null,
    DateTime? StatsAt = null);
