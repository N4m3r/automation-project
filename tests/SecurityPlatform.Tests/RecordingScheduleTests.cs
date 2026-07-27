using SecurityPlatform.Core.Domain;
using SecurityPlatform.Modules.Vms;

namespace SecurityPlatform.Tests;

/// <summary>
/// Agendamento de gravação: se a avaliação errar, a câmera grava fora da
/// janela contratada ou deixa de gravar no horário crítico.
/// </summary>
public class RecordingScheduleTests
{
    [Fact]
    public void Sem_faixas_grava_24x7()
    {
        var slots = Array.Empty<ScheduleSlot>();
        Assert.True(RecordingSchedule.IsActive(slots, ScheduleKind.Recording, DateTime.UtcNow));
    }

    [Fact]
    public void Dentro_da_faixa_libera()
    {
        var slots = new[]
        {
            new ScheduleSlot
            {
                Kind = ScheduleKind.Recording,
                Day = DayOfWeek.Monday,
                Start = TimeSpan.FromHours(8),
                End = TimeSpan.FromHours(18),
                Enabled = true
            }
        };

        // Segunda-feira 10:00 UTC
        var when = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc); // Monday
        Assert.Equal(DayOfWeek.Monday, when.DayOfWeek);
        Assert.True(RecordingSchedule.IsActive(slots, ScheduleKind.Recording, when));
    }

    [Fact]
    public void Fora_da_faixa_bloqueia()
    {
        var slots = new[]
        {
            new ScheduleSlot
            {
                Kind = ScheduleKind.Recording,
                Day = DayOfWeek.Monday,
                Start = TimeSpan.FromHours(8),
                End = TimeSpan.FromHours(18),
                Enabled = true
            }
        };

        var when = new DateTime(2026, 7, 20, 20, 0, 0, DateTimeKind.Utc); // Monday 20h
        Assert.False(RecordingSchedule.IsActive(slots, ScheduleKind.Recording, when));
    }

    [Fact]
    public void Day_nulo_vale_todos_os_dias()
    {
        var slots = new[]
        {
            new ScheduleSlot
            {
                Kind = ScheduleKind.Recording,
                Day = null,
                Start = TimeSpan.FromHours(0),
                End = TimeSpan.FromHours(24),
                Enabled = true
            }
        };

        Assert.True(RecordingSchedule.IsActive(
            slots, ScheduleKind.Recording, new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Faixa_que_cruza_meia_noite()
    {
        var slots = new[]
        {
            new ScheduleSlot
            {
                Kind = ScheduleKind.Recording,
                Day = null,
                Start = TimeSpan.FromHours(22),
                End = TimeSpan.FromHours(6),
                Enabled = true
            }
        };

        Assert.True(RecordingSchedule.IsActive(
            slots, ScheduleKind.Recording, new DateTime(2026, 7, 20, 23, 0, 0, DateTimeKind.Utc)));
        Assert.True(RecordingSchedule.IsActive(
            slots, ScheduleKind.Recording, new DateTime(2026, 7, 20, 3, 0, 0, DateTimeKind.Utc)));
        Assert.False(RecordingSchedule.IsActive(
            slots, ScheduleKind.Recording, new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Faixa_desabilitada_e_ignorada()
    {
        var slots = new[]
        {
            new ScheduleSlot
            {
                Kind = ScheduleKind.Recording,
                Day = null,
                Start = TimeSpan.Zero,
                End = TimeSpan.FromHours(24),
                Enabled = false
            }
        };

        // Todas desabilitadas = equivalente a sem faixas = 24×7
        Assert.True(RecordingSchedule.IsActive(slots, ScheduleKind.Recording, DateTime.UtcNow));
    }

    [Fact]
    public void Kind_diferente_nao_conta()
    {
        var slots = new[]
        {
            new ScheduleSlot
            {
                Kind = ScheduleKind.Event,
                Day = null,
                Start = TimeSpan.FromHours(8),
                End = TimeSpan.FromHours(18),
                Enabled = true
            }
        };

        // Pediu Recording; só tem Event → sem faixas do kind → 24×7 para Recording
        Assert.True(RecordingSchedule.IsActive(
            slots, ScheduleKind.Recording, new DateTime(2026, 7, 20, 20, 0, 0, DateTimeKind.Utc)));
    }
}

public class StreamUrlBuilderTests
{
    [Fact]
    public void Reescreve_canal_hikvision()
    {
        var url = "rtsp://admin:x@192.168.1.10:554/Streaming/Channels/101";
        Assert.Equal(
            "rtsp://admin:x@192.168.1.10:554/Streaming/Channels/102",
            StreamUrlBuilder.ApplyChannel(url, 102));
    }

    [Fact]
    public void Reescreve_subtype_dahua()
    {
        var url = "rtsp://admin:x@192.168.1.10:554/cam/realmonitor?channel=1&subtype=0";
        Assert.Equal(
            "rtsp://admin:x@192.168.1.10:554/cam/realmonitor?channel=1&subtype=1",
            StreamUrlBuilder.ApplyChannel(url, 102));
    }

    [Fact]
    public void Canal_nulo_nao_altera()
    {
        var url = "rtsp://host/path";
        Assert.Equal(url, StreamUrlBuilder.ApplyChannel(url, null));
    }
}

public class WatermarkEscapeTests
{
    [Fact]
    public void Escapa_caracteres_do_drawtext()
    {
        var raw = "Servidor:A | user%1";
        var escaped = RecordingExporter.EscapeDrawText(raw);
        Assert.Contains("\\:", escaped);
        Assert.Contains("%%", escaped);
        Assert.DoesNotContain("Servidor:A", escaped); // dois-pontos escapados
    }
}
