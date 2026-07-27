using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// Mantém uma assinatura de eventos por dispositivo, usando o protocolo nativo
/// do fabricante (ex.: alertStream ISAPI da Hikvision).
///
/// É aqui que a analítica da própria câmera — detecção de movimento, cruzamento
/// de linha, intrusão, face, placa — entra na plataforma sem polling.
/// </summary>
public class DeviceEventListener(
    IServiceScopeFactory scopes,
    IEventBus bus,
    IOptions<VmsOptions> options,
    ILogger<DeviceEventListener> log) : BackgroundService
{
    private readonly VmsOptions _opt = options.Value;
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(ct);
            }
            catch (Exception e)
            {
                log.LogError(e, "Falha ao reconciliar assinaturas de evento");
            }
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }

        foreach (var cts in _running.Values) cts.Cancel();
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        // Sharding: sem isso, cada no assinaria todas as cameras e o mesmo
        // evento entraria no banco uma vez por instancia.
        var devices = (await db.Devices.AsNoTracking().ToListAsync(ct))
            .Where(d => _opt.OwnsDevice(d.Id))
            .ToList();

        var ids = devices.Select(d => d.Id).ToHashSet();

        // Encerra assinaturas de dispositivos removidos.
        foreach (var id in _running.Keys.Where(id => !ids.Contains(id)).ToList())
            if (_running.TryRemove(id, out var cts)) cts.Cancel();

        foreach (var device in devices.Where(d => !_running.ContainsKey(d.Id)))
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!_running.TryAdd(device.Id, cts)) { cts.Dispose(); continue; }

            _ = ListenAsync(device, cts.Token);
        }
    }

    private async Task ListenAsync(Device device, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<DriverRegistry>();

        IDeviceDriver driver;
        try { driver = registry.Resolve(device); }
        catch (Exception e) { log.LogWarning("{Message}", e.Message); return; }

        try
        {
            await foreach (var evt in driver.StreamEventsAsync(device, ct))
            {
                // Escopo próprio por evento: o DbContext não é thread-safe e
                // esta assinatura vive por horas.
                using var evtScope = scopes.CreateScope();
                var db = evtScope.ServiceProvider.GetRequiredService<PlatformDbContext>();

                // O driver conhece o protocolo, nao a instalacao: a origem e o
                // tenant sao carimbados aqui, senao o evento fica sem dono e
                // escapa do filtro por camera visivel.
                evt.DeviceId = device.Id;
                evt.TenantId = device.TenantId;

                // Espelha disponibilidade no cadastro para listagens /admin e /vms.
                if (IsOnlineEvent(evt.Type))
                {
                    await db.Devices.Where(d => d.Id == device.Id)
                        .ExecuteUpdateAsync(u => u
                            .SetProperty(d => d.Status, DeviceStatus.Online)
                            .SetProperty(d => d.LastSeen, DateTime.UtcNow), ct);
                }
                else if (IsOfflineEvent(evt.Type))
                {
                    await db.Devices.Where(d => d.Id == device.Id)
                        .ExecuteUpdateAsync(u => u
                            .SetProperty(d => d.Status, DeviceStatus.Offline), ct);
                }

                // LPR: cruza placa do payload com listas allow/deny/watch.
                await EnrichLprMatchAsync(db, evt, ct);
                // Face: cruza faceId com galeria facial.
                await EnrichFaceMatchAsync(db, evt, ct);

                db.Events.Add(evt);
                await db.SaveChangesAsync(ct);
                await bus.PublishAsync(evt, ct);

                log.LogInformation("Evento {Type} da camera {Id} ({Name})",
                    evt.Type, device.Id, device.Name);
            }
        }
        catch (OperationCanceledException) { /* desligamento normal */ }
        catch (Exception e)
        {
            log.LogError(e, "Assinatura de eventos da camera {Id} terminou com erro", device.Id);
        }
        finally
        {
            _running.TryRemove(device.Id, out _);   // a reconciliação reergue
        }
    }

    private static bool IsOnlineEvent(string type) =>
        type is "device_online" or "online" or "video_restore";

    private static bool IsOfflineEvent(string type) =>
        type is "device_offline" or "offline" or "video_loss";

    /// <summary>
    /// Se o evento tem placa (meta.plate), marca listMatch allow/deny/watch no payload
    /// e eleva severidade em deny.
    /// </summary>
    private static async Task EnrichLprMatchAsync(
        PlatformDbContext db, DeviceEvent evt, CancellationToken ct)
    {
        if (evt.Type is not ("lpr_detected" or "anpr" or "lpr")) return;
        var meta = EventMetadata.TryParseFromPayload(evt.Payload);
        var plate = EventMetadata.NormalizePlate(meta?.Plate);
        if (string.IsNullOrEmpty(plate)) return;

        var rule = await db.LicensePlateRules.AsNoTracking()
            .Where(r => r.TenantId == evt.TenantId && r.Active && r.Plate == plate)
            .OrderByDescending(r => r.ListType == "deny")
            .FirstOrDefaultAsync(ct);
        if (rule is null) return;

        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(evt.Payload) ? "{}" : evt.Payload)
                       as JsonObject ?? new JsonObject();
            if (node["meta"] is not JsonObject m)
            {
                m = new JsonObject();
                node["meta"] = m;
            }
            m["listMatch"] = rule.ListType;
            m["plate"] = plate;
            m["ownerName"] = rule.OwnerName;
            node["listMatch"] = rule.ListType;
            evt.Payload = node.ToJsonString();
            if (rule.ListType == "deny")
                evt.Severity = 3;
            else if (rule.ListType == "allow" && evt.Severity < 2)
                evt.Severity = 1;
        }
        catch
        {
            /* payload inválido — ignora match */
        }
    }

    /// <summary>
    /// Se o evento tem faceId, marca personName da galeria no payload.
    /// </summary>
    private static async Task EnrichFaceMatchAsync(
        PlatformDbContext db, DeviceEvent evt, CancellationToken ct)
    {
        if (evt.Type is not ("face_detected" or "face" or "facedetection")) return;
        var meta = EventMetadata.TryParseFromPayload(evt.Payload);
        var faceId = meta?.FaceId?.Trim() ?? "";
        if (faceId.Length == 0) return;

        var entry = await db.FaceGalleryEntries.AsNoTracking()
            .FirstOrDefaultAsync(f => f.TenantId == evt.TenantId && f.Active && f.ExternalFaceId == faceId, ct);
        if (entry is null) return;

        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(evt.Payload) ? "{}" : evt.Payload)
                       as JsonObject ?? new JsonObject();
            if (node["meta"] is not JsonObject m)
            {
                m = new JsonObject();
                node["meta"] = m;
            }
            m["faceId"] = faceId;
            m["personName"] = entry.Name;
            m["faceGalleryId"] = entry.Id;
            node["faceMatch"] = entry.Name;
            evt.Payload = node.ToJsonString();
        }
        catch
        {
            /* payload inválido */
        }
    }
}
