using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Busca facial leve: enroll com fingerprint, match na galeria,
/// varredura de snapshots das câmeras e consulta de eventos face.
/// Exige licença <see cref="License.AnalyticsFacial"/>.
/// </summary>
public sealed class FaceSearchService(
    PlatformDbContext db,
    FaceFingerprint fingerprint,
    DriverRegistry drivers,
    MediaGateway media,
    RecordingExporter exporter,
    IOptions<VmsOptions> options,
    ILogger<FaceSearchService> log)
{
    private readonly VmsOptions _opt = options.Value;

    public async Task<bool> IsLicensedAsync(CancellationToken ct = default)
    {
        var lic = await db.Licenses.AsNoTracking()
            .OrderByDescending(l => l.InstalledAt)
            .FirstOrDefaultAsync(ct);
        return lic?.AnalyticsFacial == true;
    }

    public async Task<object> StatusAsync(int? tenantId = null, CancellationToken ct = default)
    {
        var lic = await db.Licenses.AsNoTracking()
            .OrderByDescending(l => l.InstalledAt)
            .FirstOrDefaultAsync(ct);
        var q = db.FaceGalleryEntries.AsNoTracking();
        if (tenantId is int tid) q = q.Where(f => f.TenantId == tid);
        var galleryTotal = await q.CountAsync(ct);
        return new
        {
            licensed = lic?.AnalyticsFacial == true,
            edition = lic?.Edition.ToString() ?? "Express",
            engine = "visual-fingerprint-v1",
            featureDim = FaceFingerprint.FeatureLen,
            defaultThreshold = FaceFingerprint.DefaultThreshold,
            galleryTotal,
            modes = new[] { "gallery", "live", "events", "externalId" },
            note = "Comparação por fingerprint visual leve (FFmpeg 64×64). " +
                   "Câmeras com faceId ISAPI/VAPIX usam match por ID externo."
        };
    }

    public string FacesRoot()
    {
        // Irmão de recordings: ./data/faces
        var storage = Path.GetFullPath(_opt.StoragePath);
        var dataRoot = Directory.GetParent(storage)?.FullName
                       ?? Path.GetDirectoryName(storage)
                       ?? storage;
        var root = Path.Combine(dataRoot, "faces");
        Directory.CreateDirectory(root);
        return root;
    }

    public async Task<(FaceGalleryEntry? Entry, string? Error)> EnrollAsync(
        int tenantId,
        string name,
        string? externalFaceId,
        string? notes,
        string? listType,
        byte[]? imageBytes,
        float[]? precomputedEmbedding,
        CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return (null, "Nome obrigatório.");

        float[]? emb = precomputedEmbedding;
        if (emb is null && imageBytes is { Length: > 0 })
            emb = await fingerprint.FromImageAsync(imageBytes, ct);

        var list = NormalizeList(listType);
        var entry = new FaceGalleryEntry
        {
            TenantId = tenantId,
            Name = name,
            ExternalFaceId = (externalFaceId ?? "").Trim(),
            Notes = (notes ?? "").Trim(),
            ListType = list,
            Active = true,
            EmbeddingJson = emb is null ? "" : FaceFingerprint.Encode(emb),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.FaceGalleryEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        if (imageBytes is { Length: > 0 })
        {
            try
            {
                var rel = await SavePhotoAsync(tenantId, entry.Id, imageBytes, ct);
                entry.PhotoPath = rel;
                entry.PhotoUrl = $"/api/vms/faces/{entry.Id}/photo";
                await db.SaveChangesAsync(ct);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Falha ao gravar foto facial {Id}", entry.Id);
            }
        }

        return (entry, null);
    }

    public async Task<(FaceGalleryEntry? Entry, string? Error)> UpdateAsync(
        int tenantId, int id,
        string? name, string? externalFaceId, string? notes, string? listType, bool? active,
        byte[]? imageBytes, float[]? precomputedEmbedding,
        CancellationToken ct = default)
    {
        var entry = await db.FaceGalleryEntries
            .FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId, ct);
        if (entry is null) return (null, "not_found");

        if (!string.IsNullOrWhiteSpace(name)) entry.Name = name.Trim();
        if (externalFaceId is not null) entry.ExternalFaceId = externalFaceId.Trim();
        if (notes is not null) entry.Notes = notes.Trim();
        if (!string.IsNullOrWhiteSpace(listType)) entry.ListType = NormalizeList(listType);
        if (active is not null) entry.Active = active.Value;

        float[]? emb = precomputedEmbedding;
        if (emb is null && imageBytes is { Length: > 0 })
            emb = await fingerprint.FromImageAsync(imageBytes, ct);
        if (emb is not null)
            entry.EmbeddingJson = FaceFingerprint.Encode(emb);

        if (imageBytes is { Length: > 0 })
        {
            var rel = await SavePhotoAsync(tenantId, entry.Id, imageBytes, ct);
            entry.PhotoPath = rel;
            entry.PhotoUrl = $"/api/vms/faces/{entry.Id}/photo";
        }

        entry.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return (entry, null);
    }

    public async Task<IReadOnlyList<FaceGalleryHit>> SearchGalleryAsync(
        int tenantId,
        float[] probe,
        float threshold,
        int take,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 50);
        threshold = Math.Clamp(threshold, 0.1f, 0.99f);

        var rows = await db.FaceGalleryEntries.AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.Active && f.EmbeddingJson != "")
            .Select(f => new
            {
                f.Id, f.Name, f.ExternalFaceId, f.ListType, f.PhotoUrl, f.Notes, f.EmbeddingJson
            })
            .ToListAsync(ct);

        var hits = new List<FaceGalleryHit>(rows.Count);
        foreach (var r in rows)
        {
            var emb = FaceFingerprint.Decode(r.EmbeddingJson);
            if (emb is null) continue;
            var score = FaceFingerprint.Similarity(probe, emb);
            if (score >= threshold)
                hits.Add(new FaceGalleryHit(
                    r.Id, r.Name, r.ExternalFaceId, r.ListType, r.PhotoUrl, score, r.Notes));
        }

        return hits.OrderByDescending(h => h.Score).Take(take).ToList();
    }

    public async Task<IReadOnlyList<FaceCameraHit>> SearchLiveCamerasAsync(
        int tenantId,
        IReadOnlyCollection<int> visibleCameraIds,
        float[] probe,
        float threshold,
        IReadOnlyList<int>? cameraIds,
        int maxCameras,
        CancellationToken ct = default)
    {
        threshold = Math.Clamp(threshold, 0.1f, 0.99f);
        maxCameras = Math.Clamp(maxCameras, 1, 40);

        var q = db.Devices.AsNoTracking()
            .Where(d => d.TenantId == tenantId
                        && d.Kind == DeviceKind.Camera
                        && visibleCameraIds.Contains(d.Id));
        if (cameraIds is { Count: > 0 })
        {
            var set = cameraIds.ToHashSet();
            q = q.Where(d => set.Contains(d.Id));
        }

        var cams = await q.OrderBy(d => d.Name).Take(maxCameras)
            .Select(d => new { d.Id, d.Name })
            .ToListAsync(ct);

        var results = new FaceCameraHit[cams.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, cams.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (i, token) =>
            {
                var cam = cams[i];
                try
                {
                    var jpeg = await GrabSnapshotAsync(cam.Id, token);
                    if (jpeg is null)
                    {
                        results[i] = new FaceCameraHit(cam.Id, cam.Name, 0, false, "snapshot indisponível");
                        return;
                    }

                    var emb = await fingerprint.FromImageAsync(jpeg, token);
                    if (emb is null)
                    {
                        results[i] = new FaceCameraHit(cam.Id, cam.Name, 0, false, "fingerprint falhou");
                        return;
                    }

                    var score = FaceFingerprint.Similarity(probe, emb);
                    results[i] = new FaceCameraHit(cam.Id, cam.Name, score, true);
                }
                catch (Exception e)
                {
                    log.LogDebug(e, "Face live scan cam {Id}", cam.Id);
                    results[i] = new FaceCameraHit(cam.Id, cam.Name, 0, false, e.Message);
                }
            });

        return results
            .Where(r => r is not null)
            .OrderByDescending(r => r.Score)
            .ToList()!;
    }

    public async Task<IReadOnlyList<FaceEventHit>> SearchEventsAsync(
        int tenantId,
        IReadOnlyCollection<int> visibleCameraIds,
        string? personName,
        string? faceId,
        int? deviceId,
        DateTime? from,
        DateTime? to,
        float[]? probeEmbedding,
        float threshold,
        int take,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);
        var inicio = from ?? DateTime.UtcNow.AddDays(-7);
        var fim = to ?? DateTime.UtcNow;

        var q = db.Events.AsNoTracking()
            .Where(e => e.TenantId == tenantId
                        && e.CreatedAt >= inicio && e.CreatedAt <= fim
                        && (e.DeviceId == null || visibleCameraIds.Contains(e.DeviceId.Value))
                        && (e.Type.Contains("face") || e.Type == "facedetection"));

        if (deviceId is not null) q = q.Where(e => e.DeviceId == deviceId);

        // Pré-filtro textual quando informado
        var list = await q.OrderByDescending(e => e.CreatedAt).Take(Math.Min(take * 5, 800))
            .ToListAsync(ct);

        var deviceNames = await db.Devices.AsNoTracking()
            .Where(d => d.Kind == DeviceKind.Camera && visibleCameraIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        // Se a probe bateu em alguém da galeria, usa nome/faceId como filtro extra
        HashSet<string>? galleryNames = null;
        HashSet<string>? galleryFaceIds = null;
        if (probeEmbedding is not null)
        {
            var galleryHits = await SearchGalleryAsync(tenantId, probeEmbedding, threshold, 10, ct);
            if (galleryHits.Count > 0)
            {
                galleryNames = galleryHits.Select(h => h.Name)
                    .Where(n => n.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                galleryFaceIds = galleryHits.Select(h => h.ExternalFaceId)
                    .Where(n => n.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }

        var nameFilter = (personName ?? "").Trim();
        var faceFilter = (faceId ?? "").Trim();

        var hits = new List<FaceEventHit>();
        foreach (var e in list)
        {
            var meta = EventMetadata.TryParseFromPayload(e.Payload);
            var fid = meta?.FaceId?.Trim() ?? "";
            var pname = meta?.PersonName?.Trim() ?? "";

            if (nameFilter.Length > 0
                && !pname.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)
                && !(e.Payload?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                continue;
            if (faceFilter.Length > 0
                && !fid.Equals(faceFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            float? score = null;
            if (galleryNames is not null || galleryFaceIds is not null)
            {
                var byName = galleryNames is not null && pname.Length > 0 && galleryNames.Contains(pname);
                var byId = galleryFaceIds is not null && fid.Length > 0 && galleryFaceIds.Contains(fid);
                if (!byName && !byId && nameFilter.Length == 0 && faceFilter.Length == 0)
                {
                    // Com probe: só eventos ligados a alguém da galeria, a menos que filtro textual
                    // (eventos sem ID não têm score visual — não armazenamos crop)
                    continue;
                }
                if (byName || byId)
                {
                    // Score herdado do melhor match de galeria com mesmo nome/id
                    score = 0.9f;
                }
            }

            string? devName = null;
            if (e.DeviceId is int did && deviceNames.TryGetValue(did, out var dn))
                devName = dn;

            hits.Add(new FaceEventHit(
                e.Id, e.DeviceId, devName, e.Type, e.CreatedAt,
                string.IsNullOrEmpty(fid) ? null : fid,
                string.IsNullOrEmpty(pname) ? null : pname,
                score, e.Severity));

            if (hits.Count >= take) break;
        }

        return hits;
    }

    public async Task<byte[]?> LoadPhotoAsync(int tenantId, int id, CancellationToken ct = default)
    {
        var entry = await db.FaceGalleryEntries.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId, ct);
        if (entry is null) return null;

        if (!string.IsNullOrWhiteSpace(entry.PhotoPath))
        {
            var full = ResolvePhotoPath(entry.PhotoPath);
            if (full is not null && File.Exists(full))
                return await File.ReadAllBytesAsync(full, ct);
        }

        // data URL embutida
        if (entry.PhotoUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return FaceFingerprint.DecodeImagePayload(entry.PhotoUrl);

        return null;
    }

    public async Task DeleteAsync(int tenantId, int id, CancellationToken ct = default)
    {
        var entry = await db.FaceGalleryEntries
            .FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId, ct);
        if (entry is null) return;

        if (!string.IsNullOrWhiteSpace(entry.PhotoPath))
        {
            var full = ResolvePhotoPath(entry.PhotoPath);
            try { if (full is not null && File.Exists(full)) File.Delete(full); }
            catch (IOException) { }
        }

        db.FaceGalleryEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
    }

    private async Task<string> SavePhotoAsync(int tenantId, int id, byte[] bytes, CancellationToken ct)
    {
        var dir = Path.Combine(FacesRoot(), tenantId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{id}.jpg");
        await File.WriteAllBytesAsync(file, bytes, ct);
        // relativo a FacesRoot
        return Path.Combine(tenantId.ToString(CultureInfo.InvariantCulture), $"{id}.jpg")
            .Replace('\\', '/');
    }

    private string? ResolvePhotoPath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var root = Path.GetFullPath(FacesRoot());
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return full;
    }

    private async Task<byte[]?> GrabSnapshotAsync(int cameraId, CancellationToken ct)
    {
        var cam = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == cameraId, ct);
        if (cam is null) return null;

        var driver = drivers.Resolve(cam);
        var nativo = await driver.CommandAsync(cam, "snapshot", null, ct);
        if (nativo.Ok && nativo.Data?.TryGetValue("base64", out var imagem) == true)
        {
            try { return Convert.FromBase64String(imagem); }
            catch { /* fallback */ }
        }

        string? rtsp = null;
        try
        {
            var baseUrl = await driver.GetStreamUrlAsync(cam, ct);
            await media.RegisterAsync(cam.Id, baseUrl, substream: false, ct);
            if (await media.IsReadyAsync(cam.Id, substream: false, ct))
                rtsp = media.LocalRtspUrl(cam.Id, substream: false);
        }
        catch { /* tenta direto */ }

        rtsp ??= await driver.GetStreamUrlAsync(cam, ct);
        return await exporter.GrabFrameAsync(rtsp, ct);
    }

    private static string NormalizeList(string? listType)
    {
        var t = (listType ?? "watch").Trim().ToLowerInvariant();
        return t is "allow" or "deny" or "watch" ? t : "watch";
    }
}
