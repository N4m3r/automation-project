using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Gera miniaturas de timeline (JPEG) a cada N minutos por câmera.
/// Pasta: <c>{StoragePath}/_thumbs/{deviceId}/{yyyyMMdd_HHmm}.jpg</c>
/// </summary>
public sealed class ThumbnailService(
    IServiceScopeFactory scopes,
    IOptions<VmsOptions> options,
    VmsMetrics metrics,
    ILogger<ThumbnailService> log) : BackgroundService
{
    private readonly VmsOptions _opt = options.Value;

    public static string ThumbsRoot(string storagePath) =>
        Path.Combine(storagePath, "_thumbs");

    public static string ThumbPath(string storagePath, int deviceId, DateTime utc) =>
        Path.Combine(ThumbsRoot(storagePath), deviceId.ToString(),
            utc.ToString("yyyyMMdd_HHmm") + ".jpg");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_opt.ThumbnailIntervalMinutes <= 0)
        {
            log.LogInformation("Thumbnails de timeline desligados (ThumbnailIntervalMinutes=0)");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(45), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await RunAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Falha ao gerar thumbnails");
            }
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(_opt.ThumbnailIntervalMinutes, 5)), ct);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var crypto = scope.ServiceProvider.GetRequiredService<RecordingCrypto>();

        var interval = TimeSpan.FromMinutes(Math.Max(_opt.ThumbnailIntervalMinutes, 5));
        var since = DateTime.UtcNow.AddHours(-6);

        var cams = await db.Devices.AsNoTracking()
            .Where(d => d.Kind == DeviceKind.Camera)
            .Select(d => d.Id)
            .ToListAsync(ct);

        foreach (var camId in cams.Where(id => _opt.OwnsDevice(id)))
        {
            var segs = await db.Recordings.AsNoTracking()
                .Where(r => r.DeviceId == camId && r.StartedAt >= since)
                .OrderByDescending(r => r.StartedAt)
                .Take(8)
                .ToListAsync(ct);

            foreach (var seg in segs)
            {
                ct.ThrowIfCancellationRequested();
                var slot = FloorToInterval(seg.StartedAt, interval);
                var outPath = ThumbPath(_opt.StoragePath, camId, slot);
                if (File.Exists(outPath)) continue;

                var disk = StoragePaths.ResolveExisting(seg.Path, _opt.StoragePath);
                if (disk is null) continue;

                string plain;
                var isTemp = false;
                try
                {
                    (plain, isTemp) = crypto.EnsurePlainPath(disk);
                }
                catch
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    if (await ExtractJpegAsync(plain, outPath, ct))
                        metrics.IncThumbnail();
                }
                finally
                {
                    if (isTemp)
                        try { File.Delete(plain); } catch { /* */ }
                }
            }
        }
    }

    private async Task<bool> ExtractJpegAsync(string videoPath, string jpegPath, CancellationToken ct)
    {
        try
        {
            var args = $"-nostdin -hide_banner -loglevel error -y -ss 1 -i \"{videoPath}\" -frames:v 1 -q:v 5 \"{jpegPath}\"";
            using var p = Process.Start(new ProcessStartInfo(_opt.FfmpegPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            });
            if (p is null) return false;
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 && File.Exists(jpegPath) && new FileInfo(jpegPath).Length > 100;
        }
        catch (Exception e)
        {
            log.LogDebug(e, "Thumbnail FFmpeg falhou para {Path}", videoPath);
            return false;
        }
    }

    internal static DateTime FloorToInterval(DateTime utc, TimeSpan interval)
    {
        var ticks = interval.Ticks;
        if (ticks <= 0) return utc;
        return new DateTime(utc.Ticks - (utc.Ticks % ticks), DateTimeKind.Utc);
    }
}
