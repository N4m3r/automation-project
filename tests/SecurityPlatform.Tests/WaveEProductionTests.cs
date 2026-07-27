using System.Diagnostics;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Modules.Vms;

namespace SecurityPlatform.Tests;

public class WaveEProductionTests
{
    [Theory]
    [InlineData("1,2,3,4,5", "08:00", "18:00", DayOfWeek.Monday, 10, 0, true)]
    [InlineData("1,2,3,4,5", "08:00", "18:00", DayOfWeek.Monday, 7, 0, false)]
    [InlineData("1,2,3,4,5", "08:00", "18:00", DayOfWeek.Saturday, 10, 0, false)]
    [InlineData("0,6", "00:00", "23:59", DayOfWeek.Sunday, 12, 0, true)]
    [InlineData("0,1,2,3,4,5,6", "22:00", "06:00", DayOfWeek.Wednesday, 23, 0, true)]
    [InlineData("0,1,2,3,4,5,6", "22:00", "06:00", DayOfWeek.Wednesday, 12, 0, false)]
    public void Access_schedule_evaluator(
        string days, string start, string end, DayOfWeek dow, int hour, int min, bool expected)
    {
        // Usa UTC como "local" com fuso UTC para teste determinístico.
        var sch = new AccessSchedule
        {
            Active = true,
            DaysOfWeek = days,
            StartHm = start,
            EndHm = end,
            TimeZone = "UTC"
        };
        // Monta um UTC que, em UTC, cai no dia/hora pedidos.
        var baseDate = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc); // Monday
        while (baseDate.DayOfWeek != dow) baseDate = baseDate.AddDays(1);
        var utc = new DateTime(baseDate.Year, baseDate.Month, baseDate.Day, hour, min, 0, DateTimeKind.Utc);

        Assert.Equal(expected, AccessScheduleEvaluator.IsOpenNow(sch, utc));
    }

    [Fact]
    public void Schedule_null_sempre_aberto()
    {
        Assert.True(AccessScheduleEvaluator.IsOpenNow(null));
        Assert.True(AccessScheduleEvaluator.IsOpenNow(new AccessSchedule { Active = false }));
    }

    [Fact]
    public void Parse_days_range()
    {
        var d = AccessScheduleEvaluator.ParseDays("1-5");
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, d.OrderBy(x => x));
    }

    [Fact]
    public async Task E2E_ffmpeg_privacy_blur_se_disponivel()
    {
        if (!HasFfmpeg())
        {
            // CI sem FFmpeg: não falha o pipeline unitário.
            return;
        }

        var work = Path.Combine(Path.GetTempPath(), "sp_e2e_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var src = Path.Combine(work, "src.mp4");
            var blurred = Path.Combine(work, "blur.mp4");

            // Gera 1s de vídeo colorido.
            Assert.True(await RunFfmpeg(
                $"-nostdin -hide_banner -loglevel error -y -f lavfi -i testsrc=size=320x240:rate=10 -t 1 -pix_fmt yuv420p \"{src}\""));
            Assert.True(File.Exists(src) && new FileInfo(src).Length > 1000);

            Assert.True(await RunFfmpeg(
                $"-nostdin -hide_banner -loglevel error -y -i \"{src}\" -vf boxblur=20:8 -c:v libx264 -preset ultrafast -crf 28 -an \"{blurred}\""));
            Assert.True(File.Exists(blurred) && new FileInfo(blurred).Length > 500);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* */ }
        }
    }

    [Fact]
    public void ExportOptions_blur_flag()
    {
        var o = new ExportOptions(Watermark: true, BlurFaces: true, UserName: "op");
        Assert.True(o.BlurFaces);
        Assert.True(o.Watermark);
    }

    private static bool HasFfmpeg()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<bool> RunFfmpeg(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        return p.ExitCode == 0;
    }
}
