using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Transcodificação opcional H.265→H.264 para live no browser.
/// FFmpeg puxa o RTSP da câmera e publica em RTSP no MediaMTX path <c>cam{id}tc</c>.
/// O endpoint /stream usa o path tc quando Vms:TranscodeLive está ligado.
/// </summary>
public class LiveTranscodeService(
    IServiceScopeFactory scopes,
    IOptions<VmsOptions> options,
    ILogger<LiveTranscodeService> log) : BackgroundService
{
    private readonly VmsOptions _opt = options.Value;
    private readonly ConcurrentDictionary<int, Process> _procs = new();

    public static string TranscodePathName(int deviceId) => $"cam{deviceId}tc";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.TranscodeLive)
        {
            log.LogInformation("Transcodificação live desligada (Vms:TranscodeLive=false).");
            return;
        }

        log.LogInformation("Transcodificação live H.264 ativa");
        while (!ct.IsCancellationRequested)
        {
            try { await ReconcileAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Falha no reconcile de transcoder");
            }
            // Ciclo curto: após restart do MediaMTX o path tc precisa voltar rápido.
            await Task.Delay(TimeSpan.FromSeconds(8), ct);
        }

        foreach (var p in _procs.Values) Kill(p);
        _procs.Clear();
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<DriverRegistry>();
        var media = scope.ServiceProvider.GetRequiredService<MediaGateway>();

        var cameras = await db.Devices.AsNoTracking()
            .Where(d => d.Kind == DeviceKind.Camera)
            .ToListAsync(ct);

        var ids = cameras.Where(c => _opt.OwnsDevice(c.Id)).Select(c => c.Id).ToHashSet();

        foreach (var id in _procs.Keys.Where(id => !ids.Contains(id)).ToList())
            if (_procs.TryRemove(id, out var p)) Kill(p);

        foreach (var cam in cameras.Where(c => ids.Contains(c.Id)))
        {
            if (_procs.TryGetValue(cam.Id, out var existing) && !existing.HasExited)
                continue;
            if (existing is not null) _procs.TryRemove(cam.Id, out _);

            // Entrada = path local do gateway (já tem 1 pull na câmera).
            // NÃO puxar a câmera de novo — esgota sessão RTSP (SETUP 500).
            var cameraRtsp = await registry.Resolve(cam).GetStreamUrlAsync(cam, ct);
            await media.RegisterAsync(cam.Id, cameraRtsp, substream: false, ct);
            if (!await media.IsReadyAsync(cam.Id, substream: false, ct))
                continue; // path principal ainda não ready; tenta no próximo ciclo

            var rtsp = media.LocalRtspUrl(cam.Id, substream: false);
            var path = TranscodePathName(cam.Id);

            // Path publisher no MediaMTX (sem source — FFmpeg publica).
            await media.RegisterPublisherAsync(path, ct);

            var publish = $"rtsp://127.0.0.1:{_opt.MediaMtxRtspPort}/{path}";
            var args = string.Join(' ',
                "-nostdin -hide_banner -loglevel error",
                "-rtsp_transport tcp -timeout 5000000",
                $"-i \"{rtsp}\"",
                "-c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p",
                "-g 30 -bf 0 -an",
                "-f rtsp -rtsp_transport tcp",
                $"\"{publish}\"");

            try
            {
                var p = Process.Start(new ProcessStartInfo(_opt.FfmpegPath, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                });
                if (p is null) continue;
                // Drena stderr para o pipe não encher e matar o FFmpeg.
                p.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)
                        && (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase)
                            || e.Data.Contains("failed", StringComparison.OrdinalIgnoreCase)))
                        log.LogDebug("Transcoder cam {Id}: {Msg}", cam.Id, e.Data);
                };
                p.BeginErrorReadLine();
                _procs[cam.Id] = p;
                log.LogInformation("Transcoder live câmera {Id} → {Path}", cam.Id, path);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Não iniciou transcoder da câmera {Id}", cam.Id);
            }
        }
    }

    private void Kill(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            p.Dispose();
        }
        catch (Exception e)
        {
            log.LogDebug(e, "Erro ao matar transcoder");
        }
    }
}
