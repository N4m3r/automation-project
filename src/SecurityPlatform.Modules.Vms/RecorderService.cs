using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;
using SecurityPlatform.Core.Events;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Gravador: um processo FFmpeg por camera, segmentado em arquivos.
///
/// Dois modos:
/// <list type="bullet">
/// <item><c>Continuous</c> — grava sempre, respeitando o agendamento
/// (<see cref="ScheduleSlot"/>). Sem faixas = 24×7.</item>
/// <item><c>OnEvent</c> — sobe o FFmpeg quando chega um evento da camera e o
/// mantem enquanto houver evento; encerra apos <c>EventRecordSeconds</c> de
/// silencio. Como o RTSP ja esta publicado no gateway de midia, a latencia ate
/// comecar a gravar e de decimos de segundo.</item>
/// </list>
///
/// Reconcilia a cada 15s — camera adicionada/removida no banco entra/sai da
/// gravacao sozinha, sem restart. Escala por sharding (ver VmsOptions).
/// </summary>
public class RecorderService(
    IServiceScopeFactory scopes,
    IEventBus bus,
    IOptions<VmsOptions> options,
    VmsMetrics metrics,
    ILogger<RecorderService> log) : BackgroundService
{
    private readonly VmsOptions _opt = options.Value;
    private readonly ConcurrentDictionary<int, Process> _running = new();

    /// <summary>Evita corrida: vários motion simultâneos tentando StartOnEvent.</summary>
    private readonly ConcurrentDictionary<int, byte> _startingOnEvent = new();

    /// <summary>Ultimo evento por camera — governa o modo OnEvent.</summary>
    private readonly ConcurrentDictionary<int, DateTime> _lastEvent = new();

    /// <summary>Última amostra de FPS/bitrate parseada do stderr do FFmpeg.</summary>
    private static readonly ConcurrentDictionary<int, StreamStats> Stats = new();

    public static StreamStats? GetStats(int deviceId) =>
        Stats.TryGetValue(deviceId, out var s) ? s : null;

    /// <summary>Quantas câmeras com FFmpeg ativo neste nó.</summary>
    public int ActiveCount => _running.Count;

    /// <summary>Prefixo no nome do arquivo: diz o que originou o segmento.</summary>
    internal const string ContinuousPrefix = "c_";
    internal const string EventPrefix = "e_";
    /// <summary>Ring buffer de pré-alarme (OnEvent com PreEventSeconds &gt; 0).</summary>
    internal const string PreEventPrefix = "p_";
    internal const string PreEventTrigger = "prebuffer";

    // Cache de agenda e perfis: evita N queries a cada 15s.
    private Dictionary<int, List<ScheduleSlot>> _agenda = new();
    private Dictionary<int, MediaProfile> _perfis = new();
    private TimeZoneInfo? _tz;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.RecorderEnabled)
        {
            log.LogInformation("Gravador desabilitado neste no.");
            return;
        }

        Directory.CreateDirectory(_opt.StoragePath);
        log.LogInformation("Gravador ativo — shard {Index}/{Count}", _opt.ShardIndex, _opt.ShardCount);

        // A escuta do barramento roda em paralelo: a reconciliacao a cada 15s
        // seria lenta demais para comecar a gravar um alarme.
        var escuta = ObserveEventsAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(ct);
            }
            catch (Exception e)
            {
                log.LogError(e, "Falha ao reconciliar gravacoes");
            }
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }

        foreach (var p in _running.Values) Kill(p);
        await escuta;
    }

    /// <summary>Marca a hora do ultimo evento de cada camera.</summary>
    private async Task ObserveEventsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in bus.SubscribeAsync(ct))
            {
                if (evt.DeviceId is not int id) continue;
                _lastEvent[id] = DateTime.UtcNow;

                // Nao espera os 15s da reconciliacao: se a camera e OnEvent e
                // ainda nao esta gravando, sobe agora.
                // Motion flood: só sobe se ainda não há processo (evita PATCH/MediaMTX em loop).
                if (!_opt.OwnsDevice(id)) continue;

                // Promove pré-buffer recente a prova de evento (não purga).
                _ = PromotePreEventAsync(id, ct);

                if (!_running.ContainsKey(id) && !_startingOnEvent.ContainsKey(id))
                    _ = StartOnEventAsync(id, ct);
            }
        }
        catch (OperationCanceledException) { /* desligamento normal */ }
        catch (Exception e)
        {
            log.LogError(e, "Escuta de eventos do gravador terminou com erro");
        }
    }

    private async Task StartOnEventAsync(int deviceId, CancellationToken ct)
    {
        if (!_startingOnEvent.TryAdd(deviceId, 0)) return;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var registry = scope.ServiceProvider.GetRequiredService<DriverRegistry>();

            var cam = await db.Devices.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == deviceId && d.Recording == RecordingMode.OnEvent, ct);
            if (cam is null || _running.ContainsKey(deviceId)) return;

            // Agenda de eventos: se houver faixas Event e o instante estiver
            // fora delas, ignora o disparo (ex.: não gravar motion à noite).
            var slots = await db.ScheduleSlots.AsNoTracking()
                .Where(s => s.DeviceId == deviceId && s.Kind == ScheduleKind.Event && s.Enabled)
                .ToListAsync(ct);
            if (!RecordingSchedule.IsActive(slots, ScheduleKind.Event, DateTime.UtcNow, _tz))
            {
                log.LogDebug("Evento da camera {Id} fora da agenda de gravacao por evento", deviceId);
                return;
            }

            var perfis = await db.MediaProfiles.AsNoTracking()
                .ToDictionaryAsync(p => p.Id, ct);
            var media = scope.ServiceProvider.GetRequiredService<MediaGateway>();
            var rtsp = await ResolveRecordRtspAsync(cam, registry, media, perfis, ct);
            if (rtsp is null) return;
            if (Start(cam, rtsp, modo: RecordMode.Event) is { } p && !_running.TryAdd(cam.Id, p)) Kill(p);
        }
        catch (Exception e)
        {
            log.LogError(e, "Falha ao iniciar gravacao por evento da camera {Id}", deviceId);
        }
        finally
        {
            _startingOnEvent.TryRemove(deviceId, out _);
        }
    }

    /// <summary>
    /// Marca segmentos de pré-buffer dos últimos PreEventSeconds como evento protegido.
    /// </summary>
    private async Task PromotePreEventAsync(int deviceId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var cam = await db.Devices.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == deviceId, ct);
            if (cam is null) return;

            var pre = _opt.EffectivePreEventSeconds(cam);
            if (pre <= 0) return;

            var from = DateTime.UtcNow.AddSeconds(-pre - 5);
            var segs = await db.Recordings
                .Where(r => r.DeviceId == deviceId
                            && r.StartedAt >= from
                            && (r.Trigger == PreEventTrigger || r.Trigger == "prebuffer"))
                .ToListAsync(ct);

            if (segs.Count == 0) return;

            foreach (var s in segs)
            {
                s.Trigger = "event";
                s.Protected = true;
            }
            await db.SaveChangesAsync(ct);
            metrics.IncPreEventPromote();
            log.LogInformation(
                "Pré-buffer promovido a evento: câmera {Id}, {N} segmentos (~{Sec}s)",
                deviceId, segs.Count, pre);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Falha ao promover pré-buffer da câmera {Id}", deviceId);
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<DriverRegistry>();
        var media = scope.ServiceProvider.GetRequiredService<MediaGateway>();
        var lease = scope.ServiceProvider.GetService<RecorderLeaseService>();

        var cameras = await db.Devices
            .Where(d => d.Kind == DeviceKind.Camera && d.Recording != RecordingMode.Off)
            .AsNoTracking()
            .ToListAsync(ct);

        // Sharding + HA lease: só grava fatia própria e lease ativo.
        var minhas = new List<Device>();
        foreach (var c in cameras.Where(c => _opt.OwnsDevice(c.Id)))
        {
            if (lease is not null && _opt.HaEnabled)
            {
                if (!await lease.TryAcquireAsync(c.Id, ct))
                    continue;
            }
            minhas.Add(c);
        }
        var ids = minhas.Select(c => c.Id).ToList();

        _agenda = (await db.ScheduleSlots.AsNoTracking()
                .Where(s => ids.Contains(s.DeviceId) && s.Enabled)
                .ToListAsync(ct))
            .GroupBy(s => s.DeviceId)
            .ToDictionary(g => g.Key, g => g.ToList());

        _perfis = await db.MediaProfiles.AsNoTracking().ToDictionaryAsync(p => p.Id, ct);

        var settings = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        _tz = ResolveTimeZone(settings?.TimeZone);

        var agora = DateTime.UtcNow;
        var devemGravar = minhas.Where(c => DeveGravar(c, agora)).ToList();
        var idsAtivos = devemGravar.Select(c => c.Id).ToHashSet();

        // Encerra o que saiu do escopo deste no, foi desligado, saiu da agenda
        // ou — no modo OnEvent — passou do tempo de silencio.
        foreach (var id in _running.Keys.Where(id => !idsAtivos.Contains(id)).ToList())
            if (_running.TryRemove(id, out var proc))
            {
                Kill(proc);
                log.LogInformation("Gravacao da camera {Id} encerrada", id);
            }

        // Sobe o que falta (inclui processos que morreram sozinhos).
        foreach (var cam in devemGravar)
        {
            if (_running.TryGetValue(cam.Id, out var existing))
            {
                if (!existing.HasExited) continue;
                _running.TryRemove(cam.Id, out _);
                log.LogWarning("FFmpeg da camera {Id} caiu — reiniciando", cam.Id);
            }

            var rtsp = await ResolveRecordRtspAsync(cam, registry, media, _perfis, ct);
            if (rtsp is null) continue; // aguarda MediaMTX — não abre RTSP direto
            var modo = ResolveMode(cam, agora);
            if (Start(cam, rtsp, modo) is { } p) _running[cam.Id] = p;
        }

        metrics.SetRecordingActive(_running.Count(kv =>
        {
            try { return !kv.Value.HasExited; }
            catch { return false; }
        }));
    }

    private enum RecordMode { Continuous, Event, PreEvent }

    private RecordMode ResolveMode(Device cam, DateTime agora)
    {
        if (cam.Recording == RecordingMode.Continuous) return RecordMode.Continuous;
        if (cam.Recording != RecordingMode.OnEvent) return RecordMode.Continuous;

        if (_lastEvent.TryGetValue(cam.Id, out var ultimo))
        {
            var janela = TimeSpan.FromSeconds(Math.Max(cam.EventRecordSeconds, 10));
            if (agora - ultimo < janela) return RecordMode.Event;
        }

        return _opt.EffectivePreEventSeconds(cam) > 0 ? RecordMode.PreEvent : RecordMode.Event;
    }

    /// <summary>
    /// Continuo: agenda de gravação + modo Continuous.
    /// Por evento: alarme quente OU ring buffer de pré-alarme (PreEventSeconds &gt; 0).
    /// </summary>
    private bool DeveGravar(Device cam, DateTime agora)
    {
        var slots = _agenda.GetValueOrDefault(cam.Id) ?? [];

        if (cam.Recording == RecordingMode.Continuous)
            return RecordingSchedule.IsActive(slots, ScheduleKind.Recording, agora, _tz);

        if (cam.Recording != RecordingMode.OnEvent) return false;

        if (!RecordingSchedule.IsActive(slots, ScheduleKind.Event, agora, _tz))
            return false;

        // Ring buffer de pré-alarme: grava sempre (segmentos curtos p_*),
        // retenção descarta o que passar de PreEventSeconds.
        if (_opt.EffectivePreEventSeconds(cam) > 0)
            return true;

        if (!_lastEvent.TryGetValue(cam.Id, out var ultimo)) return false;

        var janela = TimeSpan.FromSeconds(Math.Max(cam.EventRecordSeconds, 10));
        return agora - ultimo < janela;
    }

    /// <summary>
    /// Prefere RTSP local do MediaMTX (1 pull nativo na câmera).
    /// Direto na câmera só se <see cref="VmsOptions.AllowDirectCameraRecord"/>.
    /// </summary>
    private async Task<string?> ResolveRecordRtspAsync(
        Device cam, DriverRegistry registry, MediaGateway media,
        IReadOnlyDictionary<int, MediaProfile> perfis, CancellationToken ct)
    {
        var cameraRtsp = await ResolveCameraRtspAsync(cam, registry, perfis, ct);

        // Garante path principal no gateway (único pull permanente na câmera).
        await media.RegisterAsync(cam.Id, cameraRtsp, substream: false, ct);

        if (!_opt.RecordFromMediaGateway)
        {
            if (_opt.SingleCameraRtspPull)
                log.LogWarning(
                    "RecordFromMediaGateway=false com SingleCameraRtspPull — risco de multi-sessão na camera {Id}",
                    cam.Id);
            return cameraRtsp;
        }

        var waitSec = Math.Clamp(_opt.MediaGatewayReadyTimeoutSeconds, 5, 120);
        var attempts = Math.Max(1, waitSec * 2); // 500ms cada
        for (var i = 0; i < attempts; i++)
        {
            if (await media.IsReadyAsync(cam.Id, substream: false, ct))
            {
                var local = media.LocalRtspUrl(cam.Id, substream: false);
                log.LogDebug("Gravacao camera {Id} via gateway {Url}", cam.Id, local);
                return local;
            }
            await Task.Delay(500, ct);
        }

        if (_opt.AllowDirectCameraRecord)
        {
            log.LogWarning(
                "MediaMTX path cam{Id} nao ready — gravando DIRETO na camera (AllowDirectCameraRecord=true)",
                cam.Id);
            return cameraRtsp;
        }

        // Não abre 2ª/3ª sessão nativa: deixa o próximo reconcile tentar de novo.
        log.LogWarning(
            "MediaMTX path cam{Id} nao ready — gravacao adiada (evita multi-acesso nativo)",
            cam.Id);
        return null;
    }

    private static async Task<string> ResolveCameraRtspAsync(
        Device cam, DriverRegistry registry,
        IReadOnlyDictionary<int, MediaProfile> perfis, CancellationToken ct)
    {
        var rtsp = await registry.Resolve(cam).GetStreamUrlAsync(cam, ct);
        var canal = StreamUrlBuilder.ResolveChannel(cam, perfis, StreamUrlBuilder.Quality.Main);
        return StreamUrlBuilder.ApplyQuality(rtsp, StreamUrlBuilder.Quality.Main, canal);
    }

    private Process? Start(Device cam, string rtspUrl, RecordMode modo)
    {
        // Multi-volume: escolhe disco com mais espaço livre.
        var volume = StoragePaths.PickVolume(_opt.StoragePath, _opt.StorageVolumes);
        var dir = Path.Combine(volume, cam.Id.ToString());
        Directory.CreateDirectory(dir);

        // Pré-buffer e evento: segmentos curtos (prova rápida + ring fino).
        int segundos;
        string prefixo;
        switch (modo)
        {
            case RecordMode.Event:
                segundos = Math.Clamp(cam.EventRecordSeconds, 10, _opt.SegmentSeconds);
                prefixo = EventPrefix;
                break;
            case RecordMode.PreEvent:
            {
                var pre = Math.Max(5, _opt.EffectivePreEventSeconds(cam));
                segundos = Math.Clamp(Math.Min(pre, 15), 5, 30);
                prefixo = PreEventPrefix;
                break;
            }
            default:
                segundos = _opt.SegmentSeconds;
                prefixo = ContinuousPrefix;
                break;
        }

        var porEvento = modo is RecordMode.Event or RecordMode.PreEvent;

        // Browser-compatible (padrão): H.264 + AAC em MP4 progressivo.
        // HEVC/fMP4 da câmera não toca no <video> HTML5 da maioria dos browsers.
        // Modo copy+fMP4: CPU mínima, mas exige normalização pós-fechamento.
        string videoAudio;
        string segmentOpts;
        if (_opt.RecordBrowserCompatible)
        {
            var audio = cam.RecordAudio && _opt.RecordAudio
                ? "-c:a aac -b:a 64k -ar 16000 -ac 1"
                : "-an";
            // ultrafast/zerolatency: gravador multi-câmera sem saturar CPU.
            // g=30 + bf=0: keyframes regulares e sem B-frames (seek estável).
            videoAudio = $"-c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -g 30 -bf 0 {audio}";
            // Sem empty_moov: ao fechar o segmento o moov fica completo e o browser
            // reproduz de imediato (o arquivo em aberto ainda pode estar incompleto).
            segmentOpts = "-segment_format mp4 -reset_timestamps 1 -strftime 1";
        }
        else
        {
            var audio = cam.RecordAudio && _opt.RecordAudio ? "-c:a copy" : "-an";
            videoAudio = $"-c:v copy {audio}";
            // fMP4: escrita contínua (queda de energia perde segundos, não o bloco).
            segmentOpts = string.Join(' ',
                "-segment_format mp4",
                "-segment_format_options movflags=+frag_keyframe+empty_moov+default_base_moof",
                "-reset_timestamps 1 -strftime 1");
        }

        var args = string.Join(' ',
            "-nostdin -hide_banner -loglevel error -stats",
            "-rtsp_transport tcp",
            "-timeout 5000000",
            $"-i \"{rtspUrl}\"",
            videoAudio,
            "-f segment",
            $"-segment_time {segundos}",
            segmentOpts,
            $"\"{Path.Combine(dir, $"{prefixo}%Y%m%d_%H%M%S.mp4")}\"");

        try
        {
            var p = Process.Start(new ProcessStartInfo(_opt.FfmpegPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            });
            if (p is null) return null;

            // Obrigatorio drenar o stderr: sem leitura o buffer do pipe enche
            // e o FFmpeg trava com a gravacao pela metade.
            // O FFmpeg repete a URL RTSP nas mensagens de erro, entao a saida
            // passa por mascaramento — a senha da camera nao pode ir para o log.
            p.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                if (TryParseStats(e.Data, out var st))
                    Stats[cam.Id] = st;
                // Linhas de progresso (frame=) são normais; só erros vão ao log.
                else if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase)
                      || e.Data.Contains("failed", StringComparison.OrdinalIgnoreCase))
                    log.LogWarning("FFmpeg camera {Id}: {Message}", cam.Id, UrlMasking.Mask(e.Data));
            };
            p.BeginErrorReadLine();

            log.LogInformation("Gravando camera {Id} ({Name}) — modo {Modo}",
                cam.Id, cam.Name, modo.ToString().ToLowerInvariant());
            return p;
        }
        catch (Exception e)
        {
            log.LogError(e, "Nao foi possivel iniciar o FFmpeg para a camera {Id}", cam.Id);
            return null;
        }
    }

    /// <summary>
    /// Encerra o FFmpeg da câmera (exclusão/desligamento). Idempotente.
    /// </summary>
    public bool StopDevice(int deviceId)
    {
        if (!_running.TryRemove(deviceId, out var proc)) return false;
        Kill(proc);
        Stats.TryRemove(deviceId, out _);
        log.LogInformation("Gravação da câmera {Id} interrompida sob demanda", deviceId);
        return true;
    }

    /// <summary>
    /// Parse de linhas de progresso do FFmpeg, ex.:
    /// frame= 123 fps= 25 q=-1.0 size= 1024kB time=... bitrate= 800.0kbits/s
    /// </summary>
    internal static bool TryParseStats(string line, out StreamStats stats)
    {
        stats = default;
        if (!line.Contains("fps=", StringComparison.Ordinal) &&
            !line.Contains("bitrate=", StringComparison.Ordinal))
            return false;

        double? fps = null;
        double? bitrate = null;

        var fpsIdx = line.IndexOf("fps=", StringComparison.Ordinal);
        if (fpsIdx >= 0)
        {
            var slice = line[(fpsIdx + 4)..].TrimStart();
            var end = 0;
            while (end < slice.Length && (char.IsDigit(slice[end]) || slice[end] is '.' or ' '))
                end++;
            if (double.TryParse(slice[..end].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var f))
                fps = f;
        }

        var brIdx = line.IndexOf("bitrate=", StringComparison.Ordinal);
        if (brIdx >= 0)
        {
            var slice = line[(brIdx + 8)..].TrimStart();
            var num = "";
            foreach (var ch in slice)
            {
                if (char.IsDigit(ch) || ch == '.') num += ch;
                else break;
            }
            if (double.TryParse(num, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var b))
                bitrate = b; // kbits/s
        }

        if (fps is null && bitrate is null) return false;
        stats = new StreamStats(fps, bitrate, DateTime.UtcNow);
        return true;
    }

    private void Kill(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            p.Dispose();
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Falha ao encerrar processo FFmpeg");
        }
    }

    private static TimeZoneInfo? ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time"); }
            catch { return null; }
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }
}

public readonly record struct StreamStats(double? Fps, double? BitrateKbps, DateTime At);
