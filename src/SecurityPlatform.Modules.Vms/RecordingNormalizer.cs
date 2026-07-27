using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Garante que segmentos de gravação sejam reproduzíveis no browser
/// (<c>&lt;video&gt;</c> HTML5): H.264 + AAC em MP4 progressivo com moov no início.
/// </summary>
public sealed class RecordingNormalizer(
    IOptions<VmsOptions> options,
    ILogger<RecordingNormalizer> log)
{
    private readonly VmsOptions _opt = options.Value;

    /// <summary>Tamanho mínimo para considerar o arquivo um segmento válido.</summary>
    public const long MinPlayableBytes = 32 * 1024;

    /// <summary>Cache de probe em memória (path+mtime → resultado) — evita 3× ffprobe por request.</summary>
    private static readonly ConcurrentDictionary<string, (long MtimeTicks, long Len, PlaybackProbe Probe)> ProbeCache = new();

    /// <summary>
    /// Analisa se o arquivo já é seguro para o browser (H.264/AVC + progressivo).
    /// Um único ffprobe + detecção leve de fMP4.
    /// </summary>
    public async Task<PlaybackProbe> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return new PlaybackProbe(false, null, null, false, "arquivo ausente");

        var fi = new FileInfo(path);
        if (fi.Length < MinPlayableBytes)
            return new PlaybackProbe(false, null, null, false, "arquivo muito pequeno");

        var key = path;
        var mtime = fi.LastWriteTimeUtc.Ticks;
        if (ProbeCache.TryGetValue(key, out var hit)
            && hit.MtimeTicks == mtime && hit.Len == fi.Length)
            return hit.Probe;

        // Uma chamada só: codecs de todas as streams + major_brand.
        var (ok, raw, err) = await RunToolAsync(
            "ffprobe",
            string.Join(' ',
                "-v error",
                "-show_entries stream=index,codec_type,codec_name",
                "-show_entries format_tags=major_brand",
                "-of compact=p=0",
                $"\"{path}\""),
            TimeSpan.FromSeconds(15), ct);

        if (!ok && string.IsNullOrWhiteSpace(raw))
            return new PlaybackProbe(false, null, null, false, err ?? "ffprobe falhou");

        string? video = null;
        string? audio = null;
        var brand = "";

        foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            // compact: stream|index=0|codec_type=video|codec_name=hevc
            //          format|tag:major_brand=iso5
            if (t.Contains("codec_type=video", StringComparison.OrdinalIgnoreCase)
                || (t.Contains("video", StringComparison.OrdinalIgnoreCase) && t.Contains("codec_name", StringComparison.OrdinalIgnoreCase)))
            {
                video ??= ExtractField(t, "codec_name");
            }
            else if (t.Contains("codec_type=audio", StringComparison.OrdinalIgnoreCase)
                     || (t.StartsWith("stream", StringComparison.OrdinalIgnoreCase)
                         && t.Contains("codec_name", StringComparison.OrdinalIgnoreCase)
                         && t.Contains("audio", StringComparison.OrdinalIgnoreCase)))
            {
                audio ??= ExtractField(t, "codec_name");
            }

            if (t.Contains("major_brand", StringComparison.OrdinalIgnoreCase))
            {
                brand = ExtractField(t, "tag:major_brand")
                     ?? ExtractField(t, "major_brand")
                     ?? brand;
            }

            // Fallback linhas simples "hevc" / "h264" do csv
            var low = t.ToLowerInvariant();
            if (video is null && low is "h264" or "hevc" or "h265" or "mpeg4" or "vp9" or "av1")
                video = low is "h265" ? "hevc" : low;
            if (audio is null && (low is "aac" or "mp3" or "opus" || low.StartsWith("pcm")))
                audio = low;
        }

        video = NormalizeCodec(video);
        audio = NormalizeCodec(audio);
        brand = brand.ToLowerInvariant();

        var fragmented = brand is "iso5" or "iso6" or "dash" or "msdh" or "msix"
                      || HasMvexBox(path);

        var videoOk = video is "h264";
        var audioOk = audio is null or "aac" or "mp3";
        var playable = videoOk && audioOk && !fragmented;
        var probe = new PlaybackProbe(playable, video, audio, fragmented, null);

        ProbeCache[key] = (mtime, fi.Length, probe);
        return probe;
    }

    private static string? ExtractField(string line, string name)
    {
        var token = name + "=";
        var i = line.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var start = i + token.Length;
        var end = start;
        while (end < line.Length && line[end] is not '|' and not ' ' and not '\t')
            end++;
        return end > start ? line[start..end].Trim() : null;
    }

    private static string? NormalizeCodec(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var c = raw.Trim().ToLowerInvariant();
        return c is "h265" ? "hevc" : c;
    }

    /// <summary>
    /// Se o arquivo já é playable, devolve o mesmo path.
    /// Caso contrário gera/atualiza cache sidecar <c>*.browser.mp4</c>.
    /// </summary>
    public async Task<string> EnsurePlayableAsync(string path, CancellationToken ct = default)
    {
        if (RecordingCrypto.IsEncryptedPath(path))
            throw new InvalidOperationException("Normalize apenas paths claros; decifre antes.");

        // Cache sidecar válido? Aceita sem re-probe pesado (mtime >= original).
        var cache = BrowserCachePath(path);
        if (File.Exists(cache) && new FileInfo(cache).Length >= MinPlayableBytes
            && File.GetLastWriteTimeUtc(cache) >= File.GetLastWriteTimeUtc(path).AddSeconds(-2))
        {
            // Confia no cache se já probeou como playable ou se major_brand é isom/mp42.
            if (IsLikelyProgressiveH264(cache))
                return cache;
            var cacheProbe = await ProbeAsync(cache, ct);
            if (cacheProbe.Playable) return cache;
        }

        var probe = await ProbeAsync(path, ct);
        if (probe.Playable) return path;

        // Extensão .mp4 obrigatória: sem ela o FFmpeg não infere o muxer (exit -22).
        var tmp = cache + $".tmp_{Guid.NewGuid():N}.mp4";
        try
        {
            await TranscodeToBrowserAsync(path, tmp, probe, ct);
            if (!File.Exists(tmp) || new FileInfo(tmp).Length < MinPlayableBytes)
                throw new InvalidOperationException("FFmpeg não gerou MP4 playable.");

            if (File.Exists(cache))
            {
                try { File.Delete(cache); } catch (IOException) { /* sobrescreve */ }
            }
            File.Move(tmp, cache, overwrite: true);
            ProbeCache.TryRemove(path, out _);
            ProbeCache.TryRemove(cache, out _);
            log.LogInformation(
                "Segmento normalizado para browser: {Src} ({Video}/{Audio}, frag={Frag}) → {Dst}",
                path, probe.VideoCodec ?? "?", probe.AudioCodec ?? "-", probe.Fragmented, cache);
            return cache;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Atalho: ftyp isom/mp42/iso2 + sem mvex → quase sempre H.264 progressivo do nosso pipeline.
    /// Evita ffprobe no hot path de playback quando o cache já existe.
    /// </summary>
    internal static bool IsLikelyProgressiveH264(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 32) return false;
            Span<byte> hdr = stackalloc byte[12];
            if (fs.Read(hdr) < 12) return false;
            // bytes 4-7 = 'ftyp', 8-11 = brand
            if (hdr[4] != (byte)'f' || hdr[5] != (byte)'t' || hdr[6] != (byte)'y' || hdr[7] != (byte)'p')
                return false;
            var brand = Encoding.ASCII.GetString(hdr.Slice(8, 4));
            if (brand is "iso5" or "iso6" or "dash" or "msdh" or "msix")
                return false;
            // isom / iso2 / mp41 / mp42 / avc1 — progressivos típicos
            if (brand is "isom" or "iso2" or "mp41" or "mp42" or "avc1")
                return !HasMvexBox(path);
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Normaliza o segmento no próprio path (substitui o arquivo).
    /// </summary>
    public async Task<bool> NormalizeInPlaceAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MinPlayableBytes)
            return false;

        var probe = await ProbeAsync(path, ct);
        if (probe.Playable) return true;

        var tmp = path + $".norm_{Guid.NewGuid():N}.mp4";
        try
        {
            await TranscodeToBrowserAsync(path, tmp, probe, ct);
            if (!File.Exists(tmp) || new FileInfo(tmp).Length < MinPlayableBytes)
            {
                log.LogWarning("Normalização in-place falhou para {Path}: saída inválida", path);
                return false;
            }

            var created = File.GetCreationTimeUtc(path);
            var written = File.GetLastWriteTimeUtc(path);

            File.Copy(tmp, path, overwrite: true);
            try
            {
                File.SetCreationTimeUtc(path, created);
                File.SetLastWriteTimeUtc(path, written);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            var cache = BrowserCachePath(path);
            try { if (File.Exists(cache)) File.Delete(cache); } catch (IOException) { }

            ProbeCache.TryRemove(path, out _);
            log.LogInformation(
                "Segmento reescrito para browser: {Path} ({Video}→h264, frag={Frag})",
                path, probe.VideoCodec ?? "?", probe.Fragmented);
            return true;
        }
        catch (Exception e)
        {
            log.LogError(e, "Falha ao normalizar segmento {Path}", path);
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }

    public static string BrowserCachePath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        return Path.Combine(dir, name + ".browser.mp4");
    }

    private async Task TranscodeToBrowserAsync(
        string input, string output, PlaybackProbe probe, CancellationToken ct)
    {
        // Remux rápido se já é H.264 (só fMP4 → progressivo).
        // HEVC: ultrafast + CRF alto = conversão bem mais rápida que veryfast/crf23.
        string videoArgs;
        if (probe.VideoCodec is "h264")
            videoArgs = "-c:v copy";
        else
            videoArgs = string.Join(' ',
                "-c:v libx264 -preset ultrafast -tune fastdecode -crf 28",
                "-pix_fmt yuv420p -g 30 -bf 0 -threads 0");

        string audioArgs;
        if (probe.AudioCodec is null)
            audioArgs = "-an";
        else if (probe.AudioCodec is "aac" && probe.VideoCodec is "h264")
            audioArgs = "-c:a copy";
        else
            audioArgs = "-c:a aac -b:a 48k -ar 16000 -ac 1";

        var args = string.Join(' ',
            "-nostdin -hide_banner -loglevel error -y",
            // hwaccel quando disponível (NVDEC/QSV/D3D11) — ignora se falhar no decode
            "-hwaccel auto",
            $"-i \"{input}\"",
            videoArgs,
            audioArgs,
            "-f mp4 -movflags +faststart",
            $"\"{output}\"");

        var (ok, _, err) = await RunToolAsync(_opt.FfmpegPath, args, TimeSpan.FromMinutes(20), ct);
        if (ok) return;

        // Fallback sem hwaccel (alguns builds quebram com -hwaccel auto + copy).
        if (probe.VideoCodec is "h264")
        {
            args = string.Join(' ',
                "-nostdin -hide_banner -loglevel error -y",
                $"-i \"{input}\"",
                "-c:v copy", audioArgs,
                "-f mp4 -movflags +faststart",
                $"\"{output}\"");
            (ok, _, err) = await RunToolAsync(_opt.FfmpegPath, args, TimeSpan.FromMinutes(20), ct);
            if (ok) return;
        }
        else
        {
            args = string.Join(' ',
                "-nostdin -hide_banner -loglevel error -y",
                $"-i \"{input}\"",
                "-c:v libx264 -preset ultrafast -crf 28 -pix_fmt yuv420p -g 30 -bf 0 -threads 0",
                audioArgs,
                "-f mp4 -movflags +faststart",
                $"\"{output}\"");
            (ok, _, err) = await RunToolAsync(_opt.FfmpegPath, args, TimeSpan.FromMinutes(20), ct);
            if (ok) return;
        }

        throw new InvalidOperationException(err ?? "FFmpeg falhou na normalização.");
    }

    /// <summary>Detecção leve de fMP4: procura box 'mvex' nos primeiros 256 KB.</summary>
    internal static bool HasMvexBox(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var len = (int)Math.Min(fs.Length, 256 * 1024);
            var buf = new byte[len];
            var read = fs.Read(buf, 0, len);
            for (var i = 0; i < read - 4; i++)
            {
                if (buf[i] == (byte)'m' && buf[i + 1] == (byte)'v'
                    && buf[i + 2] == (byte)'e' && buf[i + 3] == (byte)'x')
                    return true;
            }
        }
        catch (IOException) { }
        return false;
    }

    private async Task<(bool Ok, string StdOut, string? Error)> RunToolAsync(
        string fileName, string args, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(fileName, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            });
            if (proc is null) return (false, "", "Não foi possível iniciar " + fileName);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(timeout);
            try
            {
                await proc.WaitForExitAsync(limite.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (false, "", $"{fileName} excedeu o tempo limite.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (proc.ExitCode != 0)
                return (false, stdout, $"{fileName} exit {proc.ExitCode}: {stderr.Trim()}");
            return (true, stdout, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (false, "", e.Message);
        }
    }
}

public readonly record struct PlaybackProbe(
    bool Playable,
    string? VideoCodec,
    string? AudioCodec,
    bool Fragmented,
    string? Error);
