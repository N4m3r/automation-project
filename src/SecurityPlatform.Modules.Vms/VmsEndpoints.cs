using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;
using SecurityPlatform.Core.Events;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Modules.Vms;

public static class VmsEndpoints
{
    public static IServiceCollection AddVmsModule(this IServiceCollection services)
    {
        // Timeout curto: MediaMTX caído não pode travar /stream por 100s.
        services.AddHttpClient<MediaGateway>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddSingleton<VmsMetrics>();
        services.AddSingleton<StorageClusterLock>();
        services.AddSingleton<RecordingCrypto>();
        services.AddSingleton<RecordingNormalizer>();
        services.AddSingleton<ExportSigner>();
        services.AddSingleton<PtzTourService>();
        services.AddSingleton<RecorderLeaseService>();
        services.AddHostedService(sp => sp.GetRequiredService<RecorderLeaseService>());
        services.AddScoped<RecordingExporter>();
        services.AddSingleton<FaceFingerprint>();
        services.AddScoped<FaceSearchService>();
        services.AddHostedService<MediaSyncService>();
        services.AddHostedService<MediaGatewayHealthService>();
        services.AddHostedService<DeviceEventListener>();
        // Singleton + HostedService: a exclusão de câmera precisa parar o FFmpeg
        // antes de apagar pasta/registro; AddHostedService sozinho não injeta.
        services.AddSingleton<RecorderService>();
        services.AddHostedService(sp => sp.GetRequiredService<RecorderService>());
        services.AddHostedService<RetentionService>();
        services.AddHostedService<CameraHealthService>();
        services.AddHostedService<ThumbnailService>();
        services.AddHostedService<ArchiveService>();
        services.AddSingleton<EventActionRunner>();
        services.AddHostedService<AutomationEngine>();
        services.AddHostedService<EdgePullService>();
        services.AddHostedService<LiveTranscodeService>();
        services.AddHostedService<SiaReceiverService>();
        services.AddHostedService<MqttBridgeService>();
        return services;
    }

    public static IEndpointRouteBuilder MapVmsModule(this IEndpointRouteBuilder app)
    {
        // Todo o modulo exige autenticacao; os direitos por camera sao
        // verificados endpoint a endpoint.
        var g = app.MapGroup("/api/vms").WithTags("VMS").RequireAuthorization();

        MapCameras(g);
        MapPtz(g);
        MapPlayback(g);
        MapBookmarks(g);
        MapEvents(g);
        MapHealth(g);
        MapLayouts(g);
        g.MapSynopticMaps();
        g.MapAnalytics();
        g.MapAccessControl();
        g.MapAlarms();
        g.MapFaceSearch();
        g.MapPlatformExtras();

        return app;
    }

    // -------------------------------------------------------------- cameras

    private static void MapCameras(RouteGroupBuilder g)
    {
        // Lista apenas as cameras que o usuario tem direito de ver.
        g.MapGet("/cameras", async (HttpContext ctx, PlatformDbContext db, PermissionService perms) =>
        {
            var visible = await perms.VisibleCameraIdsAsync(ctx.User.UserId());
            return await db.Devices
                .Where(d => d.Kind == DeviceKind.Camera && visible.Contains(d.Id))
                .Select(d => new
                {
                    d.Id, d.TenantId, d.Name, d.Driver, d.Host, d.Port,
                    d.Recording, d.RetentionDays, d.MaxStorageGb,
                    d.RecordingProfileId, d.LiveProfileId, d.RecordAudio,
                    d.Status, d.LastSeen, d.CreatedAt
                })
                .AsNoTracking().ToListAsync();
        });

        // Grupos de câmera (somente leitura) para operador — mapa / filtro.
        // Filtra membros às câmeras visíveis do usuário.
        g.MapGet("/camera-groups", async (HttpContext ctx, PlatformDbContext db, PermissionService perms) =>
        {
            var visible = await perms.VisibleCameraIdsAsync(ctx.User.UserId());
            var groups = await db.CameraGroups.AsNoTracking()
                .OrderBy(x => x.Name)
                .Select(gr => new { gr.Id, gr.Name, gr.Description, gr.ParentId })
                .ToListAsync();
            var members = await db.CameraGroupMembers.AsNoTracking()
                .Where(m => visible.Contains(m.DeviceId))
                .Select(m => new { m.GroupId, m.DeviceId })
                .ToListAsync();
            var byGroup = members.GroupBy(m => m.GroupId)
                .ToDictionary(x => x.Key, x => x.Select(m => m.DeviceId).ToList());

            return Results.Ok(groups.Select(gr => new
            {
                gr.Id,
                gr.Name,
                gr.Description,
                gr.ParentId,
                cameras = byGroup.TryGetValue(gr.Id, out var cams) ? cams : new List<int>()
            }));
        });

        g.MapPost("/cameras", async (
            CameraInput input, HttpContext ctx, PlatformDbContext db,
            DriverRegistry registry, MediaGateway media, AuditService audit,
            IOptions<VmsOptions> opt) =>
        {
            // Licença: bloqueia cadastro além dos canais contratados.
            var licenca = await db.Licenses.AsNoTracking()
                .OrderByDescending(l => l.InstalledAt).FirstOrDefaultAsync();
            var limite = licenca?.VideoChannels ?? 4;
            if (licenca?.ExpiresAt is DateTime exp && exp < DateTime.UtcNow)
                return Results.Conflict(new { error = "Licença expirada. Renove antes de cadastrar câmeras." });

            var usadas = await db.Devices.CountAsync(d => d.Kind == DeviceKind.Camera);
            if (usadas >= limite)
                return Results.Conflict(new
                {
                    error = $"Limite de canais da licença atingido ({usadas}/{limite}).",
                    usados = usadas,
                    licenciados = limite
                });

            // O tenant vem do token, nao do corpo: aceitar do cliente deixaria
            // um administrador de um cliente cadastrar camera em outro.
            var cam = new Device
            {
                TenantId = ctx.User.TenantId(),
                Name = input.Name,
                Kind = DeviceKind.Camera,
                Driver = input.Driver,
                Host = input.Host,
                Port = input.Port,
                Username = input.Username,
                Password = input.Password,
                StreamUrl = input.StreamUrl,
                Recording = input.Recording,
                RetentionDays = input.RetentionDays,
                MaxStorageGb = input.MaxStorageGb,
                EventRecordSeconds = input.EventRecordSeconds,
                PreEventSeconds = input.PreEventSeconds,
                RecordingProfileId = input.RecordingProfileId,
                LiveProfileId = input.LiveProfileId,
                RecordAudio = input.RecordAudio,
                EdgePullEnabled = input.EdgePullEnabled
            };

            db.Devices.Add(cam);
            await db.SaveChangesAsync();

            var driver = registry.Resolve(cam);
            cam.Status = await driver.ConnectAsync(cam) ? DeviceStatus.Online : DeviceStatus.Offline;
            cam.LastSeen = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var perfis = await db.MediaProfiles.AsNoTracking().ToDictionaryAsync(p => p.Id);
            var baseUrl = await driver.GetStreamUrlAsync(cam);
            var main = StreamUrlBuilder.ApplyQuality(baseUrl, StreamUrlBuilder.Quality.Main,
                StreamUrlBuilder.ResolveChannel(cam, perfis, StreamUrlBuilder.Quality.Main));
            // 1 pull nativo (main). Sub só se SingleCameraRtspPull=false.
            await media.RegisterAsync(cam.Id, main, substream: false);
            if (!opt.Value.SingleCameraRtspPull)
            {
                var sub = StreamUrlBuilder.ApplyQuality(baseUrl, StreamUrlBuilder.Quality.Sub,
                    StreamUrlBuilder.ResolveChannel(cam, perfis, StreamUrlBuilder.Quality.Sub));
                await media.RegisterAsync(cam.Id, sub, substream: true);
            }
            await audit.WriteAsync(ctx, "camera.create", "camera", cam.Id.ToString());

            return Results.Created($"/api/vms/cameras/{cam.Id}",
                new { cam.Id, cam.Name, cam.Host, cam.Status });
        }).RequirePermission(Permissions.CameraConfig);

        g.MapDelete("/cameras/{id:int}", async (
            int id, bool keepFiles, HttpContext ctx, PlatformDbContext db,
            MediaGateway media, AuditService audit, IOptions<VmsOptions> opt,
            RecorderService recorder) =>
        {
            var cam = await db.Devices.FirstOrDefaultAsync(d => d.Id == id && d.Kind == DeviceKind.Camera);
            if (cam is null) return Results.NotFound(new { error = "Câmera não encontrada." });

            // Para o FFmpeg antes de apagar pasta/registro — arquivo aberto
            // trava a exclusão no Windows e deixa a UI achando que "não excluiu".
            recorder.StopDevice(id);
            await Task.Delay(400);

            // Sem a limpeza, as gravacoes viram registros orfaos no banco e
            // arquivos que nenhuma rotina de retencao volta a visitar.
            var gravacoes = await db.Recordings.Where(r => r.DeviceId == id).ToListAsync();
            var protegidas = gravacoes.Count(r => r.Protected);

            db.Recordings.RemoveRange(gravacoes);
            db.Bookmarks.RemoveRange(await db.Bookmarks.Where(b => b.DeviceId == id).ToListAsync());
            db.CameraGroupMembers.RemoveRange(
                await db.CameraGroupMembers.Where(m => m.DeviceId == id).ToListAsync());
            db.ScheduleSlots.RemoveRange(
                await db.ScheduleSlots.Where(s => s.DeviceId == id).ToListAsync());

            // Direito apontando para camera excluida concederia acesso ao
            // proximo dispositivo que herdar o id.
            db.ObjectRights.RemoveRange(await db.ObjectRights
                .Where(r => r.ObjectType == ObjectTypes.Camera && r.ObjectId == id)
                .ToListAsync());

            // Eventos e regras de automação: limpa vínculo sem apagar histórico
            // de eventos (DeviceId nulo) — só desassocia.
            await db.Events.Where(e => e.DeviceId == id)
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.DeviceId, (int?)null));
            await db.AutomationRules.Where(r => r.WhenDeviceId == id)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.WhenDeviceId, (int?)null));

            db.Devices.Remove(cam);
            await db.SaveChangesAsync();
            await media.RemoveAsync(id);

            var apagados = 0;
            if (!keepFiles)
            {
                var dir = Path.Combine(opt.Value.StoragePath, id.ToString());
                apagados = RemoveDirectory(dir);
            }

            await audit.WriteAsync(ctx, "camera.delete", "camera", id.ToString(),
                detail: $"gravacoes={gravacoes.Count} (protegidas={protegidas}), arquivos={apagados}");

            return Results.Ok(new
            {
                removida = id,
                gravacoesRemovidas = gravacoes.Count,
                arquivosRemovidos = apagados,
                gravacoesProtegidasDescartadas = protegidas
            });
        }).RequirePermission(Permissions.CameraConfig);

        // quality=sub (padrão, grid rápido) | quality=main (tela cheia / detalhe)
        g.MapGet("/cameras/{id:int}/stream", async (
            int id, string? quality, HttpContext ctx, PlatformDbContext db,
            DriverRegistry registry, MediaGateway media, PermissionService perms,
            IOptions<VmsOptions> opt) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraView, ObjectTypes.Camera, id))
                return Results.Forbid();

            var cam = await db.Devices.FindAsync(id);
            if (cam is null) return Results.NotFound();

            var q = StreamUrlBuilder.ParseQuality(quality);
            var sub = q == StreamUrlBuilder.Quality.Sub;

            var perfis = await db.MediaProfiles.AsNoTracking().ToDictionaryAsync(p => p.Id);
            var baseUrl = await registry.Resolve(cam).GetStreamUrlAsync(cam);
            var channel = StreamUrlBuilder.ResolveChannel(cam, perfis, q);
            var rtsp = StreamUrlBuilder.ApplyQuality(baseUrl, q, channel);

            // Único pull nativo: main. Sub nativo só se SingleCameraRtspPull=false.
            var mainCh = StreamUrlBuilder.ResolveChannel(cam, perfis, StreamUrlBuilder.Quality.Main);
            var mainRtsp = StreamUrlBuilder.ApplyQuality(baseUrl, StreamUrlBuilder.Quality.Main, mainCh);
            await media.RegisterAsync(cam.Id, mainRtsp, substream: false);
            var allowSubNative = !opt.Value.SingleCameraRtspPull;
            if (sub && allowSubNative)
                await media.RegisterAsync(cam.Id, rtsp, substream: true);

            var publicHost = ResolveMediaPublicHost(opt.Value, ctx);

            // Live: preferir camNtc (H.264 a partir do main). Nunca abre 2º RTSP
            // nativo quando SingleCameraRtspPull=true — "sub" vira o mesmo pull.
            var useTc = opt.Value.TranscodeLive;
            StreamUrls urls;
            string pathName;
            bool ready;
            if (useTc)
            {
                pathName = LiveTranscodeService.TranscodePathName(cam.Id);
                var host = publicHost.TrimEnd('/');
                urls = new StreamUrls(
                    mainRtsp,
                    $"{host}:{opt.Value.HlsPort}/{pathName}/index.m3u8",
                    $"{host}:{opt.Value.WebRtcPort}/{pathName}");
                ready = await media.IsPathReadyAsync(pathName);
                if (!ready)
                {
                    await media.RegisterPublisherAsync(pathName);
                    ready = await media.IsPathReadyAsync(pathName);
                }
                if (!ready)
                {
                    // Fallback: main no MediaMTX (ainda 1 pull nativo).
                    var mainReady = await media.IsReadyAsync(cam.Id, substream: false);
                    if (mainReady)
                    {
                        urls = media.UrlsFor(cam.Id, mainRtsp, publicHost, substream: false);
                        pathName = MediaGateway.PathName(cam.Id, substream: false);
                        ready = true;
                        useTc = false;
                    }
                    else if (allowSubNative && sub
                             && await media.IsReadyAsync(cam.Id, substream: true))
                    {
                        urls = media.UrlsFor(cam.Id, rtsp, publicHost, substream: true);
                        pathName = MediaGateway.PathName(cam.Id, substream: true);
                        ready = true;
                        useTc = false;
                    }
                }
            }
            else if (allowSubNative && sub)
            {
                pathName = MediaGateway.PathName(cam.Id, substream: true);
                urls = media.UrlsFor(cam.Id, rtsp, publicHost, substream: true);
                ready = await media.IsReadyAsync(cam.Id, substream: true);
                // Se sub nativo ainda não subiu, usa main (1 sessão).
                if (!ready)
                {
                    pathName = MediaGateway.PathName(cam.Id, substream: false);
                    urls = media.UrlsFor(cam.Id, mainRtsp, publicHost, substream: false);
                    ready = await media.IsReadyAsync(cam.Id, substream: false);
                }
            }
            else
            {
                // Single-pull ou quality=main: sempre o path principal.
                pathName = MediaGateway.PathName(cam.Id, substream: false);
                urls = media.UrlsFor(cam.Id, mainRtsp, publicHost, substream: false);
                ready = await media.IsReadyAsync(cam.Id, substream: false);
            }

            var jwt = Uri.EscapeDataString(
                ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", "").Trim());

            return Results.Ok(new
            {
                hls = AppendQuery(urls.Hls, "jwt", jwt),
                webRtc = AppendQuery(urls.WebRtc, "jwt", jwt),
                ready,
                quality = useTc ? "main" : (sub ? "sub" : "main"),
                path = pathName,
                transcoded = useTc,
                codecHint = useTc ? "H264" : null
            });
        });

        // Talk-back: abre canal e envia áudio (base64 G.711/PCM conforme fabricante).
        g.MapPost("/cameras/{id:int}/talk/open", async (
            int id, HttpContext ctx, PlatformDbContext db,
            DriverRegistry registry, PermissionService perms, AuditService audit) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraPtz, ObjectTypes.Camera, id)
                && !await perms.HasAsync(ctx.User.UserId(), Permissions.CameraView, ObjectTypes.Camera, id))
                return Results.Forbid();

            var cam = await db.Devices.FindAsync(id);
            if (cam is null) return Results.NotFound();

            var result = await registry.Resolve(cam).CommandAsync(cam, "talk_open");
            await audit.WriteAsync(ctx, "camera.talk_open", "camera", id.ToString(), result.Ok);
            return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
        });

        g.MapPost("/cameras/{id:int}/talk/close", async (
            int id, HttpContext ctx, PlatformDbContext db,
            DriverRegistry registry, PermissionService perms) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraView, ObjectTypes.Camera, id))
                return Results.Forbid();
            var cam = await db.Devices.FindAsync(id);
            if (cam is null) return Results.NotFound();
            var result = await registry.Resolve(cam).CommandAsync(cam, "talk_close");
            return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
        });

        g.MapPost("/cameras/{id:int}/talk", async (
            int id, TalkInput input, HttpContext ctx, PlatformDbContext db,
            DriverRegistry registry, PermissionService perms, AuditService audit) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraView, ObjectTypes.Camera, id))
                return Results.Forbid();
            var cam = await db.Devices.FindAsync(id);
            if (cam is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(input.Base64))
                return Results.BadRequest(new { error = "base64 do áudio é obrigatório." });

            var driver = registry.Resolve(cam);
            await driver.CommandAsync(cam, "talk_open");
            var result = await driver.CommandAsync(cam, "talk_send",
                new Dictionary<string, string> { ["base64"] = input.Base64 });
            await audit.WriteAsync(ctx, "camera.talk_send", "camera", id.ToString(), result.Ok);
            return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
        });

        // Imagem parada da camera — usada como poster do grid e na verificacao
        // rapida de que o equipamento esta enviando video.
        g.MapGet("/cameras/{id:int}/snapshot", async (
            int id, HttpContext ctx, PlatformDbContext db,
            DriverRegistry registry, PermissionService perms, RecordingExporter exporter,
            MediaGateway media) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraView, ObjectTypes.Camera, id))
                return Results.Forbid();

            var cam = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
            if (cam is null) return Results.NotFound();

            var driver = registry.Resolve(cam);

            // Caminho preferido: o proprio equipamento entrega o JPEG (sem RTSP extra).
            var nativo = await driver.CommandAsync(cam, "snapshot");
            if (nativo.Ok && nativo.Data?.TryGetValue("base64", out var imagem) == true)
                return Results.File(Convert.FromBase64String(imagem),
                    nativo.Data.TryGetValue("contentType", out var tipo) ? tipo : "image/jpeg");

            // Fallback: quadro via MediaMTX (1 pull nativo). Direto na câmera só se gateway falhar.
            string? rtsp = null;
            try
            {
                var baseUrl = await driver.GetStreamUrlAsync(cam);
                await media.RegisterAsync(cam.Id, baseUrl, substream: false, ctx.RequestAborted);
                if (await media.IsReadyAsync(cam.Id, substream: false, ctx.RequestAborted))
                    rtsp = media.LocalRtspUrl(cam.Id, substream: false);
            }
            catch { /* tenta direto */ }

            rtsp ??= await driver.GetStreamUrlAsync(cam);
            var jpeg = await exporter.GrabFrameAsync(rtsp, ctx.RequestAborted);

            return jpeg is null
                ? Results.Problem("Nao foi possivel obter imagem da camera.", statusCode: 503)
                : Results.File(jpeg, "image/jpeg");
        });
    }

    // ------------------------------------------------------------------ ptz

    private static void MapPtz(RouteGroupBuilder g)
    {
        // Comando generico do driver (open_door, arm, relay_on, device_info...).
        g.MapPost("/cameras/{id:int}/command/{action}", async (
            int id, string action, Dictionary<string, string>? parameters, HttpContext ctx,
            PlatformDbContext db, DriverRegistry registry, PermissionService perms, AuditService audit) =>
        {
            var needed = action.StartsWith("ptz", StringComparison.Ordinal) ? Permissions.CameraPtz
                : action.StartsWith("vca", StringComparison.Ordinal) ? Permissions.CameraConfig
                : action.StartsWith("encode", StringComparison.Ordinal)
                    || action == "set_codec" ? Permissions.CameraConfig
                : Permissions.CameraView;

            if (!await perms.HasAsync(ctx.User.UserId(), needed, ObjectTypes.Camera, id))
                return Results.Forbid();

            var cam = await db.Devices.FindAsync(id);
            if (cam is null) return Results.NotFound();

            var result = await registry.Resolve(cam).CommandAsync(cam, action, parameters);
            await audit.WriteAsync(ctx, $"camera.{action}", "camera", id.ToString(), result.Ok);

            return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
        });

        // Config remota de analitico (VCA) na camera — ROI / regras basicas.
        g.MapPost("/cameras/{id:int}/vca", async (
            int id, VcaConfigInput input, HttpContext ctx,
            PlatformDbContext db, DriverRegistry registry, PermissionService perms, AuditService audit) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraConfig, ObjectTypes.Camera, id))
                return Results.Forbid();

            var cam = await db.Devices.FindAsync(id);
            if (cam is null) return Results.NotFound();

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["rule"] = input.Rule ?? "motion",
                ["enabled"] = (input.Enabled ?? true) ? "true" : "false",
                ["channel"] = input.Channel ?? "1"
            };
            if (input.Roi is not null)
            {
                parameters["x"] = input.Roi.X.ToString(System.Globalization.CultureInfo.InvariantCulture);
                parameters["y"] = input.Roi.Y.ToString(System.Globalization.CultureInfo.InvariantCulture);
                parameters["w"] = input.Roi.W.ToString(System.Globalization.CultureInfo.InvariantCulture);
                parameters["h"] = input.Roi.H.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (!string.IsNullOrWhiteSpace(input.Name))
                parameters["name"] = input.Name!;

            var result = await registry.Resolve(cam).CommandAsync(cam, "vca_configure", parameters);
            await audit.WriteAsync(ctx, "camera.vca_configure", "camera", id.ToString(), result.Ok,
                detail: input.Rule ?? "motion");

            return result.Ok
                ? Results.Ok(new { ok = true, driver = cam.Driver, data = result.Data })
                : Results.BadRequest(new { error = result.Error, driver = cam.Driver });
        });

        // Movimento continuo: o cliente envia enquanto o operador segura o
        // controle e chama /ptz/stop ao soltar. Um unico endpoint para pan,
        // tilt e zoom porque o driver recebe os tres eixos de uma vez.
        g.MapPost("/cameras/{id:int}/ptz/move", async (
            int id, PtzMove move, HttpContext ctx, PlatformDbContext db,
            DriverRegistry registry, PermissionService perms) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraPtz, ObjectTypes.Camera, id))
                return Results.Forbid();

            var cam = await db.Devices.FindAsync(id);
            if (cam is null) return Results.NotFound();

            // Velocidade fora de [-1,1] faz alguns equipamentos ignorarem o
            // comando inteiro em vez de saturarem.
            var parametros = new Dictionary<string, string>
            {
                ["pan"] = Clamp(move.Pan).ToString("0.00"),
                ["tilt"] = Clamp(move.Tilt).ToString("0.00"),
                ["zoom"] = Clamp(move.Zoom).ToString("0.00"),
                ["timeout"] = Math.Clamp(move.TimeoutSeconds, 1, 30).ToString()
            };

            var result = await registry.Resolve(cam).CommandAsync(cam, "ptz_move", parametros);
            return result.Ok ? Results.Ok(result) : Results.BadRequest(result);

            static double Clamp(double v) => Math.Clamp(v, -1, 1);
        });

        g.MapPost("/cameras/{id:int}/ptz/stop", async (
            int id, HttpContext ctx, PlatformDbContext db,
            DriverRegistry registry, PermissionService perms) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraPtz, ObjectTypes.Camera, id))
                return Results.Forbid();

            var cam = await db.Devices.FindAsync(id);
            if (cam is null) return Results.NotFound();

            var result = await registry.Resolve(cam).CommandAsync(cam, "ptz_stop");
            return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
        });

        g.MapGet("/cameras/{id:int}/ptz/presets", async (
            int id, HttpContext ctx, PlatformDbContext db,
            DriverRegistry registry, PermissionService perms) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraView, ObjectTypes.Camera, id))
                return Results.Forbid();

            var cam = await db.Devices.FindAsync(id);
            if (cam is null) return Results.NotFound();

            var result = await registry.Resolve(cam).CommandAsync(cam, "ptz_preset_list");
            return result.Ok ? Results.Ok(result.Data) : Results.BadRequest(result);
        });

        // Gravar a posicao atual como preset numerado.
        g.MapPut("/cameras/{id:int}/ptz/presets/{preset}", async (
            int id, string preset, PresetInput? input, HttpContext ctx,
            PlatformDbContext db, DriverRegistry registry,
            PermissionService perms, AuditService audit) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraPtz, ObjectTypes.Camera, id))
                return Results.Forbid();

            var cam = await db.Devices.FindAsync(id);
            if (cam is null) return Results.NotFound();

            var result = await registry.Resolve(cam).CommandAsync(cam, "ptz_preset_set",
                new Dictionary<string, string>
                {
                    ["preset"] = preset,
                    ["name"] = input?.Name ?? preset
                });

            await audit.WriteAsync(ctx, "camera.ptz_preset_set", "camera", id.ToString(), result.Ok);
            return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
        });

        // Patrulha simples: cicla presets com dwell (em memória no nó).
        g.MapPost("/cameras/{id:int}/ptz/tour/start", async (
            int id, TourInput input, HttpContext ctx, PlatformDbContext db,
            PermissionService perms, PtzTourService tours) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraPtz, ObjectTypes.Camera, id))
                return Results.Forbid();
            if (!await db.Devices.AnyAsync(d => d.Id == id && d.Kind == DeviceKind.Camera))
                return Results.NotFound();

            var presets = (input.Presets ?? ["1", "2", "3"])
                .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
            if (presets.Count == 0)
                return Results.BadRequest(new { error = "Informe ao menos um preset." });

            tours.Start(id, presets, input.DwellSeconds);
            return Results.Ok(new { running = true, presets, dwellSeconds = Math.Clamp(input.DwellSeconds, 2, 300) });
        });

        g.MapPost("/cameras/{id:int}/ptz/tour/stop", async (
            int id, HttpContext ctx, PlatformDbContext db, PermissionService perms, PtzTourService tours) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraPtz, ObjectTypes.Camera, id))
                return Results.Forbid();
            var stopped = tours.Stop(id);
            return Results.Ok(new { running = false, stopped });
        });

        g.MapGet("/cameras/{id:int}/ptz/tour", async (
            int id, HttpContext ctx, PlatformDbContext db, PermissionService perms, PtzTourService tours) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraPtz, ObjectTypes.Camera, id))
                return Results.Forbid();
            return Results.Ok(new { running = tours.IsRunning(id) });
        });
    }

    // --------------------------------------------------------------- playback

    private static void MapPlayback(RouteGroupBuilder g)
    {
        g.MapGet("/cameras/{id:int}/recordings", async (
            int id, HttpContext ctx, PlatformDbContext db, PermissionService perms,
            DateTime? from, DateTime? to, int page = 1, int pageSize = 200) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraPlayback, ObjectTypes.Camera, id))
                return Results.Forbid();

            var q = db.Recordings.Where(r => r.DeviceId == id);
            if (from is not null) q = q.Where(r => r.StartedAt >= from);
            if (to is not null) q = q.Where(r => r.StartedAt <= to);

            // Paginacao real: o Take(500) fixo escondia gravacao antiga sem
            // qualquer sinal de que havia mais.
            var total = await q.CountAsync();
            pageSize = Math.Clamp(pageSize, 1, 500);
            page = Math.Max(page, 1);

            var itens = await q.OrderByDescending(r => r.StartedAt)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .AsNoTracking().ToListAsync();

            return Results.Ok(new { total, page, pageSize, itens });
        });

        // Dias com gravação (calendário do monitor — bolinha azul no dia).
        g.MapGet("/cameras/{id:int}/recording-days", async (
            int id, HttpContext ctx, PlatformDbContext db, PermissionService perms,
            int? year, int? month, DateTime? from, DateTime? to) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraPlayback, ObjectTypes.Camera, id))
                return Results.Forbid();

            DateTime inicio, fim;
            if (from is not null && to is not null)
            {
                inicio = from.Value;
                fim = to.Value;
            }
            else
            {
                var y = year ?? DateTime.UtcNow.Year;
                var m = month ?? DateTime.UtcNow.Month;
                inicio = new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc);
                fim = inicio.AddMonths(1);
            }

            var rows = await db.Recordings.AsNoTracking()
                .Where(r => r.DeviceId == id && r.StartedAt >= inicio && r.StartedAt < fim)
                .Select(r => r.StartedAt)
                .ToListAsync();

            // Agrupa por dia local do operador (UTC date do carimbo — client interpreta).
            var days = rows
                .GroupBy(t => t.Date)
                .Select(g => new { date = g.Key.ToString("yyyy-MM-dd"), count = g.Count() })
                .OrderBy(x => x.date)
                .ToList();

            return Results.Ok(new { from = inicio, to = fim, days });
        });

        // Linha do tempo: blocos continuos de gravacao e os buracos entre eles.
        // Sem isso o operador nao distingue "nada aconteceu" de "nao gravou".
        g.MapGet("/cameras/{id:int}/timeline", async (
            int id, HttpContext ctx, PlatformDbContext db, PermissionService perms,
            IOptions<VmsOptions> opt, DateTime? from, DateTime? to) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraPlayback, ObjectTypes.Camera, id))
                return Results.Forbid();

            var inicio = from ?? DateTime.UtcNow.AddDays(-1);
            var fim = to ?? DateTime.UtcNow;

            var gravacoes = await db.Recordings.AsNoTracking()
                .Where(r => r.DeviceId == id && r.StartedAt <= fim && (r.EndedAt ?? r.StartedAt) >= inicio)
                .OrderBy(r => r.StartedAt)
                .ToListAsync();

            // Segmentos separados por menos de 30s sao o corte normal do
            // gravador, nao uma interrupcao — juntar evita uma timeline picotada.
            var blocosRaw = new List<(DateTime Inicio, DateTime Fim, string Trigger, List<long> Ids)>();
            DateTime? blocoInicio = null, blocoFim = null;
            string trigger = "continuous";
            var idsBloco = new List<long>();

            foreach (var r in gravacoes)
            {
                var rFim = r.EndedAt ?? r.StartedAt.AddMinutes(1);
                if (blocoInicio is null)
                {
                    (blocoInicio, blocoFim, trigger) = (r.StartedAt, rFim, r.Trigger);
                    idsBloco = [r.Id];
                    continue;
                }

                if ((r.StartedAt - blocoFim!.Value).TotalSeconds <= 30 && r.Trigger == trigger)
                {
                    blocoFim = rFim > blocoFim ? rFim : blocoFim;
                    idsBloco.Add(r.Id);
                    continue;
                }

                blocosRaw.Add((blocoInicio.Value, blocoFim!.Value, trigger, idsBloco));
                (blocoInicio, blocoFim, trigger) = (r.StartedAt, rFim, r.Trigger);
                idsBloco = [r.Id];
            }
            if (blocoInicio is not null)
                blocosRaw.Add((blocoInicio.Value, blocoFim!.Value, trigger, idsBloco));

            var blocos = blocosRaw.Select(b => new
            {
                inicio = b.Inicio,
                fim = b.Fim,
                trigger = b.Trigger,
                recordingId = b.Ids.FirstOrDefault(),
                recordingIds = b.Ids.ToArray()
            }).ToList();

            var gaps = new List<object>();
            for (var i = 0; i < blocosRaw.Count - 1; i++)
            {
                var sec = (blocosRaw[i + 1].Inicio - blocosRaw[i].Fim).TotalSeconds;
                if (sec > 30)
                    gaps.Add(new
                    {
                        inicio = blocosRaw[i].Fim,
                        fim = blocosRaw[i + 1].Inicio,
                        seconds = (int)sec
                    });
            }

            var segmentos = gravacoes.Select(r => new
            {
                r.Id,
                r.StartedAt,
                endedAt = r.EndedAt ?? r.StartedAt.AddMinutes(1),
                r.Trigger,
                r.SizeBytes,
                r.Protected
            }).ToList();

            var marcas = await db.Bookmarks.AsNoTracking()
                .Where(b => b.DeviceId == id && b.StartedAt <= fim && b.EndedAt >= inicio)
                .Select(b => new { b.Id, b.Title, b.StartedAt, b.EndedAt })
                .ToListAsync();

            var eventos = await db.Events.AsNoTracking()
                .Where(e => e.DeviceId == id && e.CreatedAt >= inicio && e.CreatedAt <= fim)
                .OrderBy(e => e.CreatedAt)
                .Select(e => new { e.Id, e.Type, e.Severity, e.CreatedAt, e.Payload, e.Acknowledged })
                .Take(1000)
                .ToListAsync();

            var thumbs = ListTimelineThumbs(opt.Value.StoragePath, id, inicio, fim);

            return Results.Ok(new
            {
                de = inicio,
                ate = fim,
                blocos,
                segmentos,
                gaps,
                thumbs,
                bookmarks = marcas,
                eventos
            });
        });

        // Miniatura JPEG da timeline.
        g.MapGet("/cameras/{id:int}/thumbs/{stamp}", async (
            int id, string stamp, HttpContext ctx, PermissionService perms, IOptions<VmsOptions> opt) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraPlayback, ObjectTypes.Camera, id))
                return Results.Forbid();

            if (stamp.Length is < 8 or > 20 || stamp.Any(c => !(char.IsDigit(c) || c == '_')))
                return Results.BadRequest(new { error = "stamp inválido (yyyyMMdd_HHmm)." });

            var path = ThumbnailService.ThumbPath(opt.Value.StoragePath, id,
                DateTime.TryParseExact(stamp, "yyyyMMdd_HHmm",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var dt) ? dt : DateTime.UtcNow);

            // Se parse falhou, tenta path direto.
            if (!File.Exists(path))
                path = Path.Combine(ThumbnailService.ThumbsRoot(opt.Value.StoragePath), id.ToString(), stamp + ".jpg");

            if (!File.Exists(path)) return Results.NotFound();
            if (!StoragePaths.IsInside(path, ThumbnailService.ThumbsRoot(opt.Value.StoragePath)))
                return Results.Forbid();

            return Results.File(path, "image/jpeg");
        });

        // Busca smart: eventos de movimento/metadados + segmento de gravação mais próximo.
        g.MapGet("/cameras/{id:int}/search", async (
            int id, HttpContext ctx, PlatformDbContext db, PermissionService perms,
            string? type, DateTime? from, DateTime? to, int take = 100) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraPlayback, ObjectTypes.Camera, id))
                return Results.Forbid();

            var inicio = from ?? DateTime.UtcNow.AddDays(-1);
            var fim = to ?? DateTime.UtcNow;
            take = Math.Clamp(take, 1, 500);

            var q = db.Events.AsNoTracking()
                .Where(e => e.DeviceId == id && e.CreatedAt >= inicio && e.CreatedAt <= fim);

            if (!string.IsNullOrWhiteSpace(type))
            {
                var t = type.Trim().ToLowerInvariant();
                // Atalhos: motion = vários tipos de movimento
                if (t is "motion" or "movimento")
                    q = q.Where(e =>
                        e.Type.Contains("motion") || e.Type.Contains("VMD")
                        || e.Type.Contains("intrusion") || e.Type.Contains("line")
                        || e.Type.Contains("crossed") || e.Type == "video_loss");
                else
                    q = q.Where(e => e.Type.Contains(type));
            }

            var eventos = await q.OrderByDescending(e => e.CreatedAt).Take(take).ToListAsync();

            var segs = await db.Recordings.AsNoTracking()
                .Where(r => r.DeviceId == id && r.StartedAt <= fim && (r.EndedAt ?? r.StartedAt) >= inicio)
                .OrderBy(r => r.StartedAt)
                .Select(r => new { r.Id, r.StartedAt, r.EndedAt, r.Trigger, r.SizeBytes })
                .ToListAsync();

            var hits = eventos.Select(e =>
            {
                var rec = segs
                    .Where(s => s.StartedAt <= e.CreatedAt && (s.EndedAt ?? s.StartedAt.AddMinutes(10)) >= e.CreatedAt)
                    .OrderBy(s => Math.Abs((s.StartedAt - e.CreatedAt).TotalSeconds))
                    .FirstOrDefault()
                    ?? segs.OrderBy(s => Math.Abs((s.StartedAt - e.CreatedAt).TotalSeconds)).FirstOrDefault();

                return new
                {
                    eventId = e.Id,
                    e.Type,
                    e.Severity,
                    at = e.CreatedAt,
                    e.Payload,
                    recordingId = rec?.Id,
                    recordingStartedAt = rec?.StartedAt,
                    recordingTrigger = rec?.Trigger
                };
            }).ToList();

            return Results.Ok(new { de = inicio, ate = fim, total = hits.Count, hits });
        });

        // Playback de um segmento: exige direito na camera dona da gravacao.
        // Normaliza HEVC/fMP4 → H.264 progressivo on-demand (cache sidecar).
        g.MapGet("/recordings/{recordingId:long}/file", async (
            long recordingId, HttpContext ctx, PlatformDbContext db,
            PermissionService perms, AuditService audit, IOptions<VmsOptions> opt,
            RecordingCrypto crypto, RecordingNormalizer normalizer) =>
        {
            var rec = await db.Recordings.FindAsync(recordingId);
            if (rec is null) return Results.NotFound();

            // Resolve path relativo legado (./data/recordings\…) → absoluto no StoragePath.
            var storageRoot = opt.Value.StoragePath;
            var resolved = StoragePaths.ResolveExisting(rec.Path, storageRoot);
            if (resolved is null)
                return Results.NotFound(new { error = "Arquivo de gravação ausente no disco.", path = rec.Path });

            // Atualiza path absoluto no banco (migração lazy — evita 500 futuros).
            if (!string.Equals(rec.Path, resolved, StringComparison.OrdinalIgnoreCase))
            {
                rec.Path = resolved;
                try { await db.SaveChangesAsync(); } catch { /* best-effort */ }
            }

            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraPlayback, ObjectTypes.Camera, rec.DeviceId))
                return Results.Forbid();

            if (!StoragePaths.IsInside(resolved, storageRoot))
                return Results.Forbid();

            // Auditoria só no request completo (não em cada Range parcial).
            if (!ctx.Request.Headers.ContainsKey("Range"))
                await audit.WriteAsync(ctx, "recording.play", "recording", recordingId.ToString());

            var temps = new List<string>();
            try
            {
                // Fast path: sidecar .browser.mp4 já convertido (sem ffprobe).
                if (!rec.Encrypted && !RecordingCrypto.IsEncryptedPath(resolved))
                {
                    var sidecar = RecordingNormalizer.BrowserCachePath(resolved);
                    if (File.Exists(sidecar) && RecordingNormalizer.IsLikelyProgressiveH264(sidecar)
                        && File.GetLastWriteTimeUtc(sidecar) >= File.GetLastWriteTimeUtc(resolved).AddSeconds(-2))
                    {
                        return Results.File(sidecar, "video/mp4",
                            fileDownloadName: $"recording_{recordingId}.mp4",
                            enableRangeProcessing: true);
                    }

                    // Original já H.264 progressivo com moov legível.
                    if (RecordingNormalizer.IsLikelyProgressiveH264(resolved))
                    {
                        return Results.File(resolved, "video/mp4",
                            fileDownloadName: $"recording_{recordingId}.mp4",
                            enableRangeProcessing: true);
                    }
                }

                string sourcePath;
                if (rec.Encrypted || RecordingCrypto.IsEncryptedPath(resolved))
                {
                    var plain = crypto.DecryptToTemp(resolved);
                    temps.Add(plain);
                    sourcePath = plain;
                }
                else
                {
                    sourcePath = resolved;
                }

                string playable;
                try
                {
                    playable = await normalizer.EnsurePlayableAsync(sourcePath, ctx.RequestAborted);
                }
                catch (Exception e)
                {
                    return Results.Problem(
                        $"Falha ao preparar gravação para o browser: {e.Message}",
                        statusCode: 500);
                }

                if (!string.Equals(playable, resolved, StringComparison.OrdinalIgnoreCase)
                    && !StoragePaths.IsInside(playable, storageRoot))
                {
                    temps.Add(playable);
                }

                if (temps.Count > 0)
                {
                    var toClean = temps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    ctx.Response.OnCompleted(() =>
                    {
                        foreach (var t in toClean)
                        {
                            try { if (File.Exists(t)) File.Delete(t); } catch (IOException) { }
                        }
                        return Task.CompletedTask;
                    });
                }

                return Results.File(playable, "video/mp4",
                    fileDownloadName: $"recording_{recordingId}.mp4",
                    enableRangeProcessing: true);
            }
            catch (Exception e) when (e is InvalidDataException or CryptographicException or IOException)
            {
                foreach (var t in temps)
                    try { if (File.Exists(t)) File.Delete(t); } catch (IOException) { }
                return Results.Problem($"Falha ao abrir gravação: {e.Message}", statusCode: 500);
            }
        });

        // Exportacao de um intervalo: junta os segmentos que o cobrem em um MP4
        // unico. Antes so era possivel baixar o segmento bruto, o que obrigava o
        // operador a remontar o incidente por fora.
        g.MapPost("/cameras/{id:int}/export", async (
            int id, ExportRequest req, HttpContext ctx, PlatformDbContext db,
            PermissionService perms, AuditService audit, RecordingExporter exporter,
            ExportSigner signer, IOptions<VmsOptions> opt, VmsMetrics metrics) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraExport, ObjectTypes.Camera, id))
                return Results.Forbid();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var from = req.From.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(req.From, DateTimeKind.Utc) : req.From.ToUniversalTime();
            var to = req.To.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(req.To, DateTimeKind.Utc) : req.To.ToUniversalTime();

            if (to <= from)
                return Results.BadRequest(new { error = "Intervalo invalido: o fim deve ser apos o inicio." });

            var minutos = (to - from).TotalMinutes;
            if (minutos > opt.Value.MaxExportMinutes)
                return Results.BadRequest(new
                {
                    error = $"Intervalo de {minutos:0} min excede o maximo de {opt.Value.MaxExportMinutes} min."
                });
            if (minutos < 1.0 / 60.0)
                return Results.BadRequest(new { error = "Intervalo muito curto (minimo 1 segundo)." });

            var segmentos = await db.Recordings.AsNoTracking()
                .Where(r => r.DeviceId == id
                    && r.StartedAt <= to
                    && (r.EndedAt ?? r.StartedAt.AddHours(6)) >= from)
                .OrderBy(r => r.StartedAt)
                .ToListAsync();

            if (segmentos.Count == 0)
                return Results.NotFound(new { error = "Nao ha gravacao no intervalo pedido." });

            var settings = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == ctx.User.UserId());
            var masks = await db.PrivacyMasks.AsNoTracking()
                .Where(m => m.DeviceId == id && m.Enabled)
                .ToListAsync();
            var privacyBoxes = masks
                .SelectMany(m => PrivacyMaskHelper.ToBoundingBoxes(PrivacyMaskHelper.Parse(m.PolygonsJson)))
                .ToList();
            var exportOpts = new ExportOptions(
                Watermark: settings?.WatermarkExport == true,
                UserName: user?.Username ?? ctx.User.Identity?.Name,
                ServerName: settings?.ServerName,
                BlurFaces: settings?.BlurFacesOnExport == true,
                PrivacyBoxes: privacyBoxes.Count > 0 ? privacyBoxes : null);

            var resultado = await exporter.ExportAsync(
                id, segmentos, from, to, exportOpts, ctx.RequestAborted);

            string? signature = null;
            string sha256 = "";
            long len = 0;
            if (resultado.Ok && resultado.Path is not null
                && File.Exists(resultado.Path)
                && new FileInfo(resultado.Path).Length >= RecordingExporter.MinExportBytes)
            {
                signature = signer.SignFile(resultado.Path);
                len = new FileInfo(resultado.Path).Length;
                sha256 = ComputeSha256Hex(resultado.Path);

                db.ExportRecords.Add(new ExportRecord
                {
                    TenantId = ctx.User.TenantId(),
                    DeviceId = id,
                    UserId = ctx.User.UserId(),
                    UserName = user?.Username ?? ctx.User.Identity?.Name ?? "",
                    FromUtc = from,
                    ToUtc = to,
                    FileName = Path.GetFileName(resultado.Path),
                    FilePath = resultado.Path,
                    SizeBytes = len,
                    Sha256 = sha256,
                    Signature = signature,
                    Watermark = exportOpts.Watermark,
                    BlurFaces = exportOpts.BlurFaces,
                    SegmentCount = segmentos.Count
                });
                await db.SaveChangesAsync();
            }
            else if (resultado.Ok)
            {
                resultado = new ExportResult(false, null,
                    "Arquivo exportado invalido (muito pequeno). Verifique o intervalo e as gravacoes.");
            }

            metrics.IncExport(sw.ElapsedMilliseconds);
            await audit.WriteAsync(ctx, "recording.export", "camera", id.ToString(), resultado.Ok,
                detail: $"{from:o} .. {to:o} ({segmentos.Count} segmentos, sha256={sha256[..Math.Min(16, sha256.Length)]}…, sig={(signature is null ? "none" : signature[..Math.Min(16, signature.Length)] + "…")})");

            if (!resultado.Ok)
                return Results.Json(new { error = resultado.Error ?? "Falha na exportacao." },
                    statusCode: 500);

            ctx.Response.Headers["X-Export-Bytes"] = len.ToString();
            ctx.Response.Headers["X-Export-Sha256"] = sha256;
            if (!string.IsNullOrEmpty(signature))
                ctx.Response.Headers["X-Export-Signature"] = signature;

            return Results.File(resultado.Path!, "video/mp4",
                fileDownloadName: $"camera{id}_{from:yyyyMMdd_HHmmss}.mp4",
                enableRangeProcessing: true);
        });

        g.MapGet("/export/info", async (HttpContext ctx, PlatformDbContext db, IOptions<VmsOptions> opt) =>
        {
            var settings = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1);
            return Results.Ok(new
            {
                maxExportMinutes = opt.Value.MaxExportMinutes,
                watermark = settings?.WatermarkExport == true,
                blurFaces = settings?.BlurFacesOnExport == true,
                serverName = settings?.ServerName ?? "SecurityPlatform"
            });
        });

        // Cadeia de custódia: lista exports recentes.
        g.MapGet("/export/records", async (HttpContext ctx, PlatformDbContext db, int take = 50) =>
        {
            take = Math.Clamp(take, 1, 200);
            var tid = ctx.User.TenantId();
            var rows = await db.ExportRecords.AsNoTracking()
                .Where(e => e.TenantId == tid)
                .OrderByDescending(e => e.CreatedAt)
                .Take(take)
                .Select(e => new
                {
                    e.Id, e.DeviceId, e.UserName, e.FromUtc, e.ToUtc,
                    e.FileName, e.SizeBytes, e.Sha256, e.Signature,
                    e.Watermark, e.BlurFaces, e.SegmentCount, e.CreatedAt
                })
                .ToListAsync();
            return Results.Ok(rows);
        });

        // Verifica integridade de um export (hash + assinatura HMAC).
        g.MapPost("/export/verify", async (
            ExportVerifyRequest req, HttpContext ctx, PlatformDbContext db, ExportSigner signer) =>
        {
            if (!ctx.User.IsAdmin()) return Results.Forbid();

            ExportRecord? rec = null;
            if (req.ExportId is long eid)
                rec = await db.ExportRecords.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eid);

            var path = req.FilePath ?? rec?.FilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return Results.NotFound(new { error = "Arquivo de export não encontrado." });

            var sha = ComputeSha256Hex(path);
            var shaOk = rec is null || string.Equals(sha, rec.Sha256, StringComparison.OrdinalIgnoreCase);
            var sigOk = signer.Verify(path, rec?.Signature);
            var actualSig = signer.ComputeSignature(path);

            return Results.Ok(new
            {
                path,
                sha256 = sha,
                shaMatch = shaOk,
                signature = actualSig,
                signatureValid = sigOk,
                exportId = rec?.Id,
                recordedSha256 = rec?.Sha256
            });
        });

        // Log de purge LGPD (somente leitura).
        g.MapGet("/retention/purge-log", async (
            HttpContext ctx, PlatformDbContext db, int? deviceId, int take = 100) =>
        {
            take = Math.Clamp(take, 1, 500);
            var q = db.RetentionPurgeLogs.AsNoTracking()
                .Where(p => p.TenantId == ctx.User.TenantId());
            if (deviceId is int did) q = q.Where(p => p.DeviceId == did);
            if (!ctx.User.IsAdmin()) return Results.Forbid();
            var rows = await q.OrderByDescending(p => p.PurgedAt).Take(take).ToListAsync();
            return Results.Ok(rows);
        });

        // Máscaras de privacidade por câmera.
        g.MapGet("/cameras/{id:int}/privacy-masks", async (
            int id, HttpContext ctx, PlatformDbContext db, PermissionService perms) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraView, ObjectTypes.Camera, id))
                return Results.Forbid();
            var masks = await db.PrivacyMasks.AsNoTracking()
                .Where(m => m.DeviceId == id)
                .ToListAsync();
            return Results.Ok(masks);
        });

        g.MapPut("/cameras/{id:int}/privacy-masks", async (
            int id, PrivacyMaskInput input, HttpContext ctx, PlatformDbContext db,
            PermissionService perms, AuditService audit) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraConfig, ObjectTypes.Camera, id))
                return Results.Forbid();

            var existing = await db.PrivacyMasks.Where(m => m.DeviceId == id).ToListAsync();
            db.PrivacyMasks.RemoveRange(existing);
            db.PrivacyMasks.Add(new PrivacyMask
            {
                TenantId = ctx.User.TenantId(),
                DeviceId = id,
                Name = string.IsNullOrWhiteSpace(input.Name) ? "mask" : input.Name.Trim(),
                PolygonsJson = string.IsNullOrWhiteSpace(input.PolygonsJson) ? "[]" : input.PolygonsJson,
                Enabled = input.Enabled
            });
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "camera.privacy_mask", "camera", id.ToString(), true);
            return Results.Ok(new { ok = true });
        });
    }

    private static List<object> ListTimelineThumbs(string storageRoot, int deviceId, DateTime from, DateTime to)
    {
        var dir = Path.Combine(ThumbnailService.ThumbsRoot(storageRoot), deviceId.ToString());
        if (!Directory.Exists(dir)) return [];

        var list = new List<object>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.jpg"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (!DateTime.TryParseExact(name, "yyyyMMdd_HHmm",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var when))
                continue;
            if (when < from.AddMinutes(-5) || when > to.AddMinutes(5)) continue;
            list.Add(new
            {
                at = when,
                stamp = name,
                url = $"/api/vms/cameras/{deviceId}/thumbs/{name}"
            });
        }
        return list.OrderBy(x => ((dynamic)x).at).ToList();
    }

    private static string ComputeSha256Hex(string path)
    {
        using var fs = File.OpenRead(path);
        var hash = SHA256.HashData(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // --------------------------------------------------------------- layouts

    private static void MapLayouts(RouteGroupBuilder g)
    {
        g.MapGet("/layouts", async (HttpContext ctx, PlatformDbContext db) =>
        {
            var uid = ctx.User.UserId();
            return await db.MonitorLayouts.AsNoTracking()
                .Where(m => m.UserId == uid)
                .OrderBy(m => m.Name)
                .Select(m => new { m.Id, m.Name, m.LayoutId, m.CellsJson, m.CreatedAt, m.UpdatedAt })
                .ToListAsync();
        });

        g.MapPost("/layouts", async (LayoutInput input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name))
                return Results.BadRequest(new { error = "Nome obrigatório." });

            var uid = ctx.User.UserId();
            var tenant = ctx.User.TenantId();
            var nome = input.Name.Trim();

            var existing = await db.MonitorLayouts
                .FirstOrDefaultAsync(m => m.UserId == uid && m.Name == nome);

            if (existing is not null)
            {
                existing.LayoutId = input.LayoutId ?? existing.LayoutId;
                existing.CellsJson = input.CellsJson ?? "[]";
                existing.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                await audit.WriteAsync(ctx, "layout.update", "layout", existing.Id.ToString());
                return Results.Ok(new { existing.Id, existing.Name, existing.LayoutId });
            }

            var m = new MonitorLayout
            {
                TenantId = tenant,
                UserId = uid,
                Name = nome,
                LayoutId = input.LayoutId ?? "2x2",
                CellsJson = input.CellsJson ?? "[]"
            };
            db.MonitorLayouts.Add(m);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "layout.create", "layout", m.Id.ToString());
            return Results.Created($"/api/vms/layouts/{m.Id}", new { m.Id, m.Name, m.LayoutId });
        });

        g.MapDelete("/layouts/{id:int}", async (int id, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            var uid = ctx.User.UserId();
            var m = await db.MonitorLayouts.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid);
            if (m is null) return Results.NotFound();
            db.MonitorLayouts.Remove(m);
            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "layout.delete", "layout", id.ToString());
            return Results.NoContent();
        });
    }

    // ---------------------------------------------------------------- health

    private static void MapHealth(RouteGroupBuilder g)
    {
        // Saúde por câmera: silêncio de gravação, status e último segmento.
        g.MapGet("/cameras/health", async (
            HttpContext ctx, PlatformDbContext db, PermissionService perms, IOptions<VmsOptions> opt) =>
        {
            var visible = await perms.VisibleCameraIdsAsync(ctx.User.UserId());
            var all = await CameraHealthService.SnapshotAsync(db, opt.Value, ctx.RequestAborted);
            return Results.Ok(all.Where(h => visible.Contains(h.DeviceId)).ToList());
        });

        g.MapGet("/cameras/{id:int}/health", async (
            int id, HttpContext ctx, PlatformDbContext db, PermissionService perms, IOptions<VmsOptions> opt) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraView, ObjectTypes.Camera, id))
                return Results.Forbid();

            var all = await CameraHealthService.SnapshotAsync(db, opt.Value, ctx.RequestAborted);
            var item = all.FirstOrDefault(h => h.DeviceId == id);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });
    }

    // ------------------------------------------------------------- bookmarks

    private static void MapBookmarks(RouteGroupBuilder g)
    {
        g.MapGet("/cameras/{id:int}/bookmarks", async (
            int id, HttpContext ctx, PlatformDbContext db, PermissionService perms) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraPlayback, ObjectTypes.Camera, id))
                return Results.Forbid();

            return Results.Ok(await db.Bookmarks.AsNoTracking()
                .Where(b => b.DeviceId == id)
                .OrderByDescending(b => b.StartedAt).ToListAsync());
        });

        // Marcar um incidente protege as gravacoes do intervalo contra a
        // retencao automatica — a prova nao some porque o prazo venceu.
        g.MapPost("/cameras/{id:int}/bookmarks", async (
            int id, BookmarkInput input, HttpContext ctx, PlatformDbContext db,
            PermissionService perms, AuditService audit) =>
        {
            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraPlayback, ObjectTypes.Camera, id))
                return Results.Forbid();

            var cam = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
            if (cam is null) return Results.NotFound();
            if (input.To <= input.From) return Results.BadRequest(new { error = "Intervalo invalido." });

            var marca = new Bookmark
            {
                TenantId = cam.TenantId,
                DeviceId = id,
                Title = input.Title,
                Description = input.Description,
                StartedAt = input.From,
                EndedAt = input.To,
                CreatedByUserId = ctx.User.UserId()
            };
            db.Bookmarks.Add(marca);

            // Protege ja na criacao: esperar a proxima passada da retencao
            // deixaria uma janela em que o arquivo ainda pode ser apagado.
            var cobertas = await db.Recordings
                .Where(r => r.DeviceId == id && r.StartedAt <= input.To
                         && (r.EndedAt ?? r.StartedAt) >= input.From)
                .ToListAsync();
            foreach (var r in cobertas) r.Protected = true;

            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "bookmark.create", "camera", id.ToString(),
                detail: $"{input.Title} ({cobertas.Count} gravacoes protegidas)");

            return Results.Created($"/api/vms/bookmarks/{marca.Id}",
                new { marca.Id, marca.Title, gravacoesProtegidas = cobertas.Count });
        });

        g.MapDelete("/bookmarks/{id:long}", async (
            long id, HttpContext ctx, PlatformDbContext db,
            PermissionService perms, AuditService audit) =>
        {
            var marca = await db.Bookmarks.FindAsync(id);
            if (marca is null) return Results.NotFound();

            if (!await perms.HasAsync(ctx.User.UserId(), Permissions.CameraPlayback,
                    ObjectTypes.Camera, marca.DeviceId))
                return Results.Forbid();

            db.Bookmarks.Remove(marca);
            await db.SaveChangesAsync();

            // A protecao e reavaliada pela retencao: outra marca pode cobrir o
            // mesmo trecho, entao desproteger aqui seria precipitado.
            await audit.WriteAsync(ctx, "bookmark.delete", "bookmark", id.ToString());
            return Results.NoContent();
        });
    }

    // ---------------------------------------------------------------- events

    private static void MapEvents(RouteGroupBuilder g)
    {
        // Ingestao de evento externo (integracao, analitico de terceiro).
        // Antes qualquer usuario autenticado podia injetar evento arbitrario,
        // inclusive escolhendo Id e TenantId — o corpo agora e um DTO restrito
        // e a origem e conferida contra os direitos de quem chamou.
        g.MapPost("/events", async (
            EventInput input, HttpContext ctx, PlatformDbContext db,
            PermissionService perms, IEventBus bus, AuditService audit) =>
        {
            var userId = ctx.User.UserId();

            if (input.DeviceId is int deviceId)
            {
                var cam = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId);
                if (cam is null) return Results.BadRequest(new { error = $"Dispositivo {deviceId} nao existe." });

                if (!await perms.HasAsync(userId, Permissions.EventAck, ObjectTypes.Camera, deviceId))
                    return Results.Forbid();
            }
            else if (!await perms.HasAsync(userId, Permissions.EventAck))
            {
                return Results.Forbid();
            }

            if (string.IsNullOrWhiteSpace(input.Type))
                return Results.BadRequest(new { error = "Tipo do evento e obrigatorio." });

            var evt = new DeviceEvent
            {
                TenantId = ctx.User.TenantId(),          // do token, nunca do corpo
                DeviceId = input.DeviceId,
                Type = input.Type.Trim(),
                Severity = Math.Clamp(input.Severity, 1, 3),
                Payload = input.Payload
            };

            db.Events.Add(evt);
            await db.SaveChangesAsync();
            await bus.PublishAsync(evt);

            await audit.WriteAsync(ctx, "event.ingest", "camera",
                input.DeviceId?.ToString() ?? "", detail: evt.Type);

            return Results.Accepted($"/api/vms/events/{evt.Id}", evt);
        });

        g.MapGet("/events", async (
            HttpContext ctx, PlatformDbContext db, PermissionService perms,
            int? deviceId, string? type, DateTime? from, bool? unacknowledged, int take = 100) =>
        {
            var visible = await perms.VisibleCameraIdsAsync(ctx.User.UserId());
            var tenant = ctx.User.TenantId();

            var q = db.Events.Where(e => e.TenantId == tenant)
                .Where(e => e.DeviceId == null || visible.Contains(e.DeviceId.Value));

            if (deviceId is not null) q = q.Where(e => e.DeviceId == deviceId);
            if (!string.IsNullOrWhiteSpace(type)) q = q.Where(e => e.Type == type);
            if (from is not null) q = q.Where(e => e.CreatedAt >= from);
            if (unacknowledged == true) q = q.Where(e => !e.Acknowledged);

            return await q.OrderByDescending(e => e.CreatedAt)
                .Take(Math.Clamp(take, 1, 500)).AsNoTracking().ToListAsync();
        });

        // Tratar o evento: o painel precisa distinguir o que ja foi visto.
        g.MapPost("/events/{id:long}/ack", async (
            long id, HttpContext ctx, PlatformDbContext db,
            PermissionService perms, AuditService audit) =>
        {
            var evt = await db.Events.FindAsync(id);
            if (evt is null) return Results.NotFound();

            var ok = evt.DeviceId is int deviceId
                ? await perms.HasAsync(ctx.User.UserId(), Permissions.EventAck, ObjectTypes.Camera, deviceId)
                : await perms.HasAsync(ctx.User.UserId(), Permissions.EventAck);
            if (!ok) return Results.Forbid();

            evt.Acknowledged = true;
            evt.AcknowledgedAt = DateTime.UtcNow;
            evt.AcknowledgedByUserId = ctx.User.UserId();
            if (string.IsNullOrWhiteSpace(evt.TreatmentStatus) || evt.TreatmentStatus == "open")
                evt.TreatmentStatus = "resolved";
            await db.SaveChangesAsync();

            await audit.WriteAsync(ctx, "event.ack", "event", id.ToString());
            return Results.NoContent();
        });

        // ---- Botões de ação sobre eventos (estilo Digifort) ----
        // Lista botões habilitados do tenant para o posto de monitoramento.
        g.MapGet("/event-action-buttons", async (HttpContext ctx, PlatformDbContext db) =>
        {
            var tenant = ctx.User.TenantId();
            var list = await db.EventActionButtons.AsNoTracking()
                .Where(b => b.TenantId == tenant && b.Enabled)
                .OrderBy(b => b.SortOrder).ThenBy(b => b.Name)
                .Select(b => new
                {
                    b.Id, b.Name, b.Description, b.Icon, b.Color,
                    b.EventTypes, b.MinSeverity, b.Actions,
                    b.AutoAcknowledge, b.SetStatus,
                    b.RequireConfirm, b.RequireComment, b.SortOrder
                })
                .ToListAsync();
            return Results.Ok(list);
        });

        // Executa um botão de ação sobre um evento concreto.
        g.MapPost("/events/{eventId:long}/actions/{buttonId:int}", async (
            long eventId, int buttonId, EventButtonRunInput? input,
            HttpContext ctx, PlatformDbContext db, PermissionService perms,
            DriverRegistry registry, EventActionRunner runner, AuditService audit) =>
        {
            var evt = await db.Events.FindAsync(eventId);
            if (evt is null) return Results.NotFound(new { error = "Evento não encontrado." });

            var ok = evt.DeviceId is int deviceId
                ? await perms.HasAsync(ctx.User.UserId(), Permissions.EventAck, ObjectTypes.Camera, deviceId)
                : await perms.HasAsync(ctx.User.UserId(), Permissions.EventAck);
            if (!ok) return Results.Forbid();

            var btn = await db.EventActionButtons.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == buttonId && b.TenantId == evt.TenantId && b.Enabled);
            if (btn is null) return Results.NotFound(new { error = "Botão não encontrado ou inativo." });

            if (!EventButtonMatches(btn, evt))
                return Results.BadRequest(new { error = "Este botão não se aplica a este tipo/severidade de evento." });

            if (btn.RequireComment && string.IsNullOrWhiteSpace(input?.Comment))
                return Results.BadRequest(new { error = "Este botão exige um comentário." });

            var acoes = EventActionRunner.ParseActions(btn.Actions);
            await runner.RunAllAsync(acoes, evt, db, registry, ctx.RequestAborted);

            // Recarrega para aplicar status (runner pode ter alterado bookmarks etc.)
            evt = await db.Events.FindAsync(eventId);
            if (evt is null) return Results.NotFound();

            if (btn.AutoAcknowledge)
            {
                evt.Acknowledged = true;
                evt.AcknowledgedAt = DateTime.UtcNow;
                evt.AcknowledgedByUserId = ctx.User.UserId();
            }

            if (!string.IsNullOrWhiteSpace(btn.SetStatus))
            {
                var st = btn.SetStatus.Trim().ToLowerInvariant();
                if (st is "open" or "treating" or "resolved")
                    evt.TreatmentStatus = st;
            }

            if (!string.IsNullOrWhiteSpace(input?.Comment))
                evt.TreatmentNote = input.Comment.Trim();

            await db.SaveChangesAsync();

            await audit.WriteAsync(ctx, "event.action_button", "event", eventId.ToString(),
                detail: $"button={btn.Id}:{btn.Name}; status={evt.TreatmentStatus}");

            // Client-side kinds para o monitor reagir (live/playback/map).
            var clientKinds = acoes
                .Select(a => a.Kind)
                .Where(k => k is "OpenLive" or "OpenPlayback" or "OpenMap"
                    or "PopupVideo" or "PlaySound")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Results.Ok(new
            {
                ok = true,
                eventId,
                buttonId = btn.Id,
                buttonName = btn.Name,
                acknowledged = evt.Acknowledged,
                treatmentStatus = evt.TreatmentStatus,
                treatmentNote = evt.TreatmentNote,
                deviceId = evt.DeviceId,
                clientActions = clientKinds
            });
        });
    }

    private static bool EventButtonMatches(EventActionButton btn, DeviceEvent evt)
    {
        if (btn.MinSeverity > 0 && evt.Severity < btn.MinSeverity)
            return false;
        var types = (btn.EventTypes ?? "*").Trim();
        if (string.IsNullOrEmpty(types) || types == "*")
            return true;
        var set = types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return set.Any(t => string.Equals(t, evt.Type, StringComparison.OrdinalIgnoreCase));
    }

    // --------------------------------------------------------------- helpers

    /// <summary>
    /// Confina um caminho a uma raiz. Compara pastas completas para que
    /// "/data/rec-antigo" nao passe por estar dentro de "/data/rec".
    /// </summary>
    internal static bool IsInside(string path, string root)
        => StoragePaths.IsInside(path, root);

    /// <summary>
    /// Host público do nó de mídia para o browser. Se a config for localhost e
    /// o cliente acessar por outro host/IP, reescreve para o Host da requisição.
    /// </summary>
    internal static string ResolveMediaPublicHost(VmsOptions opt, HttpContext ctx)
    {
        var configured = (opt.MediaPublicHost ?? "http://localhost").TrimEnd('/');
        var requestHost = ctx.Request.Host.Host;

        if (string.IsNullOrWhiteSpace(requestHost)) return configured;

        var isLoopbackConfig = configured.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || configured.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);
        var requestIsLocal = string.Equals(requestHost, "localhost", StringComparison.OrdinalIgnoreCase)
            || requestHost == "127.0.0.1" || requestHost == "::1";

        if (isLoopbackConfig && !requestIsLocal)
        {
            var scheme = configured.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
            return $"{scheme}://{requestHost}";
        }

        return configured;
    }

    /// <summary>Anexa query sem quebrar path (ex.: /cam1/whep?jwt=…).</summary>
    internal static string AppendQuery(string url, string key, string value)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var sep = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{sep}{key}={value}";
    }

    private static int RemoveDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        try
        {
            var total = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
            Directory.Delete(dir, recursive: true);
            return total;
        }
        catch (IOException)
        {
            return 0;   // gravador ainda escrevendo; a retencao limpa depois
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Exige uma permissao global (nao ligada a uma camera especifica).</summary>
    private static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission)
        => builder.AddEndpointFilter(async (ctx, next) =>
        {
            var perms = ctx.HttpContext.RequestServices.GetRequiredService<PermissionService>();
            return await perms.HasAsync(ctx.HttpContext.User.UserId(), permission, ObjectTypes.Camera)
                ? await next(ctx)
                : Results.Forbid();
        });
}

public record CameraInput(
    string Name,
    string Host,
    int TenantId = 1,
    string Driver = "onvif",
    int Port = 80,
    string Username = "",
    string Password = "",
    string StreamUrl = "",
    RecordingMode Recording = RecordingMode.Continuous,
    int RetentionDays = 7,
    int MaxStorageGb = 0,
    int EventRecordSeconds = 60,
    int PreEventSeconds = 15,
    int? RecordingProfileId = null,
    int? LiveProfileId = null,
    bool RecordAudio = true,
    bool EdgePullEnabled = false);

/// <summary>Evento vindo de integracao externa. Id e TenantId sao do servidor.</summary>
public record EventInput(
    string Type,
    int? DeviceId = null,
    int Severity = 1,
    string Payload = "{}");

/// <summary>Corpo ao executar botão de ação sobre evento.</summary>
public record EventButtonRunInput(string? Comment = null);

public record PtzMove(double Pan = 0, double Tilt = 0, double Zoom = 0, int TimeoutSeconds = 2);

/// <summary>Push de regra VCA/analitico para a camera (ROI normalizado 0–1).</summary>
public record VcaConfigInput(
    string? Rule = "motion",
    bool? Enabled = true,
    string? Channel = "1",
    string? Name = null,
    EventRoi? Roi = null);
public record PresetInput(string? Name = null);
public record ExportRequest(DateTime From, DateTime To);

public record ExportVerifyRequest(long? ExportId, string? FilePath);

public record PrivacyMaskInput(string? Name, string? PolygonsJson, bool Enabled = true);
public record BookmarkInput(string Title, DateTime From, DateTime To, string Description = "");
public record LayoutInput(string Name, string? LayoutId = "2x2", string? CellsJson = "[]");
public record TourInput(string[]? Presets = null, int DwellSeconds = 8);
public record TalkInput(string Base64);
