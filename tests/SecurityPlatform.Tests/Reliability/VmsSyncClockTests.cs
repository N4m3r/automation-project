using SecurityPlatform.Modules.Vms;

namespace SecurityPlatform.Tests.Reliability;

/// <summary>
/// Harness 3 — sincronismo de playback multi-câmera (critério de aceite §8 #7:
/// 4 canais sync ±200 ms). Determinístico: exercita o cálculo do relógio mestre
/// (<see cref="SyncClock"/>) sem player/browser. Roda no CI normal.
/// </summary>
public class VmsSyncClockTests
{
    // Instante mestre de referência: 2026-07-24 12:00:30 UTC.
    private static readonly double Master =
        new DateTimeOffset(2026, 7, 24, 12, 0, 30, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static double StartedAt(int h, int m, int s) =>
        new DateTimeOffset(2026, 7, 24, h, m, s, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public void SlaveTarget_offset_from_segment_start()
    {
        // Segmento começou 12:00:00, mestre é 12:00:30 → alvo = 30 s.
        var t = SyncClock.SlaveTargetSeconds(Master, StartedAt(12, 0, 0), durationSec: 600);
        Assert.NotNull(t);
        Assert.Equal(30, t!.Value, precision: 3);
    }

    [Fact]
    public void SlaveTarget_null_when_master_before_or_after_segment()
    {
        // Segmento 12:01:00–12:11:00: o instante mestre 12:00:30 é anterior.
        Assert.Null(SyncClock.SlaveTargetSeconds(Master, StartedAt(12, 1, 0), 600));
        // Segmento 11:00:00 + 60 s termina 11:01:00: mestre 12:00:30 é posterior.
        Assert.Null(SyncClock.SlaveTargetSeconds(Master, StartedAt(11, 0, 0), 60));
    }

    [Fact]
    public void Drift_zero_when_player_at_target()
    {
        var started = StartedAt(12, 0, 0);
        var target = SyncClock.SlaveTargetSeconds(Master, started, 600)!.Value;
        var drift = SyncClock.DriftMs(Master, started, target);
        Assert.True(drift < 1, $"deriva no alvo deveria ser ~0, foi {drift} ms");
    }

    [Theory]
    [InlineData(150, false)] // dentro da tolerância
    [InlineData(200, false)] // exatamente na tolerância (não re-seek)
    [InlineData(201, true)]  // acima → re-seek
    [InlineData(1500, true)]
    public void NeedsResync_at_200ms_boundary(double driftMs, bool expected)
        => Assert.Equal(expected, SyncClock.NeedsResync(driftMs));

    [Fact]
    public void FourChannel_alignment_within_200ms()
    {
        // 4 câmeras, segmentos com inícios diferentes (grava em momentos distintos),
        // cada player parado num currentTime arbitrário. Após alinhar ao mestre,
        // a pior deriva deve cair para 0 e nenhum canal fica fora de ±200 ms.
        var slaves = new[]
        {
            new SyncClock.SlaveState(1, StartedAt(12, 0, 0), 600, CurrentTimeSec: 5),    // exibindo 12:00:05
            new SyncClock.SlaveState(2, StartedAt(11, 55, 0), 600, CurrentTimeSec: 120), // exibindo 11:57:00
            new SyncClock.SlaveState(3, StartedAt(12, 0, 12), 600, CurrentTimeSec: 300), // exibindo 12:05:12
            new SyncClock.SlaveState(4, StartedAt(12, 0, 0),  600, CurrentTimeSec: 599), // exibindo 12:09:59
        };

        // Antes de alinhar: pelo menos um canal está bem fora.
        Assert.True(SyncClock.WorstDriftMs(Master, slaves) > 200);

        var aligned = SyncClock.AlignAll(Master, slaves);
        Assert.Equal(4, aligned.Count);

        // Todos têm imagem no instante mestre (12:00:30 cai dentro dos 4 segmentos).
        Assert.All(aligned, a => Assert.True(a.HasFrame, $"cam {a.CameraId} sem imagem em t mestre"));

        // Aplicando o seek recomendado, simula os players na posição alvo e mede a deriva.
        var afterSeek = aligned.Select((a, idx) =>
            new SyncClock.SlaveState(a.CameraId, slaves[idx].StartedAtMs, slaves[idx].DurationSec, a.TargetSeconds!.Value));
        var worst = SyncClock.WorstDriftMs(Master, afterSeek);
        Assert.True(worst <= 200, $"pior deriva pós-alinhamento {worst} ms > 200 ms");
    }

    [Fact]
    public void Channel_out_of_segment_flagged_not_synced()
    {
        // cam 2 só tem gravação até 11:56:00 → sem imagem no instante mestre.
        var slaves = new[]
        {
            new SyncClock.SlaveState(1, StartedAt(12, 0, 0), 600, 5),
            new SyncClock.SlaveState(2, StartedAt(11, 50, 0), 360, 100), // termina 11:56:00
        };
        var aligned = SyncClock.AlignAll(Master, slaves);
        Assert.True(aligned[0].HasFrame);
        Assert.False(aligned[1].HasFrame);
        Assert.False(aligned[1].Resync); // não adianta re-seek: não há frame
    }

    [Fact]
    public void WorstDrift_zero_when_no_channel_has_frame()
    {
        var slaves = new[] { new SyncClock.SlaveState(1, StartedAt(10, 0, 0), 60, 10) };
        Assert.Equal(0, SyncClock.WorstDriftMs(Master, slaves));
    }
}
