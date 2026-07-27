using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Fingerprint visual leve para comparação de rostos sem GPU/ML pesado.
///
/// Pipeline: JPEG → FFmpeg (64×64 gray) → vetor de 72 floats
/// (64 médias de blocos 8×8 + 8 histogramas de gradiente) L2-normalizado.
/// Similaridade = cosseno (0..1). Bom o suficiente para galeria e varredura
/// de snapshots; câmeras com faceId externo continuam no match por ID.
/// </summary>
public sealed class FaceFingerprint(IOptions<VmsOptions> options)
{
    public const int Grid = 8;
    public const int Side = 64;
    public const int FeatureLen = Grid * Grid + Grid; // 64 + 8 = 72
    public const float DefaultThreshold = 0.78f;

    private readonly string _ffmpeg = options.Value.FfmpegPath;

    /// <summary>Extrai o vetor a partir de bytes JPEG/PNG.</summary>
    public async Task<float[]?> FromImageAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        if (imageBytes is null || imageBytes.Length < 32) return null;

        var gray = await ToGray64Async(imageBytes, ct);
        return gray is null ? null : FromGray64(gray);
    }

    /// <summary>Constrói o vetor a partir de 64×64 grayscale (4096 bytes).</summary>
    public static float[] FromGray64(ReadOnlySpan<byte> gray)
    {
        if (gray.Length < Side * Side)
            throw new ArgumentException("Esperado 64×64 gray.", nameof(gray));

        var feat = new float[FeatureLen];
        var blockCounts = new int[Grid * Grid];

        // Médias de blocos 8×8
        for (var y = 0; y < Side; y++)
        {
            var by = y * Grid / Side;
            for (var x = 0; x < Side; x++)
            {
                var bx = x * Grid / Side;
                var bi = by * Grid + bx;
                feat[bi] += gray[y * Side + x];
                blockCounts[bi]++;
            }
        }
        for (var i = 0; i < Grid * Grid; i++)
            feat[i] = blockCounts[i] > 0 ? feat[i] / (blockCounts[i] * 255f) : 0f;

        // Histograma de gradiente horizontal por faixa (8 faixas)
        for (var by = 0; by < Grid; by++)
        {
            var y0 = by * Side / Grid;
            var y1 = (by + 1) * Side / Grid;
            double acc = 0;
            var n = 0;
            for (var y = y0; y < y1; y++)
            {
                for (var x = 0; x < Side - 1; x++)
                {
                    acc += Math.Abs(gray[y * Side + x + 1] - gray[y * Side + x]);
                    n++;
                }
            }
            feat[Grid * Grid + by] = n > 0 ? (float)(acc / (n * 255.0)) : 0f;
        }

        L2Normalize(feat);
        return feat;
    }

    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n == 0) return 0f;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < n; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na < 1e-12 || nb < 1e-12) return 0f;
        var c = (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
        // Cosseno em vetores L2-normalizados ≈ [-1,1]; clamp para score 0..1
        return Math.Clamp((c + 1f) * 0.5f, 0f, 1f);
    }

    /// <summary>
    /// Similaridade entre vetores já L2-normalizados (dot product mapeado 0..1).
    /// Preferir quando ambos vieram de <see cref="FromGray64"/>.
    /// </summary>
    public static float Similarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => Cosine(a, b);

    public static string Encode(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    public static float[]? Decode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;

        // Formato preferido: base64 de float32[]
        try
        {
            var bytes = Convert.FromBase64String(encoded.Trim());
            if (bytes.Length >= sizeof(float) * 8 && bytes.Length % sizeof(float) == 0)
            {
                var n = bytes.Length / sizeof(float);
                var v = new float[n];
                Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
                return v;
            }
        }
        catch (FormatException) { /* tenta JSON */ }

        // Compat: JSON array [0.1, 0.2, ...]
        try
        {
            var arr = JsonSerializer.Deserialize<float[]>(encoded);
            return arr is { Length: > 0 } ? arr : null;
        }
        catch { return null; }
    }

    public static void L2Normalize(Span<float> v)
    {
        double sum = 0;
        for (var i = 0; i < v.Length; i++) sum += v[i] * v[i];
        if (sum < 1e-12) return;
        var inv = (float)(1.0 / Math.Sqrt(sum));
        for (var i = 0; i < v.Length; i++) v[i] *= inv;
    }

    /// <summary>Decodifica data URL ou base64 puro em bytes de imagem.</summary>
    public static byte[]? DecodeImagePayload(string? imageBase64)
    {
        if (string.IsNullOrWhiteSpace(imageBase64)) return null;
        var s = imageBase64.Trim();
        var comma = s.IndexOf(',');
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            s = s[(comma + 1)..];
        try { return Convert.FromBase64String(s); }
        catch { return null; }
    }

    private async Task<byte[]?> ToGray64Async(byte[] imageBytes, CancellationToken ct)
    {
        var tmpIn = Path.Combine(Path.GetTempPath(), $"face_in_{Guid.NewGuid():N}.img");
        try
        {
            await File.WriteAllBytesAsync(tmpIn, imageBytes, ct);

            // rawvideo gray 64x64 no stdout — sem arquivo intermediário
            var args = string.Join(' ',
                "-nostdin -hide_banner -loglevel error -y",
                $"-i \"{tmpIn}\"",
                $"-vf scale={Side}:{Side}:flags=area,format=gray",
                "-f rawvideo -pix_fmt gray -");

            using var proc = Process.Start(new ProcessStartInfo(_ffmpeg, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (proc is null) return null;

            using var ms = new MemoryStream(Side * Side);
            var copyOut = proc.StandardOutput.BaseStream.CopyToAsync(ms, ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct);

            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                await proc.WaitForExitAsync(limite.Token);
                await copyOut;
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }

            _ = await errTask;
            if (proc.ExitCode != 0) return null;

            var gray = ms.ToArray();
            return gray.Length >= Side * Side ? gray.AsSpan(0, Side * Side).ToArray() : null;
        }
        finally
        {
            try { if (File.Exists(tmpIn)) File.Delete(tmpIn); } catch (IOException) { }
        }
    }
}

/// <summary>Resultado de um match na galeria.</summary>
public sealed record FaceGalleryHit(
    int Id,
    string Name,
    string ExternalFaceId,
    string ListType,
    string PhotoUrl,
    float Score,
    string Notes);

/// <summary>Resultado de varredura em câmera ao vivo.</summary>
public sealed record FaceCameraHit(
    int CameraId,
    string CameraName,
    float Score,
    bool SnapshotOk,
    string? Error = null);

/// <summary>Hit em evento histórico de face.</summary>
public sealed record FaceEventHit(
    long EventId,
    int? DeviceId,
    string? DeviceName,
    string Type,
    DateTime CreatedAt,
    string? FaceId,
    string? PersonName,
    float? Score,
    int Severity);
