using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Indexa no banco os segmentos que o FFmpeg fechou (para o playback lista-los)
/// e aplica a retencao — por prazo (LGPD: descarte automatico), por cota de
/// disco da camera e por cota global do volume.
///
/// Com <see cref="SystemSettings.EncryptRecordings"/>, segmentos fechados são
/// cifrados (AES-GCM → .mp4.enc) antes de indexar.
/// </summary>
public class RetentionService(
    IServiceScopeFactory scopes,
    IOptions<VmsOptions> options,
    RecordingNormalizer normalizer,
    VmsMetrics metrics,
    ILogger<RetentionService> log) : BackgroundService
{
    private readonly VmsOptions _opt = options.Value;
    private readonly RecordingNormalizer _normalizer = normalizer;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunAsync(ct);
            }
            catch (Exception e)
            {
                log.LogError(e, "Falha na rotina de retencao");
            }
            // Indexação mais frequente para cifrar/listar segmentos logo após fechar.
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var crypto = scope.ServiceProvider.GetRequiredService<RecordingCrypto>();

        var settings = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var encrypt = settings?.EncryptRecordings == true;

        var cameras = await db.Devices
            .Where(d => d.Kind == DeviceKind.Camera)
            .AsNoTracking().ToListAsync(ct);

        // Indexa em todos os volumes (primary + extras).
        var volumes = new List<string> { _opt.StoragePath };
        if (_opt.StorageVolumes is { Length: > 0 })
            volumes.AddRange(_opt.StorageVolumes.Where(v => !string.IsNullOrWhiteSpace(v)));

        foreach (var cam in cameras.Where(c => _opt.OwnsDevice(c.Id)))
        {
            foreach (var vol in volumes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var dir = Path.Combine(vol, cam.Id.ToString());
                if (!Directory.Exists(dir)) continue;
                await IndexNewSegmentsAsync(db, cam, dir, encrypt, crypto, ct);
            }

            await ApplyBookmarkProtectionAsync(db, cam, ct);
            await PurgePreBufferAsync(db, cam, ct);
            await PurgeAsync(db, cam, ct);
        }

        await db.SaveChangesAsync(ct);
        await EnforceGlobalQuotaAsync(db, ct);
    }

    private async Task IndexNewSegmentsAsync(
        PlatformDbContext db, Device cam, string dir,
        bool encrypt, RecordingCrypto crypto, CancellationToken ct)
    {
        var conhecidos = (await db.Recordings
            .Where(r => r.DeviceId == cam.Id)
            .Select(r => r.Path)
            .ToListAsync(ct)).ToHashSet();

        // MP4 claro (recém fechados) e .enc já cifrados (reindex / restart).
        // Ignora: sidecars *.browser.mp4, temps de conversão (*.tmp_*.mp4 / *.norm_*.mp4).
        var mp4 = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.mp4")
                .Where(IsSegmentoGravacao)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        // O mais novo ainda pode estar aberto pelo FFmpeg (sem moov).
        var mp4Fechados = mp4.Count <= 1 ? Array.Empty<string>() : mp4.SkipLast(1).ToArray();

        var encs = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.mp4.enc")
            : [];

        foreach (var file in mp4Fechados)
        {
            if (conhecidos.Contains(file) || conhecidos.Contains(file + RecordingCrypto.Extension))
                continue;

            // Ignora lixo (segmento abortado / FFmpeg recém-criado / sem moov).
            var info = new FileInfo(file);
            if (!info.Exists || info.Length < RecordingNormalizer.MinPlayableBytes)
            {
                log.LogDebug("Segmento ignorado (muito pequeno): {File} ({Bytes} B)", file, info.Exists ? info.Length : 0);
                continue;
            }
            if (!HasMoovAtom(file))
            {
                log.LogDebug("Segmento ignorado (sem moov — ainda gravando ou incompleto): {File}", file);
                continue;
            }

            // HEVC/fMP4 → H.264 progressivo para o player web tocar de primeira.
            try
            {
                await _normalizer.NormalizeInPlaceAsync(file, ct);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Normalização do segmento falhou (indexa mesmo assim): {File}", file);
            }

            // Sidecar de cache de playback não é gravação.
            if (file.EndsWith(".browser.mp4", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = file;
            var encrypted = false;
            if (encrypt)
            {
                try
                {
                    path = crypto.EncryptFile(file);
                    encrypted = true;
                }
                catch (Exception e)
                {
                    log.LogError(e, "Falha ao cifrar segmento {File}", file);
                    path = file;
                }
            }

            // Sempre grava path absoluto — independente do CWD futuro.
            var full = Path.GetFullPath(path);
            AddRecording(db, cam, full, encrypted);
            conhecidos.Add(full);
            try
            {
                metrics.IncSegment(new FileInfo(full).Length);
            }
            catch { /* */ }
        }

        foreach (var file in encs)
        {
            if (conhecidos.Contains(file)) continue;
            AddRecording(db, cam, Path.GetFullPath(file), encrypted: true);
        }
    }

    /// <summary>Só segmentos reais de câmera (c_/e_/p_/edge_), não temps nem cache browser.</summary>
    internal static bool IsSegmentoGravacao(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Contains(".tmp_", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Contains(".norm_", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.EndsWith(".browser.mp4", StringComparison.OrdinalIgnoreCase)) return false;
        if (!name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return false;
        return name.StartsWith(RecorderService.ContinuousPrefix, StringComparison.Ordinal)
            || name.StartsWith(RecorderService.EventPrefix, StringComparison.Ordinal)
            || name.StartsWith(RecorderService.PreEventPrefix, StringComparison.Ordinal)
            || name.StartsWith(EdgePullService.EdgePrefix, StringComparison.Ordinal);
    }

    private static void AddRecording(PlatformDbContext db, Device cam, string file, bool encrypted)
    {
        var info = new FileInfo(file);
        if (!info.Exists) return;

        // Segmento ainda aberto / sem moov: não indexar (impossível reproduzir).
        if (!HasMoovAtom(file))
            return;

        var nome = Path.GetFileNameWithoutExtension(file);
        if (nome.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            nome = Path.GetFileNameWithoutExtension(nome); // c_...mp4 de .mp4.enc

        var inicio = ParseStart(nome) ?? info.CreationTimeUtc;
        var full = Path.GetFullPath(file);

        db.Recordings.Add(new Recording
        {
            TenantId = cam.TenantId,
            DeviceId = cam.Id,
            Path = full,
            SizeBytes = info.Length,
            StartedAt = inicio,
            EndedAt = info.LastWriteTimeUtc,
            Trigger = nome.StartsWith(EdgePullService.EdgePrefix, StringComparison.Ordinal)
                ? EdgePullService.EdgeTrigger
                : nome.StartsWith(RecorderService.EventPrefix, StringComparison.Ordinal)
                    ? "event"
                    : nome.StartsWith(RecorderService.PreEventPrefix, StringComparison.Ordinal)
                        ? RecorderService.PreEventTrigger
                        : "continuous",
            Encrypted = encrypted || RecordingCrypto.IsEncryptedPath(file)
        });
    }

    /// <summary>MP4 sem 'moov' (segmento em gravação ou kill do FFmpeg) não é playable.</summary>
    internal static bool HasMoovAtom(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            // Procura no início e no fim (moov costuma estar no fim em segment muxer).
            var headLen = (int)Math.Min(fs.Length, 256 * 1024);
            var buf = new byte[headLen];
            _ = fs.Read(buf, 0, headLen);
            if (ContainsFourCc(buf, "moov")) return true;

            if (fs.Length > headLen)
            {
                var tailLen = (int)Math.Min(fs.Length, 512 * 1024);
                fs.Seek(-tailLen, SeekOrigin.End);
                buf = new byte[tailLen];
                _ = fs.Read(buf, 0, tailLen);
                if (ContainsFourCc(buf, "moov")) return true;
            }
        }
        catch (IOException) { return false; }
        return false;
    }

    private static bool ContainsFourCc(byte[] buf, string tag)
    {
        if (tag.Length != 4 || buf.Length < 4) return false;
        var a = (byte)tag[0]; var b = (byte)tag[1]; var c = (byte)tag[2]; var d = (byte)tag[3];
        for (var i = 0; i < buf.Length - 3; i++)
            if (buf[i] == a && buf[i + 1] == b && buf[i + 2] == c && buf[i + 3] == d)
                return true;
        return false;
    }

    internal static DateTime? ParseStart(string fileName)
    {
        var texto = fileName;
        foreach (var prefixo in new[]
                 {
                     EdgePullService.EdgePrefix,
                     RecorderService.EventPrefix,
                     RecorderService.PreEventPrefix,
                     RecorderService.ContinuousPrefix
                 })
            if (texto.StartsWith(prefixo, StringComparison.Ordinal))
                texto = texto[prefixo.Length..];

        return DateTime.TryParseExact(texto, "yyyyMMdd_HHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var quando)
            ? quando
            : null;
    }

    private static async Task ApplyBookmarkProtectionAsync(
        PlatformDbContext db, Device cam, CancellationToken ct)
    {
        var marcas = await db.Bookmarks.AsNoTracking()
            .Where(b => b.DeviceId == cam.Id).ToListAsync(ct);

        var gravacoes = await db.Recordings.Where(r => r.DeviceId == cam.Id).ToListAsync(ct);

        foreach (var r in gravacoes)
        {
            var fim = r.EndedAt ?? r.StartedAt;
            var protegida = marcas.Any(b => b.StartedAt <= fim && b.EndedAt >= r.StartedAt);
            if (r.Protected != protegida) r.Protected = protegida;
        }
    }

    /// <summary>
    /// Ring buffer: descarta pré-buffer mais antigo que PreEventSeconds
    /// (exceto protegidos / já promovidos a event).
    /// </summary>
    private async Task PurgePreBufferAsync(PlatformDbContext db, Device cam, CancellationToken ct)
    {
        var pre = _opt.EffectivePreEventSeconds(cam);
        if (pre <= 0 || cam.Recording != RecordingMode.OnEvent) return;

        var corte = DateTime.UtcNow.AddSeconds(-pre - 10);
        var velhos = await db.Recordings
            .Where(r => r.DeviceId == cam.Id
                        && !r.Protected
                        && r.Trigger == RecorderService.PreEventTrigger
                        && r.StartedAt < corte)
            .ToListAsync(ct);

        foreach (var r in velhos)
        {
            if (!Apagar(r.Path)) continue;
            LogPurge(db, cam, r, "prebuffer");
            db.Recordings.Remove(r);
            metrics.IncPurge();
        }
    }

    private async Task PurgeAsync(PlatformDbContext db, Device cam, CancellationToken ct)
    {
        var gravacoes = await db.Recordings
            .Where(r => r.DeviceId == cam.Id && !r.Protected)
            .OrderBy(r => r.StartedAt)
            .ToListAsync(ct);

        var corte = DateTime.UtcNow.AddDays(-cam.RetentionDays);
        var expiradas = gravacoes.Where(r => (r.EndedAt ?? r.StartedAt) < corte).ToList();

        foreach (var r in expiradas)
        {
            if (!Apagar(r.Path)) continue;
            LogPurge(db, cam, r, "retention_days");
            db.Recordings.Remove(r);
            metrics.IncPurge();
        }

        if (cam.MaxStorageGb > 0)
        {
            var teto = (long)cam.MaxStorageGb * 1024 * 1024 * 1024;
            var restantes = gravacoes.Except(expiradas).ToList();
            var total = restantes.Sum(r => r.SizeBytes);

            foreach (var r in restantes)
            {
                if (total <= teto) break;
                if (!Apagar(r.Path)) continue;

                LogPurge(db, cam, r, "camera_quota");
                db.Recordings.Remove(r);
                total -= r.SizeBytes;
                metrics.IncPurge();
                log.LogInformation("Cota da camera {Id}: removido {File}", cam.Id, r.Path);
            }
        }
    }

    private async Task EnforceGlobalQuotaAsync(PlatformDbContext db, CancellationToken ct)
    {
        if (_opt.MaxStorageGb <= 0) return;

        var teto = (long)_opt.MaxStorageGb * 1024 * 1024 * 1024;
        var total = await db.Recordings.SumAsync(r => r.SizeBytes, ct);
        if (total <= teto) return;

        log.LogWarning("Cota global excedida: {Usado} GB de {Teto} GB — liberando espaco",
            total / 1024 / 1024 / 1024, _opt.MaxStorageGb);

        var candidatas = await db.Recordings
            .Where(r => !r.Protected)
            .OrderBy(r => r.StartedAt)
            .ToListAsync(ct);

        foreach (var r in candidatas)
        {
            if (total <= teto) break;
            if (!Apagar(r.Path)) continue;

            db.RetentionPurgeLogs.Add(new RetentionPurgeLog
            {
                TenantId = r.TenantId,
                DeviceId = r.DeviceId,
                RecordingId = r.Id,
                Path = r.Path,
                SizeBytes = r.SizeBytes,
                StartedAt = r.StartedAt,
                Reason = "global_quota"
            });
            db.Recordings.Remove(r);
            total -= r.SizeBytes;
            metrics.IncPurge();
        }

        await db.SaveChangesAsync(ct);

        if (total > teto)
            log.LogError("Cota global ainda excedida apos a limpeza: so restam gravacoes protegidas.");
    }

    private static void LogPurge(PlatformDbContext db, Device cam, Recording r, string reason)
    {
        db.RetentionPurgeLogs.Add(new RetentionPurgeLog
        {
            TenantId = cam.TenantId,
            DeviceId = cam.Id,
            RecordingId = r.Id,
            Path = r.Path,
            SizeBytes = r.SizeBytes,
            StartedAt = r.StartedAt,
            Reason = reason
        });
    }

    private bool Apagar(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            var sig = path + ".sig";
            if (File.Exists(sig)) File.Delete(sig);
            var plainCache = path + ".plain.cache";
            if (File.Exists(plainCache)) File.Delete(plainCache);
            try
            {
                var browser = RecordingNormalizer.BrowserCachePath(
                    RecordingCrypto.IsEncryptedPath(path)
                        ? path[..^RecordingCrypto.Extension.Length]
                        : path);
                if (File.Exists(browser)) File.Delete(browser);
            }
            catch (IOException) { }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException e)
        {
            log.LogWarning(e, "Sem permissao para remover {File}", path);
            return false;
        }
    }
}
