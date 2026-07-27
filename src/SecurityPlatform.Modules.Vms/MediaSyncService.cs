using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Mantém no MediaMTX os paths principal (<c>camN</c>) e sub (<c>camNs</c>)
/// de cada câmera, e remove órfãos.
/// </summary>
public class MediaSyncService(
    IServiceScopeFactory scopes,
    ILogger<MediaSyncService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Primeira passagem rápida para o grid já ter path na abertura.
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SyncAsync(ct);
            }
            catch (Exception e)
            {
                log.LogError(e, "Falha ao sincronizar o no de midia");
            }
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
        }
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<PlatformDbContext>();
        var registry = sp.GetRequiredService<DriverRegistry>();
        var media = sp.GetRequiredService<MediaGateway>();

        var cameras = await db.Devices
            .Where(d => d.Kind == DeviceKind.Camera)
            .AsNoTracking().ToListAsync(ct);

        var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<VmsOptions>>().Value;
        var singlePull = opt.SingleCameraRtspPull;

        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cameras)
        {
            expected.Add(MediaGateway.PathName(c.Id, substream: false));
            // Sub nativo só entra na lista se a política permitir 2º RTSP.
            if (!singlePull)
                expected.Add(MediaGateway.PathName(c.Id, substream: true));
            if (opt.TranscodeLive)
                expected.Add(LiveTranscodeService.TranscodePathName(c.Id));
        }

        var existing = await media.ListPathNamesAsync(ct);
        foreach (var orphan in existing.Where(p => p.StartsWith("cam", StringComparison.Ordinal)
                                                   && !expected.Contains(p)))
        {
            // Com single-pull, camNs é orfão intencional — remove para fechar
            // a 2ª sessão RTSP na câmera.
            log.LogInformation("Removendo path orfao do no de midia: {Path}", orphan);
            await media.RemovePathAsync(orphan, ct);
        }

        var perfis = await db.MediaProfiles.AsNoTracking().ToDictionaryAsync(p => p.Id, ct);

        foreach (var cam in cameras)
        {
            // Um único pull permanente: main. Live/gravador/transcoder leem do MediaMTX.
            var baseUrl = await registry.Resolve(cam).GetStreamUrlAsync(cam, ct);
            var mainCh = StreamUrlBuilder.ResolveChannel(cam, perfis, StreamUrlBuilder.Quality.Main);
            var mainRtsp = StreamUrlBuilder.ApplyQuality(baseUrl, StreamUrlBuilder.Quality.Main, mainCh);
            await media.RegisterAsync(cam.Id, mainRtsp, substream: false, ct);

            if (!singlePull)
            {
                var subCh = StreamUrlBuilder.ResolveChannel(cam, perfis, StreamUrlBuilder.Quality.Sub);
                var subRtsp = StreamUrlBuilder.ApplyQuality(baseUrl, StreamUrlBuilder.Quality.Sub, subCh);
                await media.RegisterAsync(cam.Id, subRtsp, substream: true, ct);
            }
        }
    }
}
