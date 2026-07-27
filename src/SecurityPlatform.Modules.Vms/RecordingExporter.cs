using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Recorta e junta gravações com FFmpeg.
/// Segmentos .enc são decifrados; paths com Unicode/espaços são preparados em
/// pasta ASCII (%TEMP%) porque o concat demuxer do FFmpeg no Windows falha em
/// caminhos como "...\PROJETO AUTOMAÇÃO\...".
/// </summary>
public class RecordingExporter(
    IOptions<VmsOptions> options,
    RecordingCrypto crypto,
    RecordingNormalizer normalizer,
    ILogger<RecordingExporter> log)
{
    private readonly VmsOptions _opt = options.Value;
    private readonly RecordingNormalizer _normalizer = normalizer;

    /// <summary>Mínimo aceitável para um MP4 exportado (evita ftyp+moov vazio ~262 B).</summary>
    public const long MinExportBytes = 8 * 1024;

    public async Task<ExportResult> ExportAsync(
        int deviceId, IReadOnlyList<Recording> segmentos,
        DateTime from, DateTime to, ExportOptions? exportOpts = null, CancellationToken ct = default)
    {
        from = NormalizeUtc(from);
        to = NormalizeUtc(to);
        if (to <= from)
            return new ExportResult(false, null, "Intervalo invalido.");

        var storageRoot = _opt.StoragePath;
        Directory.CreateDirectory(_opt.ExportPath);

        // Resolve path do banco → disco; descarta incompletos / ausentes.
        var prepared = new List<(Recording Rec, string DiskPath)>();
        foreach (var s in segmentos.OrderBy(x => x.StartedAt))
        {
            var resolved = StoragePaths.ResolveExisting(s.Path, storageRoot);
            if (resolved is null)
            {
                log.LogWarning("Export: segmento {Id} ausente no disco ({Path})", s.Id, s.Path);
                continue;
            }

            long len;
            try { len = new FileInfo(resolved).Length; }
            catch (IOException) { continue; }

            if (len < RecordingNormalizer.MinPlayableBytes)
            {
                log.LogDebug("Export: ignora segmento {Id} muito pequeno ({Len} B)", s.Id, len);
                continue;
            }

            prepared.Add((s, resolved));
        }

        if (prepared.Count == 0)
            return new ExportResult(false, null,
                "Os arquivos do intervalo nao estao mais no disco (ou estao incompletos).");

        var workDir = Path.Combine(Path.GetTempPath(), "sp_export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        var temps = new List<string>();
        var clips = new List<string>();
        var saida = Path.Combine(_opt.ExportPath,
            $"cam{deviceId}_{from:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.mp4");

        try
        {
            // 1) Para cada segmento: path ASCII em workDir + recorte do trecho útil.
            var clipIdx = 0;
            foreach (var (rec, diskPath) in prepared)
            {
                ct.ThrowIfCancellationRequested();

                var recStart = NormalizeUtc(rec.StartedAt);
                var recEnd = rec.EndedAt is null
                    ? recStart.AddSeconds(EstimateDurationSeconds(diskPath, fallback: 120))
                    : NormalizeUtc(rec.EndedAt.Value);

                // Interseção com [from, to]
                var clipFrom = from > recStart ? from : recStart;
                var clipTo = to < recEnd ? to : recEnd;
                if (clipTo <= clipFrom)
                    continue;

                var ss = Math.Max(0, (clipFrom - recStart).TotalSeconds);
                var dur = Math.Max(0.1, (clipTo - clipFrom).TotalSeconds);

                // EndedAt no banco às vezes fica "estourado"; limita pelo tamanho real do arquivo.
                var fileDur = EstimateDurationSeconds(diskPath, fallback: dur + ss + 1);
                if (ss >= fileDur - 0.05)
                    continue;
                if (ss + dur > fileDur)
                    dur = Math.Max(0.1, fileDur - ss);

                string plain;
                try
                {
                    var (p, isTemp) = crypto.EnsurePlainPath(diskPath);
                    if (isTemp) temps.Add(p);
                    plain = p;
                }
                catch (Exception e)
                {
                    log.LogWarning(e, "Export: falha ao decifrar {Path}", diskPath);
                    continue;
                }

                string playable;
                try
                {
                    playable = await _normalizer.EnsurePlayableAsync(plain, ct);
                    if (!string.Equals(playable, plain, StringComparison.OrdinalIgnoreCase)
                        && IsUnderTemp(playable))
                        temps.Add(playable);
                }
                catch (Exception e)
                {
                    log.LogWarning(e, "Export: normalização falhou, usa original {Path}", plain);
                    playable = plain;
                }

                // Copia/link para pasta ASCII (evita AUTOMAÇÃO no concat demuxer).
                var staged = Path.Combine(workDir, $"src_{clipIdx:D3}.mp4");
                if (!await StageForFfmpegAsync(playable, staged, ct))
                {
                    log.LogWarning("Export: nao foi possivel preparar {Path}", playable);
                    continue;
                }
                temps.Add(staged);

                var clipPath = Path.Combine(workDir, $"clip_{clipIdx:D3}.mp4");
                var cutOk = await CutSegmentAsync(staged, clipPath, ss, dur, ct);
                if (!cutOk || !File.Exists(clipPath) || new FileInfo(clipPath).Length < MinExportBytes)
                {
                    // Fallback reencode do trecho
                    cutOk = await CutSegmentAsync(staged, clipPath, ss, dur, ct, reencode: true);
                }

                if (!cutOk || !File.Exists(clipPath) || new FileInfo(clipPath).Length < MinExportBytes)
                {
                    log.LogWarning(
                        "Export: falha ao recortar segmento {Id} ss={Ss:0.###}s dur={Dur:0.###}s",
                        rec.Id, ss, dur);
                    try { if (File.Exists(clipPath)) File.Delete(clipPath); } catch (IOException) { }
                    continue;
                }

                clips.Add(clipPath);
                clipIdx++;
            }

            if (clips.Count == 0)
                return new ExportResult(false, null,
                    "Nao foi possivel recortar nenhum trecho valido no intervalo (segmentos incompletos ou fora da faixa).");

            // 2) Concat dos clips (todos em workDir ASCII).
            var lista = Path.Combine(workDir, "concat.txt");
            await WriteConcatListAsync(lista, clips, ct);

            var concatOut = Path.Combine(workDir, "joined.mp4");
            var joined = await ConcatClipsAsync(lista, concatOut, ct);
            if (!joined || !File.Exists(concatOut) || new FileInfo(concatOut).Length < MinExportBytes)
            {
                // Último recurso: reencode no concat
                joined = await ConcatClipsAsync(lista, concatOut, ct, reencode: true);
            }

            if (!joined || !File.Exists(concatOut) || new FileInfo(concatOut).Length < MinExportBytes)
                return new ExportResult(false, null,
                    "FFmpeg nao conseguiu juntar os trechos exportados.");

            // 3) Marca d'água opcional (fonte Windows explícita — drawtext sem fontfile falha).
            if (exportOpts?.Watermark == true)
            {
                var wmOut = Path.Combine(workDir, "watermarked.mp4");
                var texto = BuildWatermarkText(exportOpts, deviceId, from);
                var wmOk = await ApplyWatermarkAsync(concatOut, wmOut, texto, ct);
                if (wmOk && File.Exists(wmOut) && new FileInfo(wmOut).Length >= MinExportBytes)
                    concatOut = wmOut;
                else
                    log.LogWarning("Export: marca d'água falhou; entrega sem watermark.");
            }

            // 3b) LGPD: blur de privacidade (anonimiza faces/corpos no quadro).
            if (exportOpts?.BlurFaces == true)
            {
                var blurOut = Path.Combine(workDir, "blurred.mp4");
                var blurOk = await ApplyPrivacyBlurAsync(concatOut, blurOut, ct);
                if (blurOk && File.Exists(blurOut) && new FileInfo(blurOut).Length >= MinExportBytes)
                    concatOut = blurOut;
                else
                    log.LogWarning("Export: blur LGPD falhou; entrega sem anonimização.");
            }

            // 3c) Máscaras de privacidade cadastradas (caixas pretas nas ROIs).
            if (exportOpts?.PrivacyBoxes is { Count: > 0 } boxes)
            {
                var maskOut = Path.Combine(workDir, "masked.mp4");
                var maskOk = await ApplyPrivacyBoxesAsync(concatOut, maskOut, boxes, ct);
                if (maskOk && File.Exists(maskOut) && new FileInfo(maskOut).Length >= MinExportBytes)
                    concatOut = maskOut;
                else
                    log.LogWarning("Export: máscaras de privacidade falharam; entrega sem mask.");
            }

            // 4) Copia final para pasta de exports (pode ter Unicode no path do projeto).
            //    FFmpeg -i source ASCII -c copy "destino com acento" funciona no Windows.
            var finalOk = await CopyWithFfmpegOrFileAsync(concatOut, saida, ct);
            if (!finalOk || !File.Exists(saida) || new FileInfo(saida).Length < MinExportBytes)
            {
                try { if (File.Exists(saida)) File.Delete(saida); } catch (IOException) { }
                return new ExportResult(false, null,
                    "Arquivo exportado ficou vazio ou corrompido. Tente um intervalo menor.");
            }

            log.LogInformation(
                "Export cam{Cam} {From:o}..{To:o}: {Clips} clip(s), {Bytes} bytes → {Path}",
                deviceId, from, to, clips.Count, new FileInfo(saida).Length, saida);

            return new ExportResult(true, saida, null);
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(saida)) File.Delete(saida); } catch (IOException) { }
            throw;
        }
        catch (Exception e) when (e is InvalidDataException or CryptographicException or IOException)
        {
            log.LogError(e, "Falha ao preparar segmentos para export");
            try { if (File.Exists(saida)) File.Delete(saida); } catch (IOException) { }
            return new ExportResult(false, null, e.Message);
        }
        finally
        {
            foreach (var tpath in temps)
                try { if (File.Exists(tpath)) File.Delete(tpath); } catch (IOException) { }

            try
            {
                if (Directory.Exists(workDir))
                    Directory.Delete(workDir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static DateTime NormalizeUtc(DateTime dt)
    {
        return dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
        };
    }

    private static double EstimateDurationSeconds(string path, double fallback)
    {
        // Usa tamanho/bitrate aproximado só como teto quando EndedAt é null.
        try
        {
            var len = new FileInfo(path).Length;
            // ~4 Mbit/s típico das gravações atuais → bytes*8/bitrate
            var sec = len * 8.0 / 4_000_000.0;
            return Math.Clamp(sec, 5, 3600);
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Coloca o arquivo em destino ASCII. Prefere hard link; senão copia.
    /// </summary>
    private static async Task<bool> StageForFfmpegAsync(string source, string dest, CancellationToken ct)
    {
        try
        {
            if (File.Exists(dest)) File.Delete(dest);
        }
        catch (IOException) { }

        try
        {
            // Hard link evita copiar dezenas/centenas de MB.
            if (OperatingSystem.IsWindows())
            {
                if (CreateHardLink(dest, source, IntPtr.Zero))
                    return true;
            }
            else
            {
                File.CreateSymbolicLink(dest, source);
                if (File.Exists(dest)) return true;
            }
        }
        catch
        {
            /* fallback copy */
        }

        try
        {
            await using var src = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1024 * 128, useAsync: true);
            await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write,
                FileShare.None, 1024 * 128, useAsync: true);
            await src.CopyToAsync(dst, ct);
            return File.Exists(dest) && new FileInfo(dest).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private async Task<bool> CutSegmentAsync(
        string input, string output, double ss, double dur, CancellationToken ct, bool reencode = false)
    {
        try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }

        var ssS = ss.ToString("0.000", CultureInfo.InvariantCulture);
        var tS = dur.ToString("0.000", CultureInfo.InvariantCulture);

        // -ss antes de -i: seek rápido; para recorte preciso com reencode usamos depois.
        string args;
        if (!reencode)
        {
            args = string.Join(' ',
                "-nostdin -hide_banner -loglevel error -y",
                $"-ss {ssS}",
                $"-i \"{input}\"",
                $"-t {tS}",
                "-c copy -avoid_negative_ts make_zero -movflags +faststart",
                $"\"{output}\"");
        }
        else
        {
            args = string.Join(' ',
                "-nostdin -hide_banner -loglevel error -y",
                $"-i \"{input}\"",
                $"-ss {ssS}",
                $"-t {tS}",
                "-c:v libx264 -preset ultrafast -crf 26 -pix_fmt yuv420p",
                "-c:a aac -b:a 64k -movflags +faststart",
                $"\"{output}\"");
        }

        var (ok, err) = await RunAsync(args, TimeSpan.FromMinutes(10), ct);
        if (!ok)
            log.LogDebug("CutSegment falhou (reencode={Re}): {Err}", reencode, err);
        return ok;
    }

    private static async Task WriteConcatListAsync(string lista, IReadOnlyList<string> clips, CancellationToken ct)
    {
        // UTF-8 sem BOM — BOM quebra o demuxer ("unknown keyword").
        var lines = clips.Select(p =>
        {
            var norm = p.Replace("\\", "/", StringComparison.Ordinal).Replace("'", @"'\''", StringComparison.Ordinal);
            return $"file '{norm}'";
        });
        await File.WriteAllLinesAsync(lista, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
    }

    private async Task<bool> ConcatClipsAsync(string lista, string output, CancellationToken ct, bool reencode = false)
    {
        try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }

        string args;
        if (!reencode)
        {
            args = string.Join(' ',
                "-nostdin -hide_banner -loglevel error -y",
                "-f concat -safe 0",
                $"-i \"{lista}\"",
                "-c copy -movflags +faststart",
                $"\"{output}\"");
        }
        else
        {
            args = string.Join(' ',
                "-nostdin -hide_banner -loglevel error -y",
                "-f concat -safe 0",
                $"-i \"{lista}\"",
                "-c:v libx264 -preset ultrafast -crf 28 -pix_fmt yuv420p",
                "-c:a aac -b:a 48k -movflags +faststart",
                $"\"{output}\"");
        }

        var (ok, err) = await RunAsync(args, TimeSpan.FromMinutes(20), ct);
        if (!ok)
            log.LogWarning("Concat falhou (reencode={Re}): {Err}", reencode, err);
        return ok;
    }

    private async Task<bool> ApplyWatermarkAsync(string input, string output, string text, CancellationToken ct)
    {
        try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }

        var font = FindDrawTextFont();
        var escaped = EscapeDrawText(text);
        var fontOpt = font is null
            ? ""
            : $"fontfile={EscapeDrawTextFontPath(font)}:";

        // Sem fonte o drawtext costuma falhar no Windows (fontconfig ausente).
        if (font is null)
        {
            log.LogWarning("Export: nenhuma fonte TTF encontrada; pula watermark.");
            return false;
        }

        var args = string.Join(' ',
            "-nostdin -hide_banner -loglevel error -y",
            $"-i \"{input}\"",
            $"-vf \"drawtext={fontOpt}text='{escaped}':fontsize=18:fontcolor=white@0.9:box=1:boxcolor=black@0.45:boxborderw=6:x=24:y=h-th-24\"",
            "-c:v libx264 -preset ultrafast -crf 26 -pix_fmt yuv420p -c:a copy -movflags +faststart",
            $"\"{output}\"");

        var (ok, err) = await RunAsync(args, TimeSpan.FromMinutes(20), ct);
        if (!ok)
        {
            // Áudio copy pode falhar se o stream for incompatível — tenta reencode áudio.
            args = string.Join(' ',
                "-nostdin -hide_banner -loglevel error -y",
                $"-i \"{input}\"",
                $"-vf \"drawtext={fontOpt}text='{escaped}':fontsize=18:fontcolor=white@0.9:box=1:boxcolor=black@0.45:boxborderw=6:x=24:y=h-th-24\"",
                "-c:v libx264 -preset ultrafast -crf 26 -pix_fmt yuv420p -c:a aac -b:a 64k -movflags +faststart",
                $"\"{output}\"");
            (ok, err) = await RunAsync(args, TimeSpan.FromMinutes(20), ct);
        }

        if (!ok)
            log.LogWarning("Watermark FFmpeg falhou: {Err}", err);
        return ok;
    }

    /// <summary>
    /// Anonimização LGPD: boxblur forte no quadro inteiro + leveção legível.
    /// Não usa ML de face; remove identificação visual de pessoas no export.
    /// </summary>
    private async Task<bool> ApplyPrivacyBlurAsync(string input, string output, CancellationToken ct)
    {
        try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }

        // boxblur=luma_radius:luma_power — valores altos anonimizam faces/corpos.
        var args = string.Join(' ',
            "-nostdin -hide_banner -loglevel error -y",
            $"-i \"{input}\"",
            "-vf \"boxblur=20:8\"",
            "-c:v libx264 -preset ultrafast -crf 28 -pix_fmt yuv420p -c:a aac -b:a 64k -movflags +faststart",
            $"\"{output}\"");

        var (ok, err) = await RunAsync(args, TimeSpan.FromMinutes(30), ct);
        if (!ok)
            log.LogWarning("Privacy blur FFmpeg falhou: {Err}", err);
        return ok;
    }

    private async Task<bool> ApplyPrivacyBoxesAsync(
        string input, string output,
        IReadOnlyList<(double X, double Y, double W, double H)> boxes,
        CancellationToken ct)
    {
        var filter = PrivacyMaskHelper.BuildDrawboxFilter(boxes);
        if (string.IsNullOrEmpty(filter)) return false;

        try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }

        var args = string.Join(' ',
            "-nostdin -hide_banner -loglevel error -y",
            $"-i \"{input}\"",
            $"-vf \"{filter}\"",
            "-c:v libx264 -preset ultrafast -crf 23 -pix_fmt yuv420p -c:a copy -movflags +faststart",
            $"\"{output}\"");

        var (ok, err) = await RunAsync(args, TimeSpan.FromMinutes(30), ct);
        if (!ok)
            log.LogWarning("Privacy boxes FFmpeg falhou: {Err}", err);
        return ok;
    }

    private static string? FindDrawTextFont()
    {
        var candidates = new[]
        {
            @"C:\Windows\Fonts\arial.ttf",
            @"C:\Windows\Fonts\segoeui.ttf",
            @"C:\Windows\Fonts\calibri.ttf",
            @"C:\Windows\Fonts\tahoma.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
            "/System/Library/Fonts/Supplemental/Arial.ttf"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Escapa path de fonte para drawtext (C: → C\:).</summary>
    internal static string EscapeDrawTextFontPath(string path)
    {
        var p = path.Replace("\\", "/", StringComparison.Ordinal);
        // Dois-pontos do drive Windows precisa de escape no filtergraph.
        p = p.Replace(":", "\\:", StringComparison.Ordinal);
        return p;
    }

    private async Task<bool> CopyWithFfmpegOrFileAsync(string src, string dest, CancellationToken ct)
    {
        try
        {
            // Preferência: File.Copy (mais simples; paths Unicode OK no .NET).
            File.Copy(src, dest, overwrite: true);
            return File.Exists(dest) && new FileInfo(dest).Length >= MinExportBytes;
        }
        catch (IOException e)
        {
            log.LogWarning(e, "File.Copy export falhou, tenta FFmpeg");
        }

        var args = string.Join(' ',
            "-nostdin -hide_banner -loglevel error -y",
            $"-i \"{src}\"",
            "-c copy -movflags +faststart",
            $"\"{dest}\"");
        var (ok, _) = await RunAsync(args, TimeSpan.FromMinutes(5), ct);
        return ok && File.Exists(dest) && new FileInfo(dest).Length >= MinExportBytes;
    }

    private static bool IsUnderTemp(string path)
    {
        try
        {
            var temp = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(path);
            return full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(temp + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildWatermarkText(ExportOptions opt, int deviceId, DateTime from)
    {
        var user = string.IsNullOrWhiteSpace(opt.UserName) ? "export" : opt.UserName;
        var server = string.IsNullOrWhiteSpace(opt.ServerName) ? "SecurityPlatform" : opt.ServerName;
        return $"{server} | cam {deviceId} | {user} | {from:yyyy-MM-dd HH:mm} UTC";
    }

    internal static string EscapeDrawText(string text)
        => text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal);

    public async Task<byte[]?> GrabFrameAsync(string rtspUrl, CancellationToken ct = default)
    {
        var destino = Path.Combine(Path.GetTempPath(), $"snap_{Guid.NewGuid():N}.jpg");
        try
        {
            var args = string.Join(' ',
                "-nostdin -hide_banner -loglevel error -y",
                "-rtsp_transport tcp -timeout 5000000",
                $"-i \"{rtspUrl}\"",
                "-frames:v 1 -q:v 3",
                $"\"{destino}\"");

            var (ok, _) = await RunAsync(args, TimeSpan.FromSeconds(15), ct);
            return ok && File.Exists(destino) ? await File.ReadAllBytesAsync(destino, ct) : null;
        }
        finally
        {
            try { if (File.Exists(destino)) File.Delete(destino); } catch (IOException) { }
        }
    }

    private async Task<(bool Ok, string? Error)> RunAsync(
        string args, TimeSpan timeout, CancellationToken ct)
    {
        using var proc = Process.Start(new ProcessStartInfo(_opt.FfmpegPath, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        });

        if (proc is null) return (false, "Nao foi possivel iniciar o FFmpeg.");

        var stderr = await proc.StandardError.ReadToEndAsync(ct);

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(timeout);

        try
        {
            await proc.WaitForExitAsync(limite.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (false, "O FFmpeg excedeu o tempo limite.");
        }

        if (proc.ExitCode == 0) return (true, null);

        var mascarado = UrlMasking.Mask(stderr);
        log.LogWarning("FFmpeg falhou ({Code}): {Erro}", proc.ExitCode, mascarado);
        return (false, string.IsNullOrWhiteSpace(mascarado)
            ? $"FFmpeg retornou {proc.ExitCode}."
            : $"FFmpeg retornou {proc.ExitCode}: {TrimErr(mascarado)}");
    }

    private static string TrimErr(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 240 ? s : s[..240] + "…";
    }
}

public record ExportResult(bool Ok, string? Path, string? Error);

public record ExportOptions(
    bool Watermark = false,
    string? UserName = null,
    string? ServerName = null,
    bool BlurFaces = false,
    IReadOnlyList<(double X, double Y, double W, double H)>? PrivacyBoxes = null);
