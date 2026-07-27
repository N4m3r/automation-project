using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Modules.Vms;

/// <summary>Stub de análise IA no servidor. Galeria facial → <see cref="FaceSearchEndpoints"/>.</summary>
public static class PlatformExtraEndpoints
{
    public static void MapPlatformExtras(this RouteGroupBuilder g)
    {
        MapAi(g);
    }

    private static void MapAi(RouteGroupBuilder g)
    {
        // Stub de IA no servidor (GPU real fica no roadmap — aqui analisa metadados/texto).
        g.MapPost("/ai/analyze", async (AiAnalyzeInput input, HttpContext ctx, PlatformDbContext db) =>
        {
            var tenant = ctx.User.TenantId();
            var kind = (input.Kind ?? "scene").ToLowerInvariant();
            var summary = "";
            var tags = new List<string>();
            object? details = null;

            if (input.EventId is long eid)
            {
                var ev = await db.Events.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == eid && e.TenantId == tenant);
                if (ev is null) return Results.NotFound(new { error = "Evento não encontrado." });
                var meta = EventMetadata.TryParseFromPayload(ev.Payload);
                tags.Add(ev.Type);
                if (meta?.Plate is { Length: > 0 } plate)
                {
                    tags.Add("lpr");
                    tags.Add(plate);
                    summary = $"Placa {plate}" + (meta.ListMatch is not null ? $" [{meta.ListMatch}]" : "");
                }
                else if (meta?.FaceId is { Length: > 0 } fid)
                {
                    tags.Add("face");
                    var face = await db.FaceGalleryEntries.AsNoTracking()
                        .FirstOrDefaultAsync(f => f.TenantId == tenant && f.ExternalFaceId == fid && f.Active);
                    summary = face is not null
                        ? $"Rosto reconhecido: {face.Name} (id {fid})"
                        : $"Rosto desconhecido (id {fid})";
                    if (face is not null) tags.Add(face.Name);
                }
                else if (meta?.Kind is { Length: > 0 } k)
                {
                    tags.Add(k);
                    summary = $"Analítico embarcado: {k}" +
                              (meta.Description is { Length: > 0 } d ? $" — {d}" : "");
                }
                else
                {
                    summary = $"Evento {ev.Type} severidade {ev.Severity}";
                }

                details = new
                {
                    ev.Id, ev.DeviceId, ev.Type, ev.Severity, ev.CreatedAt, meta,
                    risk = ev.Severity >= 3 ? "high" : ev.Severity >= 2 ? "medium" : "low"
                };
            }
            else if (input.AlarmEventId is long aid)
            {
                var a = await db.AlarmEvents.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == aid && e.TenantId == tenant);
                if (a is null) return Results.NotFound(new { error = "Alarme não encontrado." });
                tags.Add(a.Code);
                tags.Add("alarm");
                summary = $"Alarme {a.Code} conta {a.Account} zona {a.Zone} — status {a.Status}";
                details = a;
            }
            else if (!string.IsNullOrWhiteSpace(input.Text))
            {
                summary = "Análise textual (stub): " + input.Text.Trim()[..Math.Min(200, input.Text.Trim().Length)];
                tags.Add(kind);
            }
            else
            {
                summary = "Sem payload para analisar. Envie eventId, alarmEventId ou text.";
            }

            return Results.Ok(new
            {
                engine = "server-stub-v1",
                gpu = false,
                kind,
                summary,
                tags,
                details,
                note = "Stub local: integra GPU/LLM (SpaceXAI) em produção quando configurado."
            });
        });

        g.MapGet("/ai/status", () => Results.Ok(new
        {
            available = true,
            mode = "stub",
            gpu = false,
            features = new[] { "event_summarize", "face_lookup", "alarm_context", "lpr_context" }
        }));
    }

}

public record AiAnalyzeInput(string? Kind, long? EventId, long? AlarmEventId, string? Text, int? DeviceId);
