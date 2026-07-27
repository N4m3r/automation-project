using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;
using SecurityPlatform.Core.Security;
using SecurityPlatform.Drivers.Onvif;
using SecurityPlatform.Modules.Security;
using SecurityPlatform.Modules.Vms;

namespace SecurityPlatform.Modules.Admin;

/// <summary>
/// Painel administrativo: servidor de gravação, câmeras, grupos, perfis de
/// mídia, agendamentos, licenciamento, contatos e automação.
/// Tudo aqui exige perfil administrador.
/// </summary>
public static class AdminEndpoints
{
    public static IServiceCollection AddAdminModule(this IServiceCollection services)
    {
        services.AddSingleton<HealthMonitor>();
        services.AddHostedService<LogRetentionService>();
        return services;
    }

    public static IEndpointRouteBuilder MapAdminModule(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin").WithTags("Administracao").RequireAuthorization("admin");

        MapServer(g);
        MapCameras(g);
        MapCameraGroups(g);
        MapMediaProfiles(g);
        MapSchedules(g);
        MapLicense(g);
        MapContacts(g);
        MapAutomation(g);
        MapEventActionButtons(g);
        MapSettings(g);
        MapSecuritySettings(g);
        MapIoDevices(g);
        MapGmcTenants(g);

        return app;
    }

    // ------------------------------------------------------ Security / LDAP (UI)
    private static void MapSecuritySettings(RouteGroupBuilder g)
    {
        g.MapGet("/security-settings", (RuntimeSecurityWriter writer) => Results.Ok(writer.Snapshot()));

        g.MapPut("/security-settings", async (
            SecuritySettingsInput input, HttpContext ctx, RuntimeSecurityWriter writer, AuditService audit) =>
        {
            try
            {
                await writer.WriteAsync(input, ctx.RequestAborted);
                await audit.WriteAsync(ctx, "security.settings.update", "system", "security",
                    detail: "politica + LDAP via UI");
                // Pequena espera para o file watcher recarregar IConfiguration.
                await Task.Delay(200, ctx.RequestAborted);
                return Results.Ok(writer.Snapshot());
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    // ------------------------------------------------------ GMC multi-tenant
    private static void MapGmcTenants(RouteGroupBuilder g)
    {
        g.MapGet("/tenants", async (PlatformDbContext db) =>
            await db.Tenants.AsNoTracking()
                .OrderBy(t => t.Id)
                .Select(t => new
                {
                    t.Id, t.Name, t.Active, t.CreatedAt,
                    users = db.Users.Count(u => u.TenantId == t.Id),
                    cameras = db.Devices.Count(d => d.TenantId == t.Id && d.Kind == DeviceKind.Camera)
                })
                .ToListAsync());

        g.MapPost("/tenants", async (GmcTenantInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name))
                return Results.BadRequest(new { error = "Nome do tenant obrigatório." });
            var t = new Tenant
            {
                Name = input.Name.Trim(),
                Active = input.Active ?? true,
                CreatedAt = DateTime.UtcNow
            };
            db.Tenants.Add(t);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "gmc.tenant.create", "tenant", t.Id.ToString(), true);
            return Results.Created($"/api/admin/tenants/{t.Id}", new { t.Id, t.Name });
        });

        g.MapPut("/tenants/{id:int}", async (int id, GmcTenantInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            var t = await db.Tenants.FindAsync(id);
            if (t is null) return Results.NotFound();
            if (!string.IsNullOrWhiteSpace(input.Name)) t.Name = input.Name.Trim();
            if (input.Active is not null) t.Active = input.Active.Value;
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "gmc.tenant.update", "tenant", id.ToString(), true);
            return Results.Ok(new { t.Id, t.Name, t.Active });
        });

        g.MapPost("/tenants/{id:int}/switch", async (
            int id, HttpContext ctx, PlatformDbContext db, AuthService auth, AuditService audit) =>
        {
            var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Active);
            if (t is null) return Results.NotFound(new { error = "Tenant não encontrado ou inativo." });
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == ctx.User.UserId());
            if (user is null) return Results.Unauthorized();
            var expires = DateTime.UtcNow.AddMinutes(480);
            var token = auth.IssueToken(user, expires, tenantOverride: t.Id);
            await audit.WriteAsync(ctx, "gmc.tenant.switch", "tenant", id.ToString(), true, detail: t.Name);
            return Results.Ok(new { token, expiresAt = expires, tenantId = t.Id, tenantName = t.Name });
        });

        g.MapPost("/tenants/users/{userId:int}/assign", async (
            int userId, GmcAssignInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user is null) return Results.NotFound();
            var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == input.TenantId);
            if (t is null) return Results.BadRequest(new { error = "Tenant inválido." });
            user.TenantId = input.TenantId;
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "gmc.user.assign", "user", userId.ToString(), true,
                detail: $"tenant={input.TenantId}");
            return Results.Ok(new { user.Id, user.Username, user.TenantId });
        });
    }

    // ------------------------------------------------------ dispositivos I/O

    private static void MapIoDevices(RouteGroupBuilder g)
    {
        g.MapGet("/io-devices", async (PlatformDbContext db) =>
            await db.Devices.AsNoTracking()
                .Where(d => d.Kind != DeviceKind.Camera)
                .Select(d => new
                {
                    d.Id, d.Name, d.Kind, d.Driver, d.Host, d.Port,
                    d.StreamUrl, d.Status, d.LastSeen,
                    senhaDefinida = d.Password != ""
                })
                .ToListAsync());

        g.MapPost("/io-devices", async (
            IoDeviceInput input, HttpContext ctx, PlatformDbContext db,
            DriverRegistry registry, AuditService audit) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Host))
                return Results.BadRequest(new { error = "Nome e host são obrigatórios." });

            var driver = string.IsNullOrWhiteSpace(input.Driver) ? "http-io" : input.Driver.Trim().ToLowerInvariant();
            var port = input.Port;
            if (port <= 0)
                port = driver == "commbox" ? 1024 : 80;

            // Commbox: StreamUrl vira JSON de config nativa se model/protocol informados.
            var streamUrl = input.StreamUrl ?? "";
            if (driver is "commbox" or "commbox-mio" or "mio")
            {
                driver = "commbox";
                if (string.IsNullOrWhiteSpace(streamUrl) || !streamUrl.TrimStart().StartsWith('{'))
                {
                    var model = string.IsNullOrWhiteSpace(input.Model) ? "mio0816" : input.Model!.Trim();
                    var protocol = string.IsNullOrWhiteSpace(input.Protocol) ? "auto" : input.Protocol!.Trim().ToLowerInvariant();
                    streamUrl = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        protocol,
                        model,
                        tcpPort = port
                    });
                }
            }

            var dev = new Device
            {
                TenantId = ctx.User.TenantId(),
                Name = input.Name.Trim(),
                Kind = DeviceKind.AccessPoint,
                Driver = driver,
                Host = input.Host.Trim(),
                Port = port,
                Username = input.Username ?? "",
                Password = input.Password ?? "",
                StreamUrl = streamUrl,
                Recording = RecordingMode.Off
            };

            db.Devices.Add(dev);
            await db.SaveChangesAsync();

            try
            {
                dev.Status = await registry.Resolve(dev).ConnectAsync(dev)
                    ? DeviceStatus.Online : DeviceStatus.Offline;
                dev.LastSeen = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            catch { /* driver pode não estar registrado */ }

            await audit.WriteAsync(ctx, "io.create", "device", dev.Id.ToString(),
                detail: $"{dev.Name} driver={dev.Driver}");
            return Results.Created($"/api/admin/io-devices/{dev.Id}",
                new { dev.Id, dev.Name, dev.Host, dev.Port, dev.Status, dev.Driver, dev.StreamUrl });
        });

        g.MapPost("/io-devices/{id:int}/relay/{state}", async (
            int id, string state, int channel, int? ms, HttpContext ctx,
            PlatformDbContext db, DriverRegistry registry, AuditService audit) =>
        {
            var dev = await db.Devices.FindAsync(id);
            if (dev is null || dev.Kind == DeviceKind.Camera) return Results.NotFound();

            var ch = channel <= 0 ? 1 : channel;
            string cmd;
            var p = new Dictionary<string, string> { ["channel"] = ch.ToString() };
            if (string.Equals(state, "pulse", StringComparison.OrdinalIgnoreCase))
            {
                cmd = "relay_pulse";
                p["ms"] = (ms is > 0 and <= 60_000 ? ms.Value : 1000).ToString();
            }
            else
            {
                var on = !string.Equals(state, "off", StringComparison.OrdinalIgnoreCase);
                cmd = on ? "relay_on" : "relay_off";
            }

            var result = await registry.Resolve(dev).CommandAsync(dev, cmd, p);
            await audit.WriteAsync(ctx, $"io.{cmd}", "device", id.ToString(), result.Ok,
                detail: $"ch={ch}");
            return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
        });

        g.MapPost("/io-devices/{id:int}/test", async (
            int id, PlatformDbContext db, DriverRegistry registry) =>
        {
            var dev = await db.Devices.FindAsync(id);
            if (dev is null || dev.Kind == DeviceKind.Camera) return Results.NotFound();

            var driver = registry.Resolve(dev);
            var online = await driver.ConnectAsync(dev);
            dev.Status = online ? DeviceStatus.Online : DeviceStatus.Offline;
            dev.LastSeen = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var info = await driver.CommandAsync(dev, "device_info");
            return Results.Ok(new
            {
                online,
                status = dev.Status.ToString(),
                driver = dev.Driver,
                info = info.Data
            });
        });

        g.MapGet("/io-devices/{id:int}/inputs", async (
            int id, PlatformDbContext db, DriverRegistry registry) =>
        {
            var dev = await db.Devices.FindAsync(id);
            if (dev is null || dev.Kind == DeviceKind.Camera) return Results.NotFound();
            var result = await registry.Resolve(dev).CommandAsync(dev, "get_inputs");
            return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
        });

        g.MapDelete("/io-devices/{id:int}", async (
            int id, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            var dev = await db.Devices.FindAsync(id);
            if (dev is null || dev.Kind == DeviceKind.Camera) return Results.NotFound();
            db.Devices.Remove(dev);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "io.delete", "device", id.ToString());
            return Results.NoContent();
        });
    }

    public record IoDeviceInput(
        string Name,
        string Host,
        int Port = 80,
        string Driver = "http-io",
        string Username = "",
        string Password = "",
        string StreamUrl = "",
        /// <summary>Modelo Commbox (mio0816, mio2408…); vira JSON em StreamUrl.</summary>
        string? Model = null,
        /// <summary>auto | http | tcp</summary>
        string? Protocol = null);

    // ---------------------------------------------- configuracoes do sistema

    private static void MapSettings(RouteGroupBuilder g)
    {
        g.MapGet("/settings", async (PlatformDbContext db) =>
        {
            var s = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync()
                    ?? new SystemSettings();

            // A senha SMTP nunca sai do servidor.
            return new
            {
                s.Id, s.ServerName, s.Description, s.TimeZone, s.Language,
                s.StorageRoot, s.DefaultRetentionDays, s.SegmentSeconds,
                s.EncryptRecordings, s.WatermarkExport, s.BlurFacesOnExport,
                s.ArchivePath, s.ArchiveAfterDays,
                s.DiskWarningPercent, s.DiskCriticalPercent,
                s.SmtpHost, s.SmtpPort, s.SmtpUseTls, s.SmtpUser, s.SmtpFrom,
                smtpSenhaDefinida = !string.IsNullOrEmpty(s.SmtpPassword),
                s.MediaServerApi, s.MediaPublicHost,
                s.SystemLogRetentionDays, s.EventLogRetentionDays, s.AuditRetentionDays,
                s.UpdatedAt
            };
        });

        g.MapPut("/settings", async (
            SystemSettings input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            var s = await db.SystemSettings.FirstOrDefaultAsync();
            if (s is null) { s = new SystemSettings(); db.SystemSettings.Add(s); }

            s.ServerName = input.ServerName;
            s.Description = input.Description;
            s.TimeZone = input.TimeZone;
            s.Language = input.Language;
            s.StorageRoot = input.StorageRoot;
            s.DefaultRetentionDays = input.DefaultRetentionDays;
            s.SegmentSeconds = input.SegmentSeconds;
            s.EncryptRecordings = input.EncryptRecordings;
            s.WatermarkExport = input.WatermarkExport;
            s.BlurFacesOnExport = input.BlurFacesOnExport;
            s.ArchivePath = input.ArchivePath ?? "";
            s.ArchiveAfterDays = input.ArchiveAfterDays;
            s.DiskWarningPercent = input.DiskWarningPercent;
            s.DiskCriticalPercent = input.DiskCriticalPercent;
            s.SmtpHost = input.SmtpHost;
            s.SmtpPort = input.SmtpPort;
            s.SmtpUseTls = input.SmtpUseTls;
            s.SmtpUser = input.SmtpUser;
            s.SmtpFrom = input.SmtpFrom;
            s.MediaServerApi = input.MediaServerApi;
            s.MediaPublicHost = input.MediaPublicHost;
            s.SystemLogRetentionDays = input.SystemLogRetentionDays;
            s.EventLogRetentionDays = input.EventLogRetentionDays;
            s.AuditRetentionDays = input.AuditRetentionDays;

            // Senha em branco mantém a atual.
            if (!string.IsNullOrEmpty(input.SmtpPassword)) s.SmtpPassword = input.SmtpPassword;

            s.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "settings.update", "system", "1");

            return Results.NoContent();
        });

        // --- Filtros de IP no nível do servidor
        g.MapGet("/ip-filters", async (PlatformDbContext db) =>
            await db.IpFilters.AsNoTracking().ToListAsync());

        g.MapPost("/ip-filters", async (
            IpFilter filter, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            var meuIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";

            // Uma regra que bloqueie quem a está criando derruba o próprio
            // painel usado para desfazê-la. Recusar é mais seguro que aceitar.
            var futuras = await db.IpFilters.Where(f => f.Enabled).AsNoTracking().ToListAsync();
            futuras.Add(filter);

            if (!IpRules.IsLoopback(meuIp) && !IpRules.Allowed(futuras, meuIp))
                return Results.BadRequest(new
                {
                    error = $"Esta regra bloquearia o seu proprio endereco ({meuIp}), " +
                            "deixando o painel inacessivel. Inclua o seu IP antes de aplica-la."
                });

            db.IpFilters.Add(filter);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "ipfilter.create", "system", filter.Id.ToString(),
                detail: $"{filter.Mode} {filter.Address}");

            return Results.Created($"/api/admin/ip-filters/{filter.Id}", filter);
        });

        g.MapDelete("/ip-filters/{id:int}", async (int id, PlatformDbContext db) =>
        {
            var f = await db.IpFilters.FindAsync(id);
            if (f is null) return Results.NotFound();

            db.IpFilters.Remove(f);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // --- Informações do servidor: uso de disco por câmera
        g.MapGet("/storage/usage", async (PlatformDbContext db, IOptions<VmsOptions> opt) =>
        {
            var cams = await db.Devices.Where(d => d.Kind == DeviceKind.Camera)
                .Select(d => new { d.Id, d.Name, d.RetentionDays, d.MaxStorageGb })
                .AsNoTracking().ToListAsync();

            var porCamera = await db.Recordings
                .GroupBy(r => r.DeviceId)
                .Select(gr => new
                {
                    deviceId = gr.Key,
                    arquivos = gr.Count(),
                    bytes = gr.Sum(r => r.SizeBytes),
                    protegidos = gr.Count(r => r.Protected),
                    maisAntiga = gr.Min(r => r.StartedAt),
                    maisRecente = gr.Max(r => r.StartedAt)
                }).ToListAsync();

            var detalhe = cams.Select(c =>
            {
                var u = porCamera.FirstOrDefault(p => p.deviceId == c.Id);
                var gb = Math.Round((u?.bytes ?? 0) / 1024d / 1024 / 1024, 3);
                return new
                {
                    c.Id, c.Name, c.RetentionDays, c.MaxStorageGb,
                    arquivos = u?.arquivos ?? 0,
                    gigabytes = gb,
                    protegidos = u?.protegidos ?? 0,
                    // Quanto da cota da camera ja foi consumido: e o numero que
                    // antecipa "o disco vai encher" antes de encher.
                    percentualDaCota = c.MaxStorageGb > 0
                        ? Math.Round(gb / c.MaxStorageGb * 100, 1)
                        : (double?)null,
                    maisAntiga = u?.maisAntiga,
                    maisRecente = u?.maisRecente
                };
            }).ToList();

            var totalGb = Math.Round(porCamera.Sum(p => p.bytes) / 1024d / 1024 / 1024, 3);

            return new
            {
                totalGigabytes = totalGb,
                cotaGlobalGb = opt.Value.MaxStorageGb,
                percentualDaCotaGlobal = opt.Value.MaxStorageGb > 0
                    ? Math.Round(totalGb / opt.Value.MaxStorageGb * 100, 1)
                    : (double?)null,
                cameras = detalhe
            };
        });
    }

    // ------------------------------------------------- servidor de gravacao

    private static void MapServer(RouteGroupBuilder g)
    {
        g.MapGet("/server/health", (HealthMonitor health) => health.Read());

        // Visão consolidada do servidor: o que o operador de central olha primeiro.
        g.MapGet("/server/status", async (
            PlatformDbContext db, HealthMonitor health, MediaGateway media,
            IOptions<VmsOptions> vmsOpt, RuntimeSecurityWriter securityWriter) =>
        {
            var cameras = await db.Devices
                .Where(d => d.Kind == DeviceKind.Camera)
                .AsNoTracking().ToListAsync();

            var paths = await media.ListPathNamesAsync();
            var vms = vmsOpt.Value;
            var leasesAtivos = vms.HaEnabled
                ? await db.RecorderLeases.CountAsync(l => l.ExpiresAt > DateTime.UtcNow)
                : 0;

            var since = DateTime.UtcNow.AddHours(-24);
            return new
            {
                health = health.Read(),
                cameras = new
                {
                    total = cameras.Count,
                    online = cameras.Count(c => c.Status == DeviceStatus.Online),
                    offline = cameras.Count(c => c.Status == DeviceStatus.Offline),
                    recording = cameras.Count(c => c.Recording == RecordingMode.Continuous)
                },
                media = new { pathsRegistrados = paths.Count },
                ha = new
                {
                    enabled = vms.HaEnabled,
                    nodeId = vms.ResolveNodeId(),
                    shardIndex = vms.ShardIndex,
                    shardCount = vms.ShardCount,
                    leaseSeconds = vms.LeaseSeconds,
                    activeLeases = leasesAtivos,
                    keyRingPath = securityWriter.ResolveKeyRingPath(),
                    note = vms.HaEnabled
                        ? "HA ativo: so o no com lease grava cada camera."
                        : "HA desligado (Vms:HaEnabled=false). Em multi-gravador, ligue e compartilhe Security:KeyRingPath + DB."
                },
                eventos24h = await db.Events.CountAsync(e => e.CreatedAt >= since),
                eventosCriticos24h = await db.Events.CountAsync(e => e.CreatedAt >= since && e.Severity >= 3),
                gravacoes = new
                {
                    arquivos = await db.Recordings.CountAsync(),
                    gigabytes = Math.Round(
                        await db.Recordings.SumAsync(r => (double?)r.SizeBytes) / 1024 / 1024 / 1024 ?? 0, 2)
                }
            };
        });

        // Log de atividade do servidor (auditoria filtrada por ação).
        g.MapGet("/server/activity", async (PlatformDbContext db, string? action, int take = 200) =>
        {
            var q = db.AuditLogs.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action.StartsWith(action));

            return await q.OrderByDescending(a => a.CreatedAt)
                .Take(Math.Clamp(take, 1, 1000)).ToListAsync();
        });
    }

    // ------------------------------------------------------------- cameras

    private static void MapCameras(RouteGroupBuilder g)
    {
        // Detalhe completo — o cadastro do painel administrativo.
        g.MapGet("/cameras/{id:int}", async (int id, PlatformDbContext db) =>
        {
            var cam = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
            if (cam is null) return Results.NotFound();

            var groups = await db.CameraGroupMembers.Where(m => m.DeviceId == id)
                .Join(db.CameraGroups, m => m.GroupId, gr => gr.Id, (_, gr) => new { gr.Id, gr.Name })
                .ToListAsync();

            var slots = await db.ScheduleSlots.Where(s => s.DeviceId == id).AsNoTracking().ToListAsync();

            return Results.Ok(new
            {
                cam.Id, cam.TenantId, cam.Name, cam.Kind, cam.Driver, cam.Host, cam.Port,
                cam.Username, cam.StreamUrl, cam.Recording, cam.RetentionDays,
                cam.MaxStorageGb, cam.EventRecordSeconds,
                cam.RecordingProfileId, cam.LiveProfileId, cam.RecordAudio, cam.EdgePullEnabled,
                cam.Status, cam.LastSeen, cam.CreatedAt,
                senhaDefinida = !string.IsNullOrEmpty(cam.Password),   // nunca devolve a senha
                grupos = groups,
                agendamentos = slots
            });
        });

        g.MapPut("/cameras/{id:int}", async (
            int id, CameraUpdate input, HttpContext ctx,
            PlatformDbContext db, DriverRegistry registry, MediaGateway media, AuditService audit,
            IOptions<VmsOptions> opt) =>
        {
            var cam = await db.Devices.FindAsync(id);
            if (cam is null) return Results.NotFound();

            cam.Name = input.Name ?? cam.Name;
            cam.Driver = input.Driver ?? cam.Driver;
            cam.Host = input.Host ?? cam.Host;
            cam.Port = input.Port ?? cam.Port;
            cam.Username = input.Username ?? cam.Username;
            cam.StreamUrl = input.StreamUrl ?? cam.StreamUrl;
            cam.Recording = input.Recording ?? cam.Recording;
            cam.RetentionDays = input.RetentionDays ?? cam.RetentionDays;
            cam.MaxStorageGb = input.MaxStorageGb ?? cam.MaxStorageGb;
            cam.EventRecordSeconds = input.EventRecordSeconds ?? cam.EventRecordSeconds;
            if (input.RecordingProfileId.HasValue || input.ClearRecordingProfile)
                cam.RecordingProfileId = input.ClearRecordingProfile ? null : input.RecordingProfileId;
            if (input.LiveProfileId.HasValue || input.ClearLiveProfile)
                cam.LiveProfileId = input.ClearLiveProfile ? null : input.LiveProfileId;
            if (input.RecordAudio.HasValue) cam.RecordAudio = input.RecordAudio.Value;
            if (input.EdgePullEnabled.HasValue) cam.EdgePullEnabled = input.EdgePullEnabled.Value;

            // Senha em branco significa "manter a atual".
            if (!string.IsNullOrEmpty(input.Password)) cam.Password = input.Password;

            await db.SaveChangesAsync();

            var driver = registry.Resolve(cam);
            cam.Status = await driver.ConnectAsync(cam) ? DeviceStatus.Online : DeviceStatus.Offline;
            cam.LastSeen = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var perfis = await db.MediaProfiles.AsNoTracking().ToDictionaryAsync(p => p.Id);
            var baseUrl = await driver.GetStreamUrlAsync(cam);
            var main = StreamUrlBuilder.ApplyQuality(baseUrl, StreamUrlBuilder.Quality.Main,
                StreamUrlBuilder.ResolveChannel(cam, perfis, StreamUrlBuilder.Quality.Main));
            await media.RegisterAsync(cam.Id, main, substream: false);
            // Sub nativo = 2ª sessão RTSP — só se a política permitir multi-pull.
            if (!opt.Value.SingleCameraRtspPull)
            {
                var sub = StreamUrlBuilder.ApplyQuality(baseUrl, StreamUrlBuilder.Quality.Sub,
                    StreamUrlBuilder.ResolveChannel(cam, perfis, StreamUrlBuilder.Quality.Sub));
                await media.RegisterAsync(cam.Id, sub, substream: true);
            }
            else
            {
                // Fecha path sub legado para liberar sessão na câmera.
                await media.RemovePathAsync(MediaGateway.PathName(cam.Id, substream: true));
            }
            await audit.WriteAsync(ctx, "camera.update", "camera", id.ToString());

            return Results.Ok(new { cam.Id, cam.Name, cam.Status });
        });

        // Testa a conexão sem salvar — usado no botão "Testar" do cadastro.
        g.MapPost("/cameras/test", async (
            CameraInput input, int? id, PlatformDbContext db, DriverRegistry registry) =>
        {
            // A tela não devolve a senha salva: em branco significa "usar a atual".
            var password = input.Password;
            if (string.IsNullOrEmpty(password) && id is int camId)
                password = (await db.Devices.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == camId))?.Password ?? "";

            var probe = new Device
            {
                Name = input.Name, Kind = DeviceKind.Camera, Driver = input.Driver,
                Host = input.Host, Port = input.Port,
                Username = input.Username, Password = password, StreamUrl = input.StreamUrl
            };

            var driver = registry.Resolve(probe);
            var online = await driver.ConnectAsync(probe);

            return Results.Ok(new
            {
                online,
                driver = driver.Name,
                rtsp = UrlMasking.Mask(await driver.GetStreamUrlAsync(probe))
            });
        });

        // Identificação nativa do equipamento (modelo, firmware, série).
        g.MapGet("/cameras/{id:int}/device-info", async (
            int id, PlatformDbContext db, DriverRegistry registry) =>
        {
            var cam = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
            if (cam is null) return Results.NotFound();

            var result = await registry.Resolve(cam).CommandAsync(cam, "device_info");
            return result.Ok ? Results.Ok(result.Data) : Results.BadRequest(new { error = result.Error });
        });

        // WS-Discovery ONVIF na LAN (UDP 3702). Multicast pode falhar em VLAN.
        // Câmeras já cadastradas ou já presentes como canal de NVR NÃO entram
        // em "standalone" — só as avulsas (com nome OSD quando possível).
        g.MapPost("/cameras/discover", async (
            HttpContext ctx,
            PlatformDbContext db,
            DiscoverCamerasInput? input,
            int timeoutSeconds = 4) =>
        {
            var seconds = Math.Clamp(timeoutSeconds, 1, 15);
            input ??= new DiscoverCamerasInput();

            var found = await OnvifDiscovery.ProbeAsync(
                TimeSpan.FromSeconds(seconds), ctx.RequestAborted);

            var registered = await db.Devices.AsNoTracking()
                .Where(d => d.Kind == DeviceKind.Camera)
                .ToListAsync(ctx.RequestAborted);

            var result = await CameraDiscoveryEnricher.EnrichAsync(
                found,
                registered,
                input.Username,
                input.Password,
                ctx.RequestAborted);

            // Por padrão devolve só as avulsas (não puxar as que já estão no NVR/cadastro).
            // includeSkipped=true permite auditoria no admin.
            object MapRow(CameraDiscoveryEnricher.DiscoveredCameraRow r) => new
            {
                host = r.Host,
                port = r.Port,
                name = r.Name,
                osdName = r.OsdName,
                xAddrs = r.XAddrs,
                scopes = r.Scopes,
                driver = r.Driver,
                alreadyRegistered = r.AlreadyRegistered,
                registeredDeviceId = r.RegisteredDeviceId,
                registeredName = r.RegisteredName,
                onNvr = r.OnNvr,
                nvrDeviceId = r.NvrDeviceId,
                nvrHost = r.NvrHost,
                nvrName = r.NvrName,
                nvrChannelId = r.NvrChannelId,
                nvrChannelName = r.NvrChannelName
            };

            return Results.Ok(new
            {
                foundTotal = result.FoundTotal,
                nvrSourcesKnown = result.NvrSourcesKnown,
                standaloneCount = result.Standalone.Count,
                skippedCount = result.Skipped.Count,
                // Lista principal: só avulsas (não cadastradas e não no NVR).
                devices = result.Standalone.Select(MapRow),
                // Opcional: o que foi filtrado (já no NVR / já no cadastro).
                skipped = input.IncludeSkipped == true
                    ? result.Skipped.Select(MapRow)
                    : Array.Empty<object>()
            });
        });
    }

    // ------------------------------------------------------ grupos de camera

    private static void MapCameraGroups(RouteGroupBuilder g)
    {
        g.MapGet("/camera-groups", async (PlatformDbContext db) =>
        {
            var groups = await db.CameraGroups.AsNoTracking().ToListAsync();
            var members = await db.CameraGroupMembers.AsNoTracking().ToListAsync();

            return groups.Select(gr => new
            {
                gr.Id, gr.Name, gr.Description, gr.ParentId,
                cameras = members.Where(m => m.GroupId == gr.Id).Select(m => m.DeviceId).ToList()
            });
        });

        g.MapPost("/camera-groups", async (CameraGroup group, PlatformDbContext db) =>
        {
            db.CameraGroups.Add(group);
            await db.SaveChangesAsync();
            return Results.Created($"/api/admin/camera-groups/{group.Id}", group);
        });

        g.MapPut("/camera-groups/{id:int}", async (
            int id, CameraGroupUpdate input, PlatformDbContext db) =>
        {
            var group = await db.CameraGroups.FindAsync(id);
            if (group is null) return Results.NotFound();

            // Ciclo na arvore travaria a expansao de direitos; a checagem sobe
            // a cadeia de pais antes de aceitar o novo vinculo.
            if (input.ParentId is int novoPai)
            {
                if (novoPai == id) return Results.BadRequest(new { error = "Um grupo nao pode ser pai de si mesmo." });
                if (await CriaCicloAsync(db, id, novoPai))
                    return Results.BadRequest(new { error = "Este vinculo criaria um ciclo na arvore de grupos." });
            }

            group.Name = input.Name ?? group.Name;
            group.Description = input.Description ?? group.Description;
            if (input.ClearParent) group.ParentId = null;
            else if (input.ParentId is not null) group.ParentId = input.ParentId;

            await db.SaveChangesAsync();
            return Results.Ok(new { group.Id, group.Name, group.ParentId });
        });

        // Quem perde acesso se este grupo for excluido? Direito concedido sobre
        // o grupo some junto — mostrar antes evita a surpresa depois.
        g.MapGet("/camera-groups/{id:int}/impact", async (int id, PlatformDbContext db) =>
        {
            var rights = await db.ObjectRights.AsNoTracking()
                .Where(r => r.ObjectType == ObjectTypes.CameraGroup && r.ObjectId == id)
                .ToListAsync();

            var gruposDeUsuario = await db.UserGroups.AsNoTracking()
                .Where(x => rights.Where(r => r.SubjectType == SubjectType.Group)
                    .Select(r => r.SubjectId).Contains(x.Id))
                .Select(x => new { x.Id, x.Name }).ToListAsync();

            var subgrupos = await db.CameraGroups.AsNoTracking()
                .Where(x => x.ParentId == id).Select(x => new { x.Id, x.Name }).ToListAsync();

            var cameras = await db.CameraGroupMembers.AsNoTracking()
                .Where(m => m.GroupId == id).CountAsync();

            return Results.Ok(new { direitos = rights.Count, gruposDeUsuario, subgrupos, cameras });
        });

        g.MapDelete("/camera-groups/{id:int}", async (int id, PlatformDbContext db) =>
        {
            var group = await db.CameraGroups.FindAsync(id);
            if (group is null) return Results.NotFound();

            db.CameraGroups.Remove(group);
            db.CameraGroupMembers.RemoveRange(db.CameraGroupMembers.Where(m => m.GroupId == id));

            // Direito apontando para grupo excluido ficaria orfao e voltaria a
            // valer se o id fosse reaproveitado.
            db.ObjectRights.RemoveRange(db.ObjectRights
                .Where(r => r.ObjectType == ObjectTypes.CameraGroup && r.ObjectId == id));

            // Subgrupo sem pai sobe para a raiz em vez de virar inalcancavel.
            foreach (var filho in await db.CameraGroups.Where(x => x.ParentId == id).ToListAsync())
                filho.ParentId = null;

            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Adicionar varias cameras de uma vez: a tela seleciona em lote, e uma
        // requisicao por camera deixava a configuracao pela metade quando falhava.
        g.MapPut("/camera-groups/{groupId:int}/cameras", async (
            int groupId, int[] deviceIds, PlatformDbContext db) =>
        {
            if (!await db.CameraGroups.AnyAsync(x => x.Id == groupId)) return Results.NotFound();

            db.CameraGroupMembers.RemoveRange(db.CameraGroupMembers.Where(m => m.GroupId == groupId));
            foreach (var deviceId in deviceIds.Distinct())
                db.CameraGroupMembers.Add(new CameraGroupMember { GroupId = groupId, DeviceId = deviceId });

            await db.SaveChangesAsync();
            return Results.Ok(new { grupo = groupId, cameras = deviceIds.Distinct().Count() });
        });

        g.MapPost("/camera-groups/{groupId:int}/cameras/{deviceId:int}", async (
            int groupId, int deviceId, PlatformDbContext db) =>
        {
            if (await db.CameraGroupMembers.AnyAsync(m => m.GroupId == groupId && m.DeviceId == deviceId))
                return Results.NoContent();

            db.CameraGroupMembers.Add(new CameraGroupMember { GroupId = groupId, DeviceId = deviceId });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapDelete("/camera-groups/{groupId:int}/cameras/{deviceId:int}", async (
            int groupId, int deviceId, PlatformDbContext db) =>
        {
            var m = await db.CameraGroupMembers
                .FirstOrDefaultAsync(x => x.GroupId == groupId && x.DeviceId == deviceId);
            if (m is null) return Results.NotFound();

            db.CameraGroupMembers.Remove(m);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    /// <summary>Subir a cadeia de pais e reencontrar o proprio grupo = ciclo.</summary>
    private static async Task<bool> CriaCicloAsync(PlatformDbContext db, int groupId, int novoPai)
    {
        var pais = await db.CameraGroups.AsNoTracking()
            .Where(x => x.ParentId != null)
            .ToDictionaryAsync(x => x.Id, x => x.ParentId!.Value);

        var atual = novoPai;
        var visitados = new HashSet<int>();
        while (visitados.Add(atual))
        {
            if (atual == groupId) return true;
            if (!pais.TryGetValue(atual, out atual)) return false;
        }
        return true;   // ja havia ciclo — nao piorar
    }

    // ------------------------------------------------------ perfis de midia

    private static void MapMediaProfiles(RouteGroupBuilder g)
    {
        g.MapGet("/media-profiles", async (PlatformDbContext db) =>
            await db.MediaProfiles.AsNoTracking().ToListAsync());

        g.MapPost("/media-profiles", async (MediaProfile profile, PlatformDbContext db) =>
        {
            db.MediaProfiles.Add(profile);
            await db.SaveChangesAsync();
            return Results.Created($"/api/admin/media-profiles/{profile.Id}", profile);
        });

        g.MapDelete("/media-profiles/{id:int}", async (int id, PlatformDbContext db) =>
        {
            var p = await db.MediaProfiles.FindAsync(id);
            if (p is null) return Results.NotFound();

            db.MediaProfiles.Remove(p);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Calculadora de disco: quanto ocupa gravar N dias neste perfil.
        g.MapGet("/media-profiles/{id:int}/storage-estimate", async (
            int id, PlatformDbContext db, int days = 7, int cameras = 1) =>
        {
            var p = await db.MediaProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return Results.NotFound();

            var gbPerDay = p.BitrateKbps / 8d / 1024 / 1024 * 86400;   // Kbps -> GB/dia
            return Results.Ok(new
            {
                perfil = p.Name,
                gbPorDiaPorCamera = Math.Round(gbPerDay, 2),
                totalGb = Math.Round(gbPerDay * days * cameras, 1),
                dias = days,
                cameras
            });
        });
    }

    // -------------------------------------------------------- agendamentos

    private static void MapSchedules(RouteGroupBuilder g)
    {
        g.MapGet("/schedules", async (PlatformDbContext db, int? deviceId) =>
        {
            var q = db.ScheduleSlots.AsNoTracking().AsQueryable();
            if (deviceId is not null) q = q.Where(s => s.DeviceId == deviceId);
            return await q.ToListAsync();
        });

        g.MapPost("/schedules", async (ScheduleSlot slot, PlatformDbContext db) =>
        {
            if (slot.End <= slot.Start)
                return Results.BadRequest(new { error = "O fim deve ser posterior ao inicio." });

            db.ScheduleSlots.Add(slot);
            await db.SaveChangesAsync();
            return Results.Created($"/api/admin/schedules/{slot.Id}", slot);
        });

        g.MapDelete("/schedules/{id:int}", async (int id, PlatformDbContext db) =>
        {
            var s = await db.ScheduleSlots.FindAsync(id);
            if (s is null) return Results.NotFound();

            db.ScheduleSlots.Remove(s);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    // -------------------------------------------------------- licenciamento

    private static void MapLicense(RouteGroupBuilder g)
    {
        g.MapGet("/license", async (PlatformDbContext db) =>
        {
            var lic = await db.Licenses.AsNoTracking().OrderByDescending(l => l.InstalledAt)
                .FirstOrDefaultAsync();

            var usedChannels = await db.Devices.CountAsync(d => d.Kind == DeviceKind.Camera);
            var usedAccess = await db.Devices.CountAsync(d => d.Kind == DeviceKind.AccessPoint);
            var usedAlarms = await db.Devices.CountAsync(d => d.Kind == DeviceKind.AlarmPanel);

            // Sem licença instalada, a instalação opera no limite gratuito.
            lic ??= new License { Edition = LicenseEdition.Express, VideoChannels = 4 };

            return new
            {
                licenca = new
                {
                    lic.Edition, lic.CustomerName, lic.ExpiresAt, lic.InstalledAt,
                    lic.Failover, lic.MultiTenant, lic.AnalyticsLpr, lic.AnalyticsFacial
                },
                consumo = new
                {
                    canaisVideo = new { usados = usedChannels, licenciados = lic.VideoChannels },
                    pontosAcesso = new { usados = usedAccess, licenciados = lic.AccessPoints },
                    zonasAlarme = new { usados = usedAlarms, licenciados = lic.AlarmZones }
                },
                excedido = usedChannels > lic.VideoChannels,
                expirada = lic.ExpiresAt is not null && lic.ExpiresAt < DateTime.UtcNow
            };
        });

        g.MapPost("/license", async (License license, HttpContext ctx,
            PlatformDbContext db, AuditService audit, LicenseSigner signer) =>
        {
            // Chave assinada (payload.hmac) prevalece sobre os campos soltos do body.
            if (!string.IsNullOrWhiteSpace(license.Key))
            {
                try
                {
                    var signed = signer.TryValidate(license.Key);
                    if (signed is not null)
                    {
                        license.Edition = Enum.TryParse<LicenseEdition>(signed.Edition, true, out var ed)
                            ? ed : LicenseEdition.Express;
                        license.CustomerName = signed.CustomerName;
                        license.VideoChannels = signed.VideoChannels;
                        license.AccessPoints = signed.AccessPoints;
                        license.AlarmZones = signed.AlarmZones;
                        license.Failover = signed.Failover;
                        license.MultiTenant = signed.MultiTenant;
                        license.AnalyticsLpr = signed.AnalyticsLpr;
                        license.AnalyticsFacial = signed.AnalyticsFacial;
                        license.ExpiresAt = signed.ExpiresAt;
                    }
                    else if (signer.RequiresSigned)
                    {
                        return Results.BadRequest(new
                        {
                            error = "Esta instalação exige licença assinada (payload.assinatura)."
                        });
                    }
                }
                catch (InvalidOperationException e)
                {
                    return Results.BadRequest(new { error = e.Message });
                }
            }
            else if (signer.RequiresSigned)
            {
                return Results.BadRequest(new
                {
                    error = "Informe a chave de licença assinada no campo key."
                });
            }

            license.InstalledAt = DateTime.UtcNow;
            db.Licenses.Add(license);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "license.install", "license", license.Id.ToString(),
                detail: $"{license.Edition} · {license.VideoChannels} canais");

            return Results.Created($"/api/admin/license", new
            {
                license.Id, license.Edition, license.CustomerName, license.VideoChannels,
                license.AccessPoints, license.AlarmZones, license.ExpiresAt, license.InstalledAt
            });
        });

        // Gera chave assinada (admin) — para integradores / ambiente controlado.
        g.MapPost("/license/sign", (LicensePayload payload, LicenseSigner signer) =>
        {
            try
            {
                var key = signer.Issue(payload);
                return Results.Ok(new { key, payload });
            }
            catch (Exception e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });
    }

    // -------------------------------------------------------------- contatos

    private static void MapContacts(RouteGroupBuilder g)
    {
        g.MapGet("/contacts", async (PlatformDbContext db) =>
            await db.Contacts.AsNoTracking().ToListAsync());

        g.MapPost("/contacts", async (Contact contact, PlatformDbContext db) =>
        {
            db.Contacts.Add(contact);
            await db.SaveChangesAsync();
            return Results.Created($"/api/admin/contacts/{contact.Id}", contact);
        });

        g.MapDelete("/contacts/{id:int}", async (int id, PlatformDbContext db) =>
        {
            var c = await db.Contacts.FindAsync(id);
            if (c is null) return Results.NotFound();

            db.Contacts.Remove(c);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    // ------------------------------------------------------------ automacao

    private static void MapAutomation(RouteGroupBuilder g)
    {
        g.MapGet("/automation", async (HttpContext ctx, PlatformDbContext db) =>
            await db.AutomationRules.AsNoTracking()
                .Where(r => r.TenantId == ctx.User.TenantId())
                .OrderByDescending(r => r.Enabled).ThenBy(r => r.Name)
                .ToListAsync());

        g.MapPost("/automation", async (AutomationRuleInput input, HttpContext ctx,
            PlatformDbContext db, AuditService audit) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name))
                return Results.BadRequest(new { error = "Nome da regra é obrigatório." });
            if (!TryValidateActions(input.Actions, out var err))
                return Results.BadRequest(new { error = err });

            var rule = ApplyAutomationInput(new AutomationRule
            {
                TenantId = ctx.User.TenantId(),
                CreatedAt = DateTime.UtcNow
            }, input);

            db.AutomationRules.Add(rule);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "automation.create", "rule", rule.Id.ToString(), detail: rule.Name);
            return Results.Created($"/api/admin/automation/{rule.Id}", rule);
        });

        g.MapPut("/automation/{id:int}", async (
            int id, AutomationRuleInput input, HttpContext ctx,
            PlatformDbContext db, AuditService audit) =>
        {
            var rule = await db.AutomationRules
                .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == ctx.User.TenantId());
            if (rule is null) return Results.NotFound();
            if (input.Actions is not null && !TryValidateActions(input.Actions, out var err))
                return Results.BadRequest(new { error = err });

            ApplyAutomationInput(rule, input);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "automation.update", "rule", id.ToString(), detail: rule.Name);
            return Results.Ok(rule);
        });

        g.MapPost("/automation/{id:int}/enabled/{enabled:bool}", async (
            int id, bool enabled, HttpContext ctx, PlatformDbContext db) =>
        {
            var r = await db.AutomationRules
                .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == ctx.User.TenantId());
            if (r is null) return Results.NotFound();

            r.Enabled = enabled;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapDelete("/automation/{id:int}", async (int id, HttpContext ctx, PlatformDbContext db) =>
        {
            var r = await db.AutomationRules
                .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == ctx.User.TenantId());
            if (r is null) return Results.NotFound();

            db.AutomationRules.Remove(r);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapGet("/automation/action-kinds", () => Enum.GetNames<EventActionKind>());

        // Templates prontos (estilo Digifort) para agilizar o cadastro.
        g.MapGet("/automation/templates", () => Results.Ok(new object[]
        {
            new
            {
                id = "popup_sound",
                name = "Popup + som de alarme",
                description = "Abre popup com a câmera e toca alerta no posto",
                whenEventType = "intrusion",
                minSeverity = 2,
                cooldownSeconds = 30,
                actions = new object[]
                {
                    new { kind = "PopupVideo", secondsAfter = 20 },
                    new { kind = "PlaySound", title = "alarm" },
                    new { kind = "OpenLive" }
                }
            },
            new
            {
                id = "motion_popup",
                name = "Motion → popup live",
                description = "Qualquer motion abre vídeo no posto",
                whenEventType = "motion",
                minSeverity = 1,
                cooldownSeconds = 15,
                actions = new object[]
                {
                    new { kind = "PopupVideo", secondsAfter = 12 },
                    new { kind = "PlaySound", title = "beep" }
                }
            },
            new
            {
                id = "offline_alert",
                name = "Câmera offline → e-mail + popup",
                description = "Avisa no posto e por e-mail",
                whenEventType = "device_offline",
                minSeverity = 2,
                cooldownSeconds = 120,
                actions = new object[]
                {
                    new { kind = "PopupVideo", secondsAfter = 30 },
                    new { kind = "PlaySound", title = "alarm" },
                    new { kind = "Email", subject = "[VMS] Câmera offline" }
                }
            },
            new
            {
                id = "sirene_commbox",
                name = "Alarme → relé/sirene (I/O)",
                description = "Dispara saída digital (Commbox/HTTP-IO) por 3s",
                whenEventType = "intrusion",
                minSeverity = 2,
                cooldownSeconds = 60,
                actions = new object[]
                {
                    new { kind = "OutputRelay", channel = 1, pulseMs = 3000 },
                    new { kind = "PopupVideo", secondsAfter = 20 },
                    new { kind = "PlaySound", title = "alarm" }
                }
            },
            new
            {
                id = "bookmark",
                name = "Evento crítico → bookmark",
                description = "Marca e protege gravação ±30s/60s",
                whenEventType = "*",
                minSeverity = 3,
                cooldownSeconds = 10,
                actions = new object[]
                {
                    new { kind = "Bookmark", secondsBefore = 30, secondsAfter = 60, title = "Crítico" },
                    new { kind = "PopupVideo", secondsAfter = 25 }
                }
            }
        }));
    }

    private static bool TryValidateActions(string? actions, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(actions)) { error = "Informe ao menos uma ação."; return false; }
        try
        {
            var list = EventActionRunner.ParseActions(actions);
            if (list.Count == 0) { error = "Lista de ações vazia."; return false; }
            return true;
        }
        catch
        {
            error = "Ações: JSON inválido.";
            return false;
        }
    }

    private static AutomationRule ApplyAutomationInput(AutomationRule rule, AutomationRuleInput input)
    {
        if (input.Name is not null) rule.Name = input.Name.Trim();
        if (input.WhenEventType is not null) rule.WhenEventType = string.IsNullOrWhiteSpace(input.WhenEventType) ? "*" : input.WhenEventType.Trim();
        if (input.WhenDeviceId.HasValue) rule.WhenDeviceId = input.WhenDeviceId == 0 ? null : input.WhenDeviceId;
        else if (input.ClearDevice) rule.WhenDeviceId = null;
        if (input.MinSeverity is int sev) rule.MinSeverity = Math.Clamp(sev, 1, 3);
        if (input.Actions is not null) rule.Actions = input.Actions;
        if (input.ScheduleDays is not null) rule.ScheduleDays = string.IsNullOrWhiteSpace(input.ScheduleDays) ? "0,1,2,3,4,5,6" : input.ScheduleDays.Trim();
        if (input.ScheduleStart is not null) rule.ScheduleStart = input.ScheduleStart.Trim();
        if (input.ScheduleEnd is not null) rule.ScheduleEnd = input.ScheduleEnd.Trim();
        if (input.TimeZone is not null) rule.TimeZone = string.IsNullOrWhiteSpace(input.TimeZone) ? "America/Sao_Paulo" : input.TimeZone.Trim();
        if (input.CooldownSeconds is int cd) rule.CooldownSeconds = Math.Clamp(cd, 0, 86_400);
        if (input.Enabled is bool en) rule.Enabled = en;
        return rule;
    }

    // ------------------------------------ botões de ação sobre eventos (Digifort-like)

    private static void MapEventActionButtons(RouteGroupBuilder g)
    {
        g.MapGet("/event-action-buttons", async (HttpContext ctx, PlatformDbContext db) =>
        {
            var tenant = ctx.User.TenantId();
            return await db.EventActionButtons.AsNoTracking()
                .Where(b => b.TenantId == tenant)
                .OrderBy(b => b.SortOrder).ThenBy(b => b.Name)
                .ToListAsync();
        });

        g.MapPost("/event-action-buttons", async (
            EventActionButtonInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name))
                return Results.BadRequest(new { error = "Nome obrigatório." });

            // Valida JSON de ações
            try { _ = EventActionRunner.ParseActions(input.Actions ?? "[]"); }
            catch { return Results.BadRequest(new { error = "Actions: JSON inválido." }); }

            var btn = new EventActionButton
            {
                TenantId = ctx.User.TenantId(),
                Name = input.Name.Trim(),
                Description = input.Description?.Trim() ?? "",
                Icon = string.IsNullOrWhiteSpace(input.Icon) ? "⚡" : input.Icon.Trim(),
                Color = string.IsNullOrWhiteSpace(input.Color) ? "#238636" : input.Color.Trim(),
                EventTypes = string.IsNullOrWhiteSpace(input.EventTypes) ? "*" : input.EventTypes.Trim(),
                MinSeverity = Math.Clamp(input.MinSeverity ?? 1, 1, 3),
                Actions = string.IsNullOrWhiteSpace(input.Actions) ? "[]" : input.Actions!,
                AutoAcknowledge = input.AutoAcknowledge ?? false,
                SetStatus = NormalizeStatus(input.SetStatus),
                RequireConfirm = input.RequireConfirm ?? false,
                RequireComment = input.RequireComment ?? false,
                SortOrder = input.SortOrder ?? 100,
                Enabled = input.Enabled ?? true,
                UpdatedAt = DateTime.UtcNow
            };
            db.EventActionButtons.Add(btn);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "event_button.create", "event_button", btn.Id.ToString(), detail: btn.Name);
            return Results.Created($"/api/admin/event-action-buttons/{btn.Id}", btn);
        });

        g.MapPut("/event-action-buttons/{id:int}", async (
            int id, EventActionButtonInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            var btn = await db.EventActionButtons.FirstOrDefaultAsync(b => b.Id == id && b.TenantId == ctx.User.TenantId());
            if (btn is null) return Results.NotFound();

            if (input.Name is not null) btn.Name = input.Name.Trim();
            if (input.Description is not null) btn.Description = input.Description.Trim();
            if (input.Icon is not null) btn.Icon = input.Icon.Trim();
            if (input.Color is not null) btn.Color = input.Color.Trim();
            if (input.EventTypes is not null) btn.EventTypes = string.IsNullOrWhiteSpace(input.EventTypes) ? "*" : input.EventTypes.Trim();
            if (input.MinSeverity is int sev) btn.MinSeverity = Math.Clamp(sev, 1, 3);
            if (input.Actions is not null)
            {
                _ = EventActionRunner.ParseActions(input.Actions);
                btn.Actions = input.Actions;
            }
            if (input.AutoAcknowledge is bool aa) btn.AutoAcknowledge = aa;
            if (input.SetStatus is not null) btn.SetStatus = NormalizeStatus(input.SetStatus);
            if (input.RequireConfirm is bool rc) btn.RequireConfirm = rc;
            if (input.RequireComment is bool rcm) btn.RequireComment = rcm;
            if (input.SortOrder is int so) btn.SortOrder = so;
            if (input.Enabled is bool en) btn.Enabled = en;
            btn.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "event_button.update", "event_button", id.ToString(), detail: btn.Name);
            return Results.Ok(btn);
        });

        g.MapPost("/event-action-buttons/{id:int}/enabled/{enabled:bool}", async (
            int id, bool enabled, HttpContext ctx, PlatformDbContext db) =>
        {
            var btn = await db.EventActionButtons.FirstOrDefaultAsync(b => b.Id == id && b.TenantId == ctx.User.TenantId());
            if (btn is null) return Results.NotFound();
            btn.Enabled = enabled;
            btn.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapDelete("/event-action-buttons/{id:int}", async (
            int id, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            var btn = await db.EventActionButtons.FirstOrDefaultAsync(b => b.Id == id && b.TenantId == ctx.User.TenantId());
            if (btn is null) return Results.NotFound();
            db.EventActionButtons.Remove(btn);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "event_button.delete", "event_button", id.ToString(), detail: btn.Name);
            return Results.NoContent();
        });
    }

    private static string? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var s = status.Trim().ToLowerInvariant();
        return s is "open" or "treating" or "resolved" ? s : null;
    }
}

public record EventActionButtonInput(
    string? Name = null,
    string? Description = null,
    string? Icon = null,
    string? Color = null,
    string? EventTypes = null,
    int? MinSeverity = null,
    string? Actions = null,
    bool? AutoAcknowledge = null,
    string? SetStatus = null,
    bool? RequireConfirm = null,
    bool? RequireComment = null,
    int? SortOrder = null,
    bool? Enabled = null);

/// <summary>Cadastro/edição de regra de automação (agenda + cooldown + ações).</summary>
public record AutomationRuleInput(
    string? Name = null,
    string? WhenEventType = null,
    int? WhenDeviceId = null,
    bool ClearDevice = false,
    int? MinSeverity = null,
    string? Actions = null,
    string? ScheduleDays = null,
    string? ScheduleStart = null,
    string? ScheduleEnd = null,
    string? TimeZone = null,
    int? CooldownSeconds = null,
    bool? Enabled = null);


public record GmcTenantInput(string? Name, bool? Active);
public record GmcAssignInput(int TenantId);

/// <summary>
/// Corpo opcional da descoberta ONVIF: credenciais de sonda para ler o OSD
/// das câmeras avulsas, e se deve listar o que foi filtrado (já no NVR/cadastro).
/// </summary>
public record DiscoverCamerasInput(
    string? Username = null,
    string? Password = null,
    bool? IncludeSkipped = null);

public record CameraGroupUpdate(
    string? Name = null,
    string? Description = null,
    int? ParentId = null,
    bool ClearParent = false);

public record CameraUpdate(
    string? Name = null,
    string? Driver = null,
    string? Host = null,
    int? Port = null,
    string? Username = null,
    string? Password = null,
    string? StreamUrl = null,
    RecordingMode? Recording = null,
    int? RetentionDays = null,
    int? MaxStorageGb = null,
    int? EventRecordSeconds = null,
    int? RecordingProfileId = null,
    int? LiveProfileId = null,
    bool ClearRecordingProfile = false,
    bool ClearLiveProfile = false,
    bool? RecordAudio = null,
    bool? EdgePullEnabled = null);
