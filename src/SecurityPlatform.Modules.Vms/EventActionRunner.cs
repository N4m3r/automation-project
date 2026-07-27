using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Executa ações de evento (automação automática e botões manuais do operador).
/// Formato JSON: [{"kind":"PtzPreset","deviceId":2,"preset":3}, ...]
/// </summary>
public sealed class EventActionRunner(ILogger<EventActionRunner> log)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static List<ActionDto> ParseActions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ActionDto>>(json, JsonOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task RunAllAsync(
        IEnumerable<ActionDto> acoes, DeviceEvent evt,
        PlatformDbContext db, DriverRegistry registry, CancellationToken ct = default)
    {
        var settings = await db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct) ?? new SystemSettings();

        foreach (var acao in acoes)
        {
            try
            {
                await RunOneAsync(acao, evt, settings, db, registry, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Ação {Kind} falhou no evento #{Id}", acao.Kind, evt.Id);
            }
        }
    }

    public async Task RunOneAsync(
        ActionDto acao, DeviceEvent evt, SystemSettings settings,
        PlatformDbContext db, DriverRegistry registry, CancellationToken ct = default)
    {
        var kind = Enum.TryParse<EventActionKind>(acao.Kind, ignoreCase: true, out var k)
            ? k
            : (EventActionKind?)null;

        switch (kind)
        {
            case EventActionKind.Email:
                await SendEmailAsync(acao, evt, settings, db, ct);
                break;

            case EventActionKind.PtzPreset:
            {
                var deviceId = acao.DeviceId ?? evt.DeviceId;
                if (deviceId is not int id) return;
                var cam = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
                if (cam is null) return;
                var preset = acao.Preset?.ToString() ?? "1";
                await registry.Resolve(cam).CommandAsync(cam, "ptz_preset",
                    new Dictionary<string, string> { ["preset"] = preset }, ct);
                break;
            }

            case EventActionKind.Bookmark:
            {
                if (evt.DeviceId is not int camId) return;
                var cam = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == camId, ct);
                if (cam is null) return;

                var margem = TimeSpan.FromSeconds(Math.Clamp(acao.SecondsBefore ?? 30, 5, 300));
                var depois = TimeSpan.FromSeconds(Math.Clamp(acao.SecondsAfter ?? 60, 5, 600));
                var inicio = evt.CreatedAt - margem;
                var fim = evt.CreatedAt + depois;

                db.Bookmarks.Add(new Bookmark
                {
                    TenantId = cam.TenantId,
                    DeviceId = camId,
                    Title = acao.Title ?? $"Auto: {evt.Type}",
                    Description = $"Ação de evento #{evt.Id}",
                    StartedAt = inicio,
                    EndedAt = fim
                });

                var cobertas = await db.Recordings
                    .Where(r => r.DeviceId == camId && r.StartedAt <= fim
                             && (r.EndedAt ?? r.StartedAt) >= inicio)
                    .ToListAsync(ct);
                foreach (var r in cobertas) r.Protected = true;
                await db.SaveChangesAsync(ct);
                break;
            }

            case EventActionKind.HttpRequest:
            {
                if (string.IsNullOrWhiteSpace(acao.Url)) return;
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var method = (acao.Method ?? "POST").ToUpperInvariant();
                using var req = new HttpRequestMessage(new HttpMethod(method), acao.Url);
                if (method is "POST" or "PUT" or "PATCH")
                {
                    var body = JsonSerializer.Serialize(new
                    {
                        eventId = evt.Id,
                        type = evt.Type,
                        deviceId = evt.DeviceId,
                        severity = evt.Severity,
                        payload = evt.Payload,
                        at = evt.CreatedAt
                    });
                    req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                }
                var res = await http.SendAsync(req, ct);
                if (!res.IsSuccessStatusCode)
                    log.LogWarning("Webhook {Url} respondeu HTTP {Code}", acao.Url, (int)res.StatusCode);
                break;
            }

            case EventActionKind.PopupVideo:
            case EventActionKind.PlaySound:
            case EventActionKind.PushNotification:
            case EventActionKind.OpenLive:
            case EventActionKind.OpenPlayback:
            case EventActionKind.OpenMap:
                // Cliente (monitor) trata via flags de retorno / WebSocket.
                log.LogDebug("Ação de cliente {Kind} para evento {Id}", kind, evt.Id);
                break;

            case EventActionKind.OutputRelay:
            {
                var deviceId = acao.DeviceId ?? evt.DeviceId;
                if (deviceId is not int id) return;
                var dev = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
                if (dev is null)
                {
                    log.LogWarning("OutputRelay: dispositivo {Id} não encontrado", id);
                    return;
                }

                var channel = acao.Channel?.ToString() ?? "1";
                var on = !string.Equals(acao.State, "off", StringComparison.OrdinalIgnoreCase);
                var cmd = on ? "relay_on" : "relay_off";
                var parametros = new Dictionary<string, string> { ["channel"] = channel };
                if (acao.PulseMs is int ms) { cmd = "relay_pulse"; parametros["ms"] = ms.ToString(); }

                var result = await registry.Resolve(dev).CommandAsync(dev, cmd, parametros, ct);
                if (!result.Ok)
                    log.LogWarning("OutputRelay falhou em {Id}: {Erro}", id, result.Error);
                break;
            }

            default:
                log.LogWarning("Tipo de ação desconhecido: {Kind}", acao.Kind);
                break;
        }
    }

    private async Task SendEmailAsync(
        ActionDto acao, DeviceEvent evt, SystemSettings settings,
        PlatformDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
        {
            log.LogWarning("Ação e-mail pedida mas SMTP não está configurado");
            return;
        }

        var destinos = new List<string>();
        if (!string.IsNullOrWhiteSpace(acao.Email))
            destinos.Add(acao.Email);

        if (acao.ContactId is int cid)
        {
            var c = await db.Contacts.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == cid && x.Active, ct);
            if (c is not null && !string.IsNullOrWhiteSpace(c.Email))
                destinos.Add(c.Email);
        }

        if (destinos.Count == 0)
        {
            destinos = await db.Contacts.AsNoTracking()
                .Where(c => c.TenantId == evt.TenantId && c.Active && c.Email != "")
                .Select(c => c.Email)
                .ToListAsync(ct);
        }

        destinos = destinos.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (destinos.Count == 0)
        {
            log.LogWarning("Nenhum destinatário de e-mail para o evento {Id}", evt.Id);
            return;
        }

        var from = string.IsNullOrWhiteSpace(settings.SmtpFrom)
            ? settings.SmtpUser
            : settings.SmtpFrom;
        if (string.IsNullOrWhiteSpace(from)) from = "noreply@localhost";

        var assunto = acao.Subject ?? $"[{settings.ServerName}] {evt.Type} — câmera {evt.DeviceId}";
        var corpo = acao.Body ??
            $"Evento: {evt.Type}\nSeveridade: {evt.Severity}\nCâmera: {evt.DeviceId}\nQuando (UTC): {evt.CreatedAt:o}\nPayload: {evt.Payload}";

        using var msg = new MailMessage
        {
            From = new MailAddress(from),
            Subject = assunto,
            Body = corpo,
            IsBodyHtml = false
        };
        foreach (var d in destinos) msg.To.Add(d);

        using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
        {
            EnableSsl = settings.SmtpUseTls,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        if (!string.IsNullOrEmpty(settings.SmtpUser))
            client.Credentials = new NetworkCredential(settings.SmtpUser, settings.SmtpPassword);

        await client.SendMailAsync(msg, ct);
        log.LogInformation("E-mail enviado a {Count} destinatário(s) para evento {Id}",
            destinos.Count, evt.Id);
    }

    public sealed class ActionDto
    {
        public string Kind { get; set; } = "";
        public int? DeviceId { get; set; }
        public int? Preset { get; set; }
        public int? ContactId { get; set; }
        public string? Email { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public string? Url { get; set; }
        public string? Method { get; set; }
        public string? Title { get; set; }
        public int? SecondsBefore { get; set; }
        public int? SecondsAfter { get; set; }
        public int? Channel { get; set; }
        public string? State { get; set; }
        public int? PulseMs { get; set; }
    }
}
