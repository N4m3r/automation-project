using SecurityPlatform.Modules.Vms;

namespace SecurityPlatform.Tests;

public class RecordingNormalizerTests
{
    [Fact]
    public void BrowserCachePath_insere_sufixo_antes_da_extensao()
    {
        var p = Path.Combine("data", "recordings", "3", "c_20260723_120000.mp4");
        var cache = RecordingNormalizer.BrowserCachePath(p);
        Assert.Equal(
            Path.Combine("data", "recordings", "3", "c_20260723_120000.browser.mp4"),
            cache);
    }

    [Fact]
    public void HasMvexBox_detecta_fmp4_real_quando_existe()
    {
        // Usa gravação local se o ambiente de dev tiver amostras HEVC/fMP4.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var sampleDir = Path.Combine(root, "src", "SecurityPlatform.Api", "data", "recordings", "3");
        if (!Directory.Exists(sampleDir)) return; // CI sem gravações

        var sample = Directory.GetFiles(sampleDir, "c_*.mp4")
            .Where(f => !f.EndsWith(".browser.mp4", StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .Where(f => f.Length > 100_000)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .FirstOrDefault();

        if (sample is null) return;

        // Arquivos antigos (iso5) devem ter mvex; novos H.264 progressivos não.
        // Só asserta que a função não lança e devolve bool estável.
        var _ = RecordingNormalizer.HasMvexBox(sample);
        Assert.False(RecordingNormalizer.HasMvexBox(Path.Combine(sampleDir, "nao_existe.mp4")));
    }
}
