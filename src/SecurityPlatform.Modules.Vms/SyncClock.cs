namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Relógio mestre para playback multi-câmera sincronizado (posto de operação).
///
/// <para>
/// Contrato de tempo (idêntico ao cliente <c>wwwroot/js/sync-clock.js</c>):
/// o instante absoluto UTC reproduzido por um player é
/// <c>startedAtMs + currentTime*1000</c>, onde <c>startedAt</c> é o início do
/// segmento carregado naquele player. Para sincronizar N câmeras num mesmo
/// instante mestre <c>masterAbsMs</c>, cada player recebe
/// <c>currentTime = (masterAbsMs - startedAtMs) / 1000</c> (clampeado à duração).
/// </para>
///
/// Esta classe é a fonte de verdade do cálculo — testada de forma determinística
/// para o critério de aceite §8 #7 (4 canais sync ±200 ms). Não faz I/O.
/// </summary>
public static class SyncClock
{
    /// <summary>Tolerância padrão de deriva antes de forçar re-seek (critério §8 #7).</summary>
    public const double DefaultToleranceMs = 200;

    /// <summary>
    /// Posição (segundos) que o player de um slave deve assumir para exibir o
    /// instante absoluto <paramref name="masterAbsMs"/>, dado o início do seu
    /// segmento e a duração carregada. Clampeada a [0, duração - epsilon].
    /// Retorna <c>null</c> se o instante mestre está fora do segmento do slave
    /// (antes do início ou depois do fim) — o slave não tem imagem para aquele t.
    /// </summary>
    public static double? SlaveTargetSeconds(
        double masterAbsMs, double slaveStartedAtMs, double durationSec, double epsilonSec = 0.05)
    {
        if (!IsFinite(masterAbsMs) || !IsFinite(slaveStartedAtMs) || !IsFinite(durationSec) || durationSec <= 0)
            return null;

        var offset = (masterAbsMs - slaveStartedAtMs) / 1000.0;
        if (offset < -epsilonSec) return null;                 // instante antes do segmento
        if (offset > durationSec + epsilonSec) return null;    // instante depois do segmento

        return Math.Clamp(offset, 0, Math.Max(0, durationSec - epsilonSec));
    }

    /// <summary>Instante absoluto UTC (ms) atualmente exibido por um player.</summary>
    public static double SlaveAbsMs(double slaveStartedAtMs, double currentTimeSec)
        => slaveStartedAtMs + currentTimeSec * 1000.0;

    /// <summary>
    /// Deriva (ms) entre o instante exibido por um slave e o instante mestre.
    /// Sempre não-negativa.
    /// </summary>
    public static double DriftMs(double masterAbsMs, double slaveStartedAtMs, double currentTimeSec)
        => Math.Abs(SlaveAbsMs(slaveStartedAtMs, currentTimeSec) - masterAbsMs);

    /// <summary>Precisa re-seek? (deriva acima da tolerância).</summary>
    public static bool NeedsResync(double driftMs, double toleranceMs = DefaultToleranceMs)
        => driftMs > toleranceMs;

    /// <summary>Estado de um player auxiliar para avaliação de sincronismo.</summary>
    public readonly record struct SlaveState(int CameraId, double StartedAtMs, double DurationSec, double CurrentTimeSec);

    /// <summary>Decisão de alinhamento para um slave.</summary>
    public readonly record struct Alignment(int CameraId, bool HasFrame, double DriftMs, bool Resync, double? TargetSeconds);

    /// <summary>
    /// Avalia todos os slaves contra o instante mestre e devolve, para cada um,
    /// a deriva atual, se está fora de imagem e o seek necessário para realinhar.
    /// </summary>
    public static IReadOnlyList<Alignment> AlignAll(
        double masterAbsMs, IEnumerable<SlaveState> slaves, double toleranceMs = DefaultToleranceMs)
    {
        var result = new List<Alignment>();
        foreach (var s in slaves)
        {
            var target = SlaveTargetSeconds(masterAbsMs, s.StartedAtMs, s.DurationSec);
            if (target is null)
            {
                result.Add(new Alignment(s.CameraId, HasFrame: false, DriftMs: double.PositiveInfinity, Resync: false, TargetSeconds: null));
                continue;
            }
            var drift = DriftMs(masterAbsMs, s.StartedAtMs, s.CurrentTimeSec);
            result.Add(new Alignment(s.CameraId, HasFrame: true, DriftMs: drift, Resync: NeedsResync(drift, toleranceMs), TargetSeconds: target));
        }
        return result;
    }

    /// <summary>
    /// Pior deriva entre os slaves que têm imagem no instante mestre. Retorna 0
    /// se nenhum slave tem imagem. É a métrica avaliada no critério §8 #7.
    /// </summary>
    public static double WorstDriftMs(double masterAbsMs, IEnumerable<SlaveState> slaves)
    {
        double worst = 0;
        foreach (var a in AlignAll(masterAbsMs, slaves))
            if (a.HasFrame && a.DriftMs > worst) worst = a.DriftMs;
        return worst;
    }

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
