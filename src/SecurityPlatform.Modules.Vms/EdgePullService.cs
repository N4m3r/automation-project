using System.Diagnostics;
using System.Globalization;
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
/// Edge recording light (Profile G-inspired): detecta buracos na gravação
/// contínua e tenta puxar o trecho da câmera via RTSP de playback do fabricante.
///
/// Fabricantes:
/// - Hikvision: /Streaming/tracks/101?starttime=…&amp;endtime=…
/// - Dahua/Intelbras: playback em /cam/playback?…
/// - Demais: tenta URL de live (pode falhar — gap fica documentado em log)
/// </summary>
public class EdgePullService(
    IServiceScopeFactory scopes,
    IOptions<VmsOptions> options,
    ILogger<EdgePullService> log) : BackgroundService
{
    private readonly VmsOptions _opt = options.Value;
    public const string EdgePrefix = "edge_";
    public const string EdgeTrigger = "edge";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(90), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await RunAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Falha no edge pull");
            }
            await Task.Delay(TimeSpan.FromMinutes(3), ct);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<DriverRegistry>();
        var lease = scope.ServiceProvider.GetService<RecorderLeaseService>();

        var cameras = await db.Devices.AsNoTracking()
            .Where(d => d.Kind == DeviceKind.Camera
                     && d.EdgePullEnabled
                     && d.Recording == RecordingMode.Continuous)
            .ToListAsync(ct);

        var gap = TimeSpan.FromMinutes(Math.Max(_opt.EdgePullGapMinutes, 2));
        var agora = DateTime.UtcNow;

        foreach (var cam in cameras.Where(c => _opt.OwnsDevice(c.Id)))
        {
            if (lease is not null && _opt.HaEnabled && !await lease.TryAcquireAsync(cam.Id, ct))
                continue;

            var segs = await db.Recordings.AsNoTracking()
                .Where(r => r.DeviceId == cam.Id && r.StartedAt >= agora.AddDays(-2))
                .OrderBy(r => r.StartedAt)
                .ToListAsync(ct);

            if (segs.Count < 2) continue;

            for (var i = 0; i < segs.Count - 1; i++)
            {
                var fim = segs[i].EndedAt ?? segs[i].StartedAt;
                var prox = segs[i + 1].StartedAt;
                var buraco = prox - fim;
                if (buraco < gap) continue;

                // Evita puxar o mesmo buraco duas vezes (já indexado como edge).
                var jaTem = segs.Any(s =>
                    s.Trigger == EdgeTrigger && s.StartedAt >= fim && s.StartedAt <= prox);
                if (jaTem) continue;

                var pullFrom = fim.AddSeconds(1);
                var pullTo = prox.AddSeconds(-1);
                if (pullTo <= pullFrom) continue;

                log.LogInformation(
                    "Edge pull câmera {Id}: gap {Min:0} min de {From:o} a {To:o}",
                    cam.Id, buraco.TotalMinutes, pullFrom, pullTo);

                var rtsp = await BuildPlaybackUrlAsync(cam, registry, pullFrom, pullTo, ct);
                if (rtsp is null) continue;

                var ok = await PullSegmentAsync(cam.Id, rtsp, pullFrom, ct);
                if (ok)
                    log.LogInformation("Edge pull câmera {Id} concluído", cam.Id);
            }
        }
    }

    internal static async Task<string?> BuildPlaybackUrlAsync(
        Device cam, DriverRegistry registry, DateTime from, DateTime to, CancellationToken ct)
    {
        var driver = cam.Driver.ToLowerInvariant();
        var user = Uri.EscapeDataString(cam.Username ?? "");
        var pass = Uri.EscapeDataString(cam.Password ?? "");
        var cred = string.IsNullOrEmpty(cam.Username) ? "" : $"{user}:{pass}@";
        var start = from.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var end = to.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        if (driver is "hikvision" || cam.Name.Contains("hik", StringComparison.OrdinalIgnoreCase))
            return $"rtsp://{cred}{cam.Host}:554/Streaming/tracks/101?starttime={start}&endtime={end}";

        if (driver is "dahua" or "intelbras"
            || cam.Name.Contains("dahua", StringComparison.OrdinalIgnoreCase)
            || cam.Name.Contains("intelbras", StringComparison.OrdinalIgnoreCase))
        {
            // Formato comum Dahua/Intelbras playback
            var s = from.ToUniversalTime().ToString("yyyy_MM_dd_HH_mm_ss", CultureInfo.InvariantCulture);
            var e = to.ToUniversalTime().ToString("yyyy_MM_dd_HH_mm_ss", CultureInfo.InvariantCulture);
            return $"rtsp://{cred}{cam.Host}:554/cam/playback?channel=1&subtype=0&starttime={s}&endtime={e}";
        }

        // Fallback: tenta stream ao vivo (só preenche se a câmera ainda tiver buffer curto).
        try { return await registry.Resolve(cam).GetStreamUrlAsync(cam, ct); }
        catch { return null; }
    }

    private async Task<bool> PullSegmentAsync(int deviceId, string rtsp, DateTime startedAt, CancellationToken ct)
    {
        var dir = Path.Combine(_opt.StoragePath, deviceId.ToString());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir,
            $"{EdgePrefix}{startedAt:yyyyMMdd_HHmmss}.mp4");

        var args = string.Join(' ',
            "-nostdin -hide_banner -loglevel error -y",
            "-rtsp_transport tcp -timeout 8000000",
            $"-i \"{rtsp}\"",
            "-c copy -t 600 -movflags +faststart",
            $"\"{file}\"");

        try
        {
            using var p = Process.Start(new ProcessStartInfo(_opt.FfmpegPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            });
            if (p is null) return false;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(12));
            await p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);

            if (p.ExitCode != 0 || !File.Exists(file) || new FileInfo(file).Length < 1024)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { /* ignore */ }
                return false;
            }

            // Indexação fica a cargo do RetentionService (varre *.mp4).
            // Marca trigger renomeando com prefixo reconhecido no ParseStart.
            return true;
        }
        catch (Exception e)
        {
            log.LogDebug(e, "Edge pull FFmpeg falhou para câmera {Id}", deviceId);
            return false;
        }
    }
}
