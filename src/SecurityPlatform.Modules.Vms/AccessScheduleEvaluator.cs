using System.Globalization;
using SecurityPlatform.Core.Domain;

namespace SecurityPlatform.Modules.Vms;

/// <summary>Avalia se o instante atual cai na janela de um <see cref="AccessSchedule"/>.</summary>
public static class AccessScheduleEvaluator
{
    public static bool IsOpenNow(AccessSchedule? schedule, DateTime? utcNow = null)
    {
        if (schedule is null || !schedule.Active) return true;

        var nowUtc = utcNow ?? DateTime.UtcNow;
        var local = ToLocal(nowUtc, schedule.TimeZone);
        var dow = (int)local.DayOfWeek; // 0=dom … 6=sáb

        var days = ParseDays(schedule.DaysOfWeek);
        if (days.Count > 0 && !days.Contains(dow))
            return false;

        if (!TryParseHm(schedule.StartHm, out var start) || !TryParseHm(schedule.EndHm, out var end))
            return true; // horário inválido não tranca por engano

        var t = local.TimeOfDay;
        if (end <= start)
        {
            // Faixa noturna: 22:00 → 06:00
            return t >= start || t < end;
        }
        return t >= start && t < end;
    }

    public static HashSet<int> ParseDays(string? raw)
    {
        var set = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-'))
            {
                var ab = part.Split('-', 2);
                if (int.TryParse(ab[0], out var a) && int.TryParse(ab[1], out var b))
                {
                    for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                        if (i is >= 0 and <= 6) set.Add(i);
                }
            }
            else if (int.TryParse(part, out var d) && d is >= 0 and <= 6)
                set.Add(d);
        }
        return set;
    }

    public static bool TryParseHm(string? hm, out TimeSpan ts)
    {
        ts = default;
        if (string.IsNullOrWhiteSpace(hm)) return false;
        return TimeSpan.TryParseExact(hm.Trim(), new[] { @"h\:mm", @"hh\:mm" },
            CultureInfo.InvariantCulture, out ts);
    }

    public static DateTime ToLocal(DateTime utc, string? tzId)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(
                string.IsNullOrWhiteSpace(tzId) ? "America/Sao_Paulo" : tzId);
            return TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
        }
        catch (TimeZoneNotFoundException)
        {
            try
            {
                // Windows: "E. South America Standard Time"
                var tz = TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
            }
            catch
            {
                return utc.ToLocalTime();
            }
        }
        catch (InvalidTimeZoneException)
        {
            return utc.ToLocalTime();
        }
    }
}
