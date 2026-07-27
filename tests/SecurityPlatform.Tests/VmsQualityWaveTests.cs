using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Modules.Vms;

namespace SecurityPlatform.Tests;

/// <summary>Onda VMS Quality — pre-event, crypto streaming, cluster lock, E2E FFmpeg.</summary>
public class VmsQualityWaveTests
{
    [Fact]
    public void PreEvent_effective_seconds_respects_device_and_global()
    {
        var opt = new VmsOptions { PreEventSeconds = 20 };
        var cam = new Device { PreEventSeconds = 10 };
        Assert.Equal(10, opt.EffectivePreEventSeconds(cam));

        cam.PreEventSeconds = 0;
        Assert.Equal(0, opt.EffectivePreEventSeconds(cam));

        cam.PreEventSeconds = -1; // herda global
        Assert.Equal(20, opt.EffectivePreEventSeconds(cam));
    }

    [Fact]
    public void Storage_cluster_lock_writes_uuid()
    {
        var root = Path.Combine(Path.GetTempPath(), "sp_cluster_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var opt = Microsoft.Extensions.Options.Options.Create(new VmsOptions
            {
                StoragePath = root,
                ClusterId = ""
            });
            var lockSvc = new StorageClusterLock(opt, NullLogger<StorageClusterLock>.Instance);
            var id = lockSvc.EnsureAndValidate();
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.True(File.Exists(Path.Combine(root, StorageClusterLock.FileName)));
            Assert.Equal(id, StorageClusterLock.Read(root));

            // Segundo boot com mesmo id ok
            opt.Value.ClusterId = id;
            Assert.Equal(id, lockSvc.EnsureAndValidate());

            // Divergência aborta
            opt.Value.ClusterId = Guid.NewGuid().ToString("D");
            Assert.Throws<InvalidOperationException>(() => lockSvc.EnsureAndValidate());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* */ }
        }
    }

    [Fact]
    public void Recording_crypto_roundtrip_streaming()
    {
        var provider = DataProtectionProvider.Create(Path.Combine(Path.GetTempPath(),
            "sp_dp_" + Guid.NewGuid().ToString("N")));
        var crypto = new RecordingCrypto(provider, NullLogger<RecordingCrypto>.Instance);

        var work = Path.Combine(Path.GetTempPath(), "sp_crypto_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // > 1 MiB para exercitar multi-frame
            var plain = Path.Combine(work, "seg.mp4");
            var payload = RandomNumberGenerator.GetBytes(1_200_000);
            // Prefixo fake de MP4
            payload[0] = 0; payload[1] = 0; payload[2] = 0; payload[3] = 0x20;
            File.WriteAllBytes(plain, payload);

            var enc = crypto.EncryptFile(plain);
            Assert.True(RecordingCrypto.IsEncryptedPath(enc));
            Assert.False(File.Exists(plain));

            var (path, isTemp) = crypto.EnsurePlainPath(enc);
            Assert.True(File.Exists(path));
            var round = File.ReadAllBytes(path);
            Assert.Equal(payload, round);

            if (isTemp) try { File.Delete(path); } catch { /* */ }
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* */ }
        }
    }

    [Fact]
    public void Retention_recognizes_prebuffer_prefix()
    {
        Assert.True(RetentionService.IsSegmentoGravacao(@"C:\data\1\p_20260724_120000.mp4"));
        Assert.True(RetentionService.IsSegmentoGravacao(@"C:\data\1\e_20260724_120000.mp4"));
        Assert.True(RetentionService.IsSegmentoGravacao(@"C:\data\1\c_20260724_120000.mp4"));
        Assert.False(RetentionService.IsSegmentoGravacao(@"C:\data\1\x_20260724_120000.mp4"));
        Assert.False(RetentionService.IsSegmentoGravacao(@"C:\data\1\c_20260724_120000.browser.mp4"));
    }

    [Fact]
    public void Timeline_thumb_floor_interval()
    {
        var t = new DateTime(2026, 7, 24, 12, 37, 44, DateTimeKind.Utc);
        var floor = ThumbnailService.FloorToInterval(t, TimeSpan.FromMinutes(10));
        Assert.Equal(new DateTime(2026, 7, 24, 12, 30, 0, DateTimeKind.Utc), floor);
    }

    [Fact]
    public void Pick_volume_returns_existing_primary()
    {
        var root = Path.Combine(Path.GetTempPath(), "sp_vol_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var picked = StoragePaths.PickVolume(root, null);
            Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(picked));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* */ }
        }
    }

    [Fact]
    public async Task E2E_ffmpeg_record_normalize_export_path_if_available()
    {
        if (!HasFfmpeg()) return;

        var work = Path.Combine(Path.GetTempPath(), "sp_e2e_vms_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var src = Path.Combine(work, "src.mp4");
            // 3s color bars
            Assert.True(await RunFfmpeg(
                $"-nostdin -hide_banner -loglevel error -y -f lavfi -i testsrc=size=320x240:rate=10 -t 3 -pix_fmt yuv420p -c:v libx264 -preset ultrafast \"{src}\""));
            Assert.True(File.Exists(src) && new FileInfo(src).Length > 1000);

            // Segment style copy (simula gravador)
            var seg = Path.Combine(work, "c_20260724_120000.mp4");
            File.Copy(src, seg);

            Assert.True(RetentionService.IsSegmentoGravacao(seg));
            Assert.True(RetentionService.HasMoovAtom(seg));

            var parsed = RetentionService.ParseStart("c_20260724_120000");
            Assert.NotNull(parsed);
            Assert.Equal(2026, parsed!.Value.Year);

            // “Export” simples = re-mux
            var exp = Path.Combine(work, "export.mp4");
            Assert.True(await RunFfmpeg(
                $"-nostdin -hide_banner -loglevel error -y -i \"{seg}\" -c copy \"{exp}\""));
            Assert.True(File.Exists(exp) && new FileInfo(exp).Length >= 1024);

            var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exp)));
            Assert.Equal(64, sha.Length);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* */ }
        }
    }

    [Fact]
    public void Vms_metrics_render_prometheus()
    {
        var m = new VmsMetrics();
        m.SetRecordingActive(3);
        m.SetMediaGatewayUp(true);
        m.IncExport(150);
        m.IncSegment(1024);
        m.IncGap();
        var text = m.RenderPrometheus();
        Assert.Contains("vms_recording_active", text);
        Assert.Contains("vms_exports_total", text);
        Assert.Contains("vms_media_gateway_up", text);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("redis://localhost:6379", true)]
    [InlineData("rediss://user:pass@redis.example:6380", true)]
    [InlineData("localhost:6379,abortConnect=false", true)]
    [InlineData("127.0.0.1:6379", true)]
    public void EventBus_redis_detection(string cfg, bool expected)
        => Assert.Equal(expected, EventBusRegistration.IsRedisConfigured(cfg));

    [Fact]
    public void Redis_connection_string_normalize()
    {
        var cs = RedisEventBus.NormalizeConnectionString("redis://localhost:6379");
        Assert.Contains("localhost:6379", cs);
        Assert.Contains("abortConnect=false", cs);

        var tls = RedisEventBus.NormalizeConnectionString("rediss://:s3cret@db:6380");
        Assert.Contains("db:6380", tls);
        Assert.Contains("ssl=true", tls);
        Assert.Contains("password=s3cret", tls);
    }

    [Fact]
    public void Privacy_mask_parse_and_drawbox()
    {
        var json = """[{"points":[[0.1,0.2],[0.4,0.2],[0.4,0.5],[0.1,0.5]]}]""";
        var polys = PrivacyMaskHelper.Parse(json);
        Assert.Single(polys);
        Assert.Equal(4, polys[0].Points.Count);

        var boxes = PrivacyMaskHelper.ToBoundingBoxes(polys);
        Assert.Single(boxes);
        Assert.True(boxes[0].W > 0.2 && boxes[0].H > 0.2);

        var filter = PrivacyMaskHelper.BuildDrawboxFilter(boxes);
        Assert.NotNull(filter);
        Assert.Contains("drawbox=", filter);
        Assert.Contains("t=fill", filter);
    }

    [Fact]
    public void Privacy_mask_invalid_json_empty()
    {
        Assert.Empty(PrivacyMaskHelper.Parse("not-json"));
        Assert.Empty(PrivacyMaskHelper.Parse("[]"));
        Assert.Empty(PrivacyMaskHelper.Parse(null));
    }

    private static bool HasFfmpeg()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
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

    private static async Task<bool> RunFfmpeg(string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (p is null) return false;
        await p.WaitForExitAsync();
        return p.ExitCode == 0;
    }
}
