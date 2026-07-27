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
/// Monitora o MediaMTX: se cair, publica <c>media_gateway_down</c>;
/// na recuperação, <c>media_gateway_up</c> e força re-sync de paths.
/// </summary>
public sealed class MediaGatewayHealthService(
    IServiceScopeFactory scopes,
    IEventBus bus,
    IOptions<VmsOptions> options,
    VmsMetrics metrics,
    ILogger<MediaGatewayHealthService> log) : BackgroundService
{
    private readonly VmsOptions _opt = options.Value;
    private bool? _lastUp;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Falha no health do MediaMTX");
            }
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var media = scope.ServiceProvider.GetRequiredService<MediaGateway>();
        var up = await media.PingAsync(ct);
        metrics.SetMediaGatewayUp(up);

        if (_lastUp is null)
        {
            _lastUp = up;
            if (!up)
                log.LogWarning("MediaMTX inacessível em {Api}", _opt.MediaMtxApi);
            return;
        }

        if (_lastUp == up) return;

        if (!up)
        {
            metrics.IncMediaDown();
            log.LogError("MediaMTX DOWN — live/gravação via gateway comprometidos");
            await PublishAsync(scope, "media_gateway_down", 3, ct);
        }
        else
        {
            metrics.IncMediaRecovery();
            log.LogInformation("MediaMTX recuperado — re-registrando paths");
            await PublishAsync(scope, "media_gateway_up", 1, ct);
            // MediaSyncService re-registra no próximo ciclo; força um imediato.
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var registry = scope.ServiceProvider.GetRequiredService<DriverRegistry>();
                var cams = await db.Devices.AsNoTracking()
                    .Where(d => d.Kind == DeviceKind.Camera).ToListAsync(ct);
                var perfis = await db.MediaProfiles.AsNoTracking()
                    .ToDictionaryAsync(p => p.Id, ct);
                foreach (var cam in cams)
                {
                    var baseUrl = await registry.Resolve(cam).GetStreamUrlAsync(cam, ct);
                    var ch = StreamUrlBuilder.ResolveChannel(cam, perfis, StreamUrlBuilder.Quality.Main);
                    var rtsp = StreamUrlBuilder.ApplyQuality(baseUrl, StreamUrlBuilder.Quality.Main, ch);
                    await media.RegisterAsync(cam.Id, rtsp, substream: false, ct);
                }
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Re-registro de paths após recovery falhou");
            }
        }

        _lastUp = up;
    }

    private async Task PublishAsync(IServiceScope scope, string type, int severity, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var evt = new DeviceEvent
        {
            TenantId = 1,
            DeviceId = null,
            Type = type,
            Severity = severity,
            Payload = $"{{\"api\":\"{_opt.MediaMtxApi}\",\"node\":\"{_opt.ResolveNodeId()}\"}}"
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync(ct);
        await bus.PublishAsync(evt, ct);
    }
}
