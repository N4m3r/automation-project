using SecurityPlatform.Core.Domain;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Avalia se a câmera deve gravar no instante atual, segundo as faixas
/// <see cref="ScheduleSlot"/> cadastradas.
///
/// Regras (estilo Digifort):
/// <list type="bullet">
/// <item>Sem faixas habilitadas do tipo pedido → grava 24×7 (comportamento legado).</item>
/// <item>Com faixas → grava apenas se o instante cair em pelo menos uma.</item>
/// <item><c>Day</c> nulo vale para todos os dias da semana.</item>
/// <item>Faixa que cruza meia-noite (Start &gt; End) é suportada.</item>
/// </list>
/// </summary>
public static class RecordingSchedule
{
    /// <summary>
    /// Indica se, no instante <paramref name="when"/> (UTC), a gravação do
    /// <paramref name="kind"/> está liberada pelas faixas informadas.
    /// </summary>
    public static bool IsActive(
        IEnumerable<ScheduleSlot> slots,
        ScheduleKind kind,
        DateTime when,
        TimeZoneInfo? timeZone = null)
    {
        var aplicaveis = slots
            .Where(s => s.Enabled && s.Kind == kind)
            .ToList();

        // Sem agenda = 24×7. Com agenda, só grava dentro das faixas.
        if (aplicaveis.Count == 0) return true;

        var local = timeZone is null
            ? when
            : TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(when, DateTimeKind.Utc), timeZone);

        var dia = local.DayOfWeek;
        var hora = local.TimeOfDay;

        return aplicaveis.Any(s => Matches(s, dia, hora));
    }

    internal static bool Matches(ScheduleSlot slot, DayOfWeek day, TimeSpan time)
    {
        if (slot.Day is DayOfWeek d && d != day) return false;

        // Faixa normal: 08:00–18:00.
        if (slot.End > slot.Start)
            return time >= slot.Start && time < slot.End;

        // Cruza meia-noite: 22:00–06:00 → ativo se >= 22 ou < 06.
        if (slot.End < slot.Start)
            return time >= slot.Start || time < slot.End;

        // Start == End: janela de 24h daquele dia (ou de todos, se Day nulo).
        return true;
    }
}
