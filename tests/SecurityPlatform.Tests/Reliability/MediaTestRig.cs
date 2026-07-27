using System.Diagnostics;
using System.Net.Http.Json;

namespace SecurityPlatform.Tests.Reliability;

/// <summary>
/// Infra compartilhada dos harnesses de confiabilidade (soak / chaos).
///
/// <para>
/// Sobe uma instância <b>isolada</b> do MediaMTX (config próprio, sem auth HTTP,
/// portas dedicadas) para não depender da API nem colidir com um MediaMTX de dev,
/// e publica câmeras sintéticas via FFmpeg (<c>testsrc</c> → RTSP). O gravador de
/// teste lê do próprio MediaMTX (mesma topologia "1 pull" da produção).
/// </para>
///
/// Estes harnesses são <b>pesados/externos</b>: só rodam com
/// <c>SP_RELIABILITY=1</c> e FFmpeg + mediamtx.exe presentes. Caso contrário
/// fazem skip silencioso (como o E2E FFmpeg existente), mantendo o CI verde.
/// </summary>
public sealed class MediaTestRig : IDisposable
{
    // Portas dedicadas (deslocadas das padrão 9997/8554/8888/8889) p/ evitar conflito.
    public int ApiPort { get; }
    public int RtspPort { get; }
    public int HlsPort { get; }
    public int WebRtcPort { get; }

    public string ApiBase => $"http://127.0.0.1:{ApiPort}";
    public string RtspBase => $"rtsp://127.0.0.1:{RtspPort}";

    private readonly string _workDir;
    private readonly string _configPath;
    private readonly string _mediaMtxExe;
    private readonly List<Process> _publishers = new();
    private readonly List<Process> _recorders = new();
    private Process? _mediaMtx;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public MediaTestRig(int portBase = 19990)
    {
        ApiPort = portBase + 7;    // 19997
        RtspPort = portBase - 1436; // 18554 (portBase 19990 → 18554)
        HlsPort = portBase - 1102;  // 18888
        WebRtcPort = portBase - 1101; // 18889
        _mediaMtxExe = LocateMediaMtx() ?? "";
        _workDir = Path.Combine(Path.GetTempPath(), "sp_rig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _configPath = Path.Combine(_workDir, "mediamtx.reliability.yml");
    }

    // ---- Gates -------------------------------------------------------------

    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("SP_RELIABILITY"), "1", StringComparison.Ordinal);

    public bool ToolsAvailable => HasFfmpeg() && !string.IsNullOrEmpty(_mediaMtxExe);

    /// <summary>Skip silencioso salvo se as ferramentas existirem e SP_RELIABILITY=1.</summary>
    public static bool ShouldRun(MediaTestRig rig) => Enabled && rig.ToolsAvailable;

    // ---- MediaMTX ----------------------------------------------------------

    public async Task StartMediaMtxAsync(CancellationToken ct = default)
    {
        File.WriteAllText(_configPath, BuildConfig());
        _mediaMtx = Process.Start(new ProcessStartInfo
        {
            FileName = _mediaMtxExe,
            Arguments = $"\"{_configPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _workDir
        }) ?? throw new InvalidOperationException("Falha ao iniciar mediamtx.exe");

        if (!await WaitApiReadyAsync(TimeSpan.FromSeconds(15), ct))
            throw new InvalidOperationException("MediaMTX não respondeu na API a tempo");
    }

    /// <summary>Mata o processo do MediaMTX (chaos). Não limpa publishers/recorders.</summary>
    public void KillMediaMtx()
    {
        try { if (_mediaMtx is { HasExited: false }) _mediaMtx.Kill(entireProcessTree: true); } catch { /* */ }
        try { _mediaMtx?.WaitForExit(4000); } catch { /* */ }
        _mediaMtx = null;
    }

    public async Task<bool> ApiUpAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var res = await _http.GetAsync($"{ApiBase}/v3/config/paths/list", cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> PathReadyAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetFromJsonAsync<PathState>($"{ApiBase}/v3/paths/get/{path}", ct);
            return res?.Ready ?? false;
        }
        catch { return false; }
    }

    private async Task<bool> WaitApiReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            if (await ApiUpAsync(ct)) return true;
            await Task.Delay(300, ct);
        }
        return false;
    }

    /// <summary>Aguarda um path ficar ready (publisher conectado + fluindo).</summary>
    public async Task<bool> WaitPathReadyAsync(string path, TimeSpan timeout, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            if (await PathReadyAsync(path, ct)) return true;
            await Task.Delay(300, ct);
        }
        return false;
    }

    // ---- Câmeras sintéticas -----------------------------------------------

    public static string CamPath(int i) => $"cam{i}";

    /// <summary>Publica uma câmera sintética (testsrc + timestamp) no path indicado.</summary>
    public void StartPublisher(int camId, int seconds = 3600)
    {
        var path = CamPath(camId);
        // testsrc animado, 10 fps, H.264 ultrafast, saída RTSP/TCP para o MediaMTX.
        // testsrc já é animado (barras + tempo embutido); sem drawtext p/ não
        // depender de fontfile no Windows (drawtext sem fonte derruba o ffmpeg).
        var args =
            $"-nostdin -hide_banner -loglevel error -re " +
            $"-f lavfi -i testsrc=size=320x240:rate=10 -t {seconds} " +
            $"-pix_fmt yuv420p -c:v libx264 -preset ultrafast -tune zerolatency -g 20 " +
            $"-f rtsp -rtsp_transport tcp {RtspBase}/{path}";
        _publishers.Add(StartFfmpeg(args));
    }

    /// <summary>Grava um path do MediaMTX em segmentos MP4 (simula o RecorderService).</summary>
    public string StartRecorder(int camId, int segmentSeconds = 10)
    {
        var path = CamPath(camId);
        var outDir = Path.Combine(_workDir, "rec", path);
        Directory.CreateDirectory(outDir);
        var pattern = Path.Combine(outDir, "c_%Y%m%d_%H%M%S.mp4");
        var args =
            $"-nostdin -hide_banner -loglevel error -rtsp_transport tcp -i {RtspBase}/{path} " +
            $"-c copy -f segment -segment_time {segmentSeconds} -segment_format mp4 " +
            $"-reset_timestamps 1 -strftime 1 \"{pattern}\"";
        _recorders.Add(StartFfmpeg(args));
        return outDir;
    }

    public static int CountSegments(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "c_*.mp4").Length : 0;

    // ---- FFmpeg helpers ----------------------------------------------------

    private static Process StartFfmpeg(string args) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("Falha ao iniciar ffmpeg");

    public static bool HasFfmpeg()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Procura o mediamtx.exe subindo a árvore a partir do binário de teste.</summary>
    public static string? LocateMediaMtx()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "mediamtx.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private string BuildConfig() =>
        "logLevel: error\n" +
        "api: yes\n" +
        $"apiAddress: 127.0.0.1:{ApiPort}\n" +
        "rtsp: yes\n" +
        $"rtspAddress: :{RtspPort}\n" +
        "rtspTransports: [tcp]\n" +
        "hls: yes\n" +
        $"hlsAddress: :{HlsPort}\n" +
        "webrtc: yes\n" +
        $"webrtcAddress: :{WebRtcPort}\n" +
        "rtmp: no\n" +
        "srt: no\n" +
        // all_others = catch-all: permite publicar/ler em qualquer path (câmera sintética).
        "paths:\n" +
        "  all_others:\n";

    private record PathState(string Name, bool Ready);

    public void Dispose()
    {
        foreach (var p in _recorders.Concat(_publishers))
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* */ }
        KillMediaMtx();
        _http.Dispose();
        try { Directory.Delete(_workDir, true); } catch { /* */ }
    }
}
