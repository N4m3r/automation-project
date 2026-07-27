using System.Diagnostics;

namespace SecurityPlatform.Tests.Reliability;

/// <summary>
/// Harness 1 — soak test (critério de aceite §8 #1: N câmeras contínuas sem stall
/// não-alarmado). Publica N câmeras sintéticas no MediaMTX e grava todas em
/// segmentos por uma janela configurável, exigindo que <b>todas</b> continuem
/// produzindo segmentos até o fim (sem stall).
///
/// <para>
/// Escala por variáveis de ambiente (default = smoke curto, para provar o
/// pipeline sem esperar 24 h):
/// </para>
/// <list type="bullet">
///   <item><c>SP_SOAK_CAMS</c> — nº de câmeras (default 4; go-live: 50)</item>
///   <item><c>SP_SOAK_SECONDS</c> — duração da janela (default 40; go-live: 86400)</item>
///   <item><c>SP_SOAK_SEGMENT</c> — segundos por segmento (default 10)</item>
/// </list>
///
/// Só roda com <c>SP_RELIABILITY=1</c> + FFmpeg + mediamtx.exe; senão, skip.
/// </summary>
public class VmsSoakHarness
{
    [Fact]
    public async Task Soak_all_cameras_record_without_stall()
    {
        using var rig = new MediaTestRig();
        if (!MediaTestRig.ShouldRun(rig)) return; // skip silencioso

        var cams = EnvInt("SP_SOAK_CAMS", 4);
        var seconds = EnvInt("SP_SOAK_SECONDS", 40);
        var segment = EnvInt("SP_SOAK_SEGMENT", 10);
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 120)).Token;

        await rig.StartMediaMtxAsync(ct);

        // 1) Sobe N câmeras sintéticas (stagger leve p/ evitar tempestade de
        //    handshakes RTSP quando muitos publishers sobem juntos).
        for (var id = 1; id <= cams; id++)
        {
            rig.StartPublisher(id, seconds + 30);
            await Task.Delay(250, ct);
        }

        // 2) Todas as câmeras precisam ficar ready antes de gravar.
        for (var id = 1; id <= cams; id++)
        {
            var ready = await rig.WaitPathReadyAsync(MediaTestRig.CamPath(id), TimeSpan.FromSeconds(45), ct);
            Assert.True(ready, $"cam{id} não ficou ready no MediaMTX");
        }

        // 3) Só então liga os gravadores (leem o path já fluindo).
        var recDirs = new Dictionary<int, string>();
        for (var id = 1; id <= cams; id++)
            recDirs[id] = rig.StartRecorder(id, segment);

        // 4) Soak: amostra a contagem de segmentos ao longo da janela e garante
        //    progresso monotônico em TODAS as câmeras (stall = contagem parada).
        var stallStrikes = new Dictionary<int, int>();
        var lastCount = recDirs.ToDictionary(kv => kv.Key, kv => 0);
        var sw = Stopwatch.StartNew();
        // Após ~1.5 segmentos já deve haver arquivo; damos folga antes de exigir progresso.
        var graceUntil = TimeSpan.FromSeconds(segment * 2 + 5);
        var pollEvery = TimeSpan.FromSeconds(Math.Max(segment, 5));

        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            await Task.Delay(pollEvery, ct);
            foreach (var (id, dir) in recDirs)
            {
                var now = MediaTestRig.CountSegments(dir);
                if (sw.Elapsed > graceUntil)
                {
                    if (now <= lastCount[id]) stallStrikes[id] = stallStrikes.GetValueOrDefault(id) + 1;
                    else stallStrikes[id] = 0;
                    // 2 janelas seguidas sem novo segmento = stall real.
                    Assert.True(stallStrikes[id] < 2,
                        $"cam{id} travou: {now} segmentos parados por 2 janelas (>{segment * 2}s)");
                }
                lastCount[id] = now;
            }
        }

        // 5) Todas produziram ao menos alguns segmentos.
        var expectedMin = Math.Max(1, (seconds / segment) - 1);
        foreach (var (id, dir) in recDirs)
        {
            var count = MediaTestRig.CountSegments(dir);
            Assert.True(count >= expectedMin,
                $"cam{id} gravou {count} segmentos (esperado >= {expectedMin})");
        }
    }

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
}
