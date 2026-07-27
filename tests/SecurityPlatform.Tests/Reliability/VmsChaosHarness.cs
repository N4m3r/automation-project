using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SecurityPlatform.Modules.Vms;

namespace SecurityPlatform.Tests.Reliability;

/// <summary>
/// Harness 2 — chaos do MediaMTX (critério de aceite §8 #2: queda do MediaMTX
/// recupera live e gravação em &lt; 60 s). Exercita a classe de produção
/// <see cref="MediaGateway"/> (Ping / cache / re-registro) contra um MediaMTX
/// real: publica uma câmera, mata o processo, e mede o tempo até o gateway voltar
/// a responder e o path ficar ready de novo.
///
/// Só roda com <c>SP_RELIABILITY=1</c> + FFmpeg + mediamtx.exe; senão, skip.
/// </summary>
public class VmsChaosHarness
{
    private const int CamId = 1;

    [Fact]
    public async Task MediaMtx_kill_recovers_under_60s()
    {
        using var rig = new MediaTestRig();
        if (!MediaTestRig.ShouldRun(rig)) return; // skip silencioso

        var ct = new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token;
        var gateway = NewGateway(rig);
        var path = MediaTestRig.CamPath(CamId);

        // 1) Estado saudável: MediaMTX de pé, câmera publicando, path ready.
        await rig.StartMediaMtxAsync(ct);
        rig.StartPublisher(CamId, seconds: 240);
        Assert.True(await gateway.PingAsync(ct), "gateway deveria estar UP no início");
        Assert.True(await rig.WaitPathReadyAsync(path, TimeSpan.FromSeconds(30), ct),
            "câmera não ficou ready antes do chaos");

        // 2) CHAOS: mata o MediaMTX. Ping precisa refletir a queda.
        rig.KillMediaMtx();
        Assert.True(await WaitUntilAsync(async () => !await gateway.PingAsync(ct),
            TimeSpan.FromSeconds(15), ct), "gateway não detectou a queda do MediaMTX");
        // O que o MediaGatewayHealthService faz ao ver o down: invalida o cache.
        gateway.InvalidateCache();

        // 3) RECOVERY: sobe o MediaMTX de novo e re-publica a câmera (a "câmera"
        //    real continuaria lá; o publisher de teste morre junto com o servidor).
        var sw = Stopwatch.StartNew();
        await rig.StartMediaMtxAsync(ct);
        rig.StartPublisher(CamId, seconds: 120);

        // Gateway responde de novo (equivale a media_gateway_up).
        Assert.True(await WaitUntilAsync(() => gateway.PingAsync(ct),
            TimeSpan.FromSeconds(60), ct), "gateway não voltou a responder em 60 s");

        // Live/gravação recuperados = path pronto a fluir de novo.
        Assert.True(await rig.WaitPathReadyAsync(path, TimeSpan.FromSeconds(60 - (int)sw.Elapsed.TotalSeconds + 5), ct),
            "path não ficou ready após recovery");
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60),
            $"RTO {sw.Elapsed.TotalSeconds:F1}s excedeu o alvo de 60 s (§8 #2)");
    }

    private static MediaGateway NewGateway(MediaTestRig rig)
    {
        var opt = Options.Create(new VmsOptions
        {
            MediaMtxApi = rig.ApiBase,
            MediaMtxRtspHost = "127.0.0.1",
            MediaMtxRtspPort = rig.RtspPort
        });
        return new MediaGateway(new HttpClient { Timeout = TimeSpan.FromSeconds(4) }, opt,
            NullLogger<MediaGateway>.Instance);
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> cond, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            if (await cond()) return true;
            await Task.Delay(300, ct);
        }
        return false;
    }
}
