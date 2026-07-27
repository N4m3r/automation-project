using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Move gravações antigas (não protegidas) para pasta fria / NAS
/// (<see cref="SystemSettings.ArchivePath"/> + <see cref="SystemSettings.ArchiveAfterDays"/>).
/// </summary>
public sealed class ArchiveService(
    IServiceScopeFactory scopes,
    IOptions<VmsOptions> options,
    VmsMetrics metrics,
    ILogger<ArchiveService> log) : BackgroundService
{
    private readonly VmsOptions _opt = options.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await RunAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Falha no archive de gravações");
            }
            await Task.Delay(TimeSpan.FromMinutes(30), ct);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var settings = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is null) return;

        var archiveRoot = (settings.ArchivePath ?? "").Trim();
        var afterDays = settings.ArchiveAfterDays;
        if (string.IsNullOrWhiteSpace(archiveRoot) || afterDays <= 0) return;

        archiveRoot = Path.IsPathRooted(archiveRoot)
            ? Path.GetFullPath(archiveRoot)
            : Path.GetFullPath(Path.Combine(_opt.StoragePath, "..", archiveRoot));
        Directory.CreateDirectory(archiveRoot);

        var corte = DateTime.UtcNow.AddDays(-afterDays);
        var candidatos = await db.Recordings
            .Where(r => !r.Protected && r.StartedAt < corte)
            .OrderBy(r => r.StartedAt)
            .Take(200)
            .ToListAsync(ct);

        foreach (var r in candidatos)
        {
            ct.ThrowIfCancellationRequested();
            if (!_opt.OwnsDevice(r.DeviceId)) continue;

            var src = StoragePaths.ResolveExisting(r.Path, _opt.StoragePath);
            if (src is null || !File.Exists(src)) continue;

            // Já no archive?
            if (src.StartsWith(archiveRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            var rel = $"{r.DeviceId}/{Path.GetFileName(src)}";
            var dest = Path.Combine(archiveRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(src, dest);
                r.Path = dest;
                metrics.IncArchive();
                log.LogInformation("Arquivado segmento {Id} → {Dest}", r.Id, dest);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Falha ao arquivar {Path}", src);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
