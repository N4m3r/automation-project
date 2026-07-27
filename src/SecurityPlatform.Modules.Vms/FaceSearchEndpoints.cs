using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// API do módulo de reconhecimento facial (licença <c>AnalyticsFacial</c>).
/// Galeria, enroll com imagem, busca por similaridade e varredura em câmeras.
/// </summary>
public static class FaceSearchEndpoints
{
    public static void MapFaceSearch(this RouteGroupBuilder g)
    {
        g.MapGet("/faces/status", async (HttpContext ctx, FaceSearchService faces, CancellationToken ct) =>
            Results.Ok(await faces.StatusAsync(ctx.User.TenantId(), ct)));

        g.MapGet("/faces", async (HttpContext ctx, PlatformDbContext db, FaceSearchService faces) =>
        {
            if (!await faces.IsLicensedAsync())
                return FaceLicenseRequired();

            var list = await db.FaceGalleryEntries.AsNoTracking()
                .Where(f => f.TenantId == ctx.User.TenantId())
                .OrderBy(f => f.Name)
                .Select(f => new
                {
                    f.Id, f.Name, f.ExternalFaceId, f.PhotoUrl, f.PhotoPath,
                    f.ListType, f.Notes, f.Active, f.CreatedAt, f.UpdatedAt,
                    hasEmbedding = f.EmbeddingJson != ""
                })
                .ToListAsync();
            return Results.Ok(list);
        });

        g.MapGet("/faces/{id:int}/photo", async (
            int id, HttpContext ctx, FaceSearchService faces, CancellationToken ct) =>
        {
            if (!await faces.IsLicensedAsync(ct))
                return FaceLicenseRequired();

            var bytes = await faces.LoadPhotoAsync(ctx.User.TenantId(), id, ct);
            return bytes is null
                ? Results.NotFound()
                : Results.File(bytes, "image/jpeg");
        });

        g.MapPost("/faces", async (
            FaceEnrollInput input, HttpContext ctx, FaceSearchService faces,
            AuditService audit, CancellationToken ct) =>
        {
            if (!await faces.IsLicensedAsync(ct))
                return FaceLicenseRequired();
            if (!ctx.User.IsInRole("admin") && !await HasConfig(ctx))
                return Results.Forbid();

            var image = FaceFingerprint.DecodeImagePayload(input.ImageBase64)
                        ?? FaceFingerprint.DecodeImagePayload(input.PhotoUrl);

            var (entry, err) = await faces.EnrollAsync(
                ctx.User.TenantId(),
                input.Name ?? "",
                input.ExternalFaceId,
                input.Notes,
                input.ListType,
                image,
                input.Embedding,
                ct);

            if (entry is null)
                return Results.BadRequest(new { error = err ?? "Falha no cadastro." });

            // PhotoUrl externo (sem bytes) — grava referência
            if (image is null && !string.IsNullOrWhiteSpace(input.PhotoUrl)
                && !input.PhotoUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                entry.PhotoUrl = input.PhotoUrl.Trim();
                await ctx.RequestServices.GetRequiredService<PlatformDbContext>().SaveChangesAsync(ct);
            }

            await audit.WriteAsync(ctx, "face.gallery.add", "face", entry.Id.ToString(), true,
                detail: entry.Name);
            return Results.Created($"/api/vms/faces/{entry.Id}", ToDto(entry));
        });

        g.MapPost("/faces/enroll", async (HttpContext ctx, FaceSearchService faces,
            AuditService audit, CancellationToken ct) =>
        {
            if (!await faces.IsLicensedAsync(ct))
                return FaceLicenseRequired();
            if (!ctx.User.IsInRole("admin") && !await HasConfig(ctx))
                return Results.Forbid();

            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "Use multipart/form-data ou POST /faces com JSON." });

            var form = await ctx.Request.ReadFormAsync(ct);
            var name = form["name"].ToString();
            var externalFaceId = form["externalFaceId"].ToString();
            var notes = form["notes"].ToString();
            var listType = form["listType"].ToString();

            byte[]? image = null;
            var file = form.Files.GetFile("photo") ?? form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is { Length: > 0 })
            {
                await using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                image = ms.ToArray();
            }
            else if (!string.IsNullOrWhiteSpace(form["imageBase64"]))
            {
                image = FaceFingerprint.DecodeImagePayload(form["imageBase64"]);
            }

            var (entry, err) = await faces.EnrollAsync(
                ctx.User.TenantId(), name, externalFaceId, notes, listType, image, null, ct);
            if (entry is null)
                return Results.BadRequest(new { error = err ?? "Falha no enroll." });

            await audit.WriteAsync(ctx, "face.gallery.enroll", "face", entry.Id.ToString(), true,
                detail: entry.Name);
            return Results.Created($"/api/vms/faces/{entry.Id}", ToDto(entry));
        });

        g.MapPut("/faces/{id:int}", async (
            int id, FaceEnrollInput input, HttpContext ctx, FaceSearchService faces,
            AuditService audit, CancellationToken ct) =>
        {
            if (!await faces.IsLicensedAsync(ct))
                return FaceLicenseRequired();
            if (!ctx.User.IsInRole("admin") && !await HasConfig(ctx))
                return Results.Forbid();

            var image = FaceFingerprint.DecodeImagePayload(input.ImageBase64);
            var (entry, err) = await faces.UpdateAsync(
                ctx.User.TenantId(), id,
                input.Name, input.ExternalFaceId, input.Notes, input.ListType, input.Active,
                image, input.Embedding, ct);

            if (err == "not_found") return Results.NotFound();
            if (entry is null) return Results.BadRequest(new { error = err });

            await audit.WriteAsync(ctx, "face.gallery.update", "face", id.ToString(), true);
            return Results.Ok(ToDto(entry));
        });

        g.MapDelete("/faces/{id:int}", async (
            int id, HttpContext ctx, FaceSearchService faces, PlatformDbContext db,
            AuditService audit, CancellationToken ct) =>
        {
            if (!await faces.IsLicensedAsync(ct))
                return FaceLicenseRequired();
            if (!ctx.User.IsInRole("admin") && !await HasConfig(ctx))
                return Results.Forbid();

            var exists = await db.FaceGalleryEntries
                .AnyAsync(f => f.Id == id && f.TenantId == ctx.User.TenantId(), ct);
            if (!exists) return Results.NotFound();

            await faces.DeleteAsync(ctx.User.TenantId(), id, ct);
            await audit.WriteAsync(ctx, "face.gallery.delete", "face", id.ToString(), true);
            return Results.NoContent();
        });

        g.MapPost("/faces/match", async (
            FaceMatchByIdInput input, HttpContext ctx, PlatformDbContext db, FaceSearchService faces) =>
        {
            if (!await faces.IsLicensedAsync())
                return FaceLicenseRequired();

            var faceId = (input.ExternalFaceId ?? "").Trim();
            if (faceId.Length == 0)
                return Results.BadRequest(new { error = "ExternalFaceId obrigatório." });

            var hit = await db.FaceGalleryEntries.AsNoTracking()
                .FirstOrDefaultAsync(f => f.TenantId == ctx.User.TenantId()
                    && f.Active && f.ExternalFaceId == faceId);
            return hit is null
                ? Results.Ok(new { matched = false })
                : Results.Ok(new
                {
                    matched = true,
                    hit.Id, hit.Name, hit.PhotoUrl, hit.Notes, hit.ListType
                });
        });

        g.MapPost("/faces/search", async (
            FaceSearchInput input, HttpContext ctx, FaceSearchService faces, FaceFingerprint fp,
            PermissionService perms, AuditService audit, CancellationToken ct) =>
        {
            if (!await faces.IsLicensedAsync(ct))
                return FaceLicenseRequired();

            var probe = await ResolveImageEmbedding(input.ImageBase64, input.Embedding, fp, ct);
            var mode = (input.Mode ?? "all").Trim().ToLowerInvariant();
            var threshold = input.Threshold ?? FaceFingerprint.DefaultThreshold;
            var take = input.Take ?? 20;
            var tenant = ctx.User.TenantId();
            var visible = await perms.VisibleCameraIdsAsync(ctx.User.UserId());

            object? gallery = null;
            object? live = null;
            object? events = null;
            string? probeError = null;

            var hasTextFilter = !string.IsNullOrWhiteSpace(input.PersonName)
                                || !string.IsNullOrWhiteSpace(input.FaceId);

            if (probe is null && mode is "gallery" or "live")
                return Results.BadRequest(new { error = "imageBase64 ou embedding obrigatório neste modo." });

            if (probe is null && mode is "all" && !hasTextFilter)
                return Results.BadRequest(new
                {
                    error = "Envie imageBase64, embedding ou personName/faceId."
                });

            if (probe is null && mode is "all")
                probeError = "Sem imagem — só eventos por texto.";

            if (probe is not null && mode is "gallery" or "all")
                gallery = await faces.SearchGalleryAsync(tenant, probe, threshold, take, ct);

            if (probe is not null && mode is "live" or "all")
            {
                var liveHits = await faces.SearchLiveCamerasAsync(
                    tenant, visible, probe, threshold,
                    input.CameraIds, input.MaxCameras ?? 16, ct);
                live = new
                {
                    threshold,
                    matches = liveHits.Where(h => h.SnapshotOk && h.Score >= threshold).ToList(),
                    scanned = liveHits
                };
            }

            if (mode is "events" or "all")
            {
                events = await faces.SearchEventsAsync(
                    tenant, visible,
                    input.PersonName, input.FaceId, input.DeviceId,
                    input.From, input.To,
                    probe, threshold, take, ct);
            }

            await audit.WriteAsync(ctx, "face.search", "face", mode, true,
                detail: $"thr={threshold:F2}");

            return Results.Ok(new
            {
                engine = "visual-fingerprint-v1",
                mode,
                threshold,
                hasProbe = probe is not null,
                probeError,
                gallery,
                live,
                events
            });
        });

        g.MapPost("/faces/search/live", async (
            FaceSearchInput input, HttpContext ctx, FaceSearchService faces, FaceFingerprint fp,
            PermissionService perms, CancellationToken ct) =>
        {
            if (!await faces.IsLicensedAsync(ct))
                return FaceLicenseRequired();

            var probe = await ResolveImageEmbedding(input.ImageBase64, input.Embedding, fp, ct);
            if (probe is null)
                return Results.BadRequest(new { error = "imageBase64 ou embedding obrigatório." });

            var visible = await perms.VisibleCameraIdsAsync(ctx.User.UserId());
            var threshold = input.Threshold ?? FaceFingerprint.DefaultThreshold;
            var hits = await faces.SearchLiveCamerasAsync(
                ctx.User.TenantId(), visible, probe, threshold,
                input.CameraIds, input.MaxCameras ?? 16, ct);

            return Results.Ok(new
            {
                threshold,
                matches = hits.Where(h => h.SnapshotOk && h.Score >= threshold)
                    .OrderByDescending(h => h.Score).ToList(),
                scanned = hits.OrderByDescending(h => h.Score).ToList()
            });
        });

        g.MapGet("/faces/events", async (
            HttpContext ctx, FaceSearchService faces, PermissionService perms,
            string? personName, string? faceId, int? deviceId,
            DateTime? from, DateTime? to, int take = 50, CancellationToken ct = default) =>
        {
            if (!await faces.IsLicensedAsync(ct))
                return FaceLicenseRequired();

            var visible = await perms.VisibleCameraIdsAsync(ctx.User.UserId());
            var rows = await faces.SearchEventsAsync(
                ctx.User.TenantId(), visible,
                personName, faceId, deviceId, from, to,
                null, FaceFingerprint.DefaultThreshold, take, ct);
            return Results.Ok(rows);
        });

        g.MapPost("/faces/compare", async (
            FaceCompareInput input, FaceSearchService faces, FaceFingerprint fp, CancellationToken ct) =>
        {
            if (!await faces.IsLicensedAsync(ct))
                return FaceLicenseRequired();

            var a = await ResolveImageEmbedding(input.ImageABase64, input.EmbeddingA, fp, ct);
            var b = await ResolveImageEmbedding(input.ImageBBase64, input.EmbeddingB, fp, ct);
            if (a is null || b is null)
                return Results.BadRequest(new { error = "Duas imagens (ou embeddings) são obrigatórias." });

            var score = FaceFingerprint.Similarity(a, b);
            var threshold = input.Threshold ?? FaceFingerprint.DefaultThreshold;
            return Results.Ok(new
            {
                score,
                threshold,
                match = score >= threshold,
                engine = "visual-fingerprint-v1"
            });
        });
    }

    private static async Task<float[]?> ResolveImageEmbedding(
        string? imageBase64, float[]? embedding, FaceFingerprint fp, CancellationToken ct)
    {
        if (embedding is { Length: > 0 }) return embedding;
        var bytes = FaceFingerprint.DecodeImagePayload(imageBase64);
        if (bytes is null) return null;
        return await fp.FromImageAsync(bytes, ct);
    }

    private static object ToDto(FaceGalleryEntry e) => new
    {
        e.Id, e.Name, e.ExternalFaceId, e.PhotoUrl, e.PhotoPath,
        e.ListType, e.Notes, e.Active, e.CreatedAt, e.UpdatedAt,
        hasEmbedding = !string.IsNullOrEmpty(e.EmbeddingJson)
    };

    private static IResult FaceLicenseRequired() => Results.Json(new
    {
        error = "Módulo de reconhecimento facial não licenciado.",
        code = "FACE_LICENSE_REQUIRED",
        hint = "Instale uma licença com AnalyticsFacial=true em Administração → Licenciamento."
    }, statusCode: StatusCodes.Status402PaymentRequired);

    private static async Task<bool> HasConfig(HttpContext ctx)
    {
        var perms = ctx.RequestServices.GetService(typeof(PermissionService)) as PermissionService;
        if (perms is null) return false;
        return await perms.HasAsync(ctx.User.UserId(), Permissions.CameraConfig)
            || await perms.HasAsync(ctx.User.UserId(), Permissions.SystemConfig);
    }
}

public record FaceEnrollInput(
    string? Name,
    string? ExternalFaceId,
    string? PhotoUrl,
    string? Notes,
    string? ListType,
    bool? Active,
    string? ImageBase64,
    float[]? Embedding);

public record FaceSearchInput(
    string? Mode,
    string? ImageBase64,
    float[]? Embedding,
    float? Threshold,
    int? Take,
    int[]? CameraIds,
    int? MaxCameras,
    string? PersonName,
    string? FaceId,
    int? DeviceId,
    DateTime? From,
    DateTime? To);

public record FaceCompareInput(
    string? ImageABase64,
    string? ImageBBase64,
    float[]? EmbeddingA,
    float[]? EmbeddingB,
    float? Threshold);

public record FaceMatchByIdInput(string? ExternalFaceId);
