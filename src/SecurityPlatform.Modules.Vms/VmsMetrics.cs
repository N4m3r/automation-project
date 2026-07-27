using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Métricas Prometheus do módulo VMS (gauges + counters).
/// Expõe via <see cref="RenderPrometheus"/> (anexado em /metrics).
/// </summary>
public sealed class VmsMetrics
{
    private long _exportsTotal;
    private long _exportDurationMs;
    private long _segmentsIndexed;
    private long _segmentBytes;
    private long _purgeTotal;
    private long _gapsDetected;
    private long _mediaGatewayDown;
    private long _mediaGatewayRecoveries;
    private long _thumbnailsGenerated;
    private long _archivesMoved;
    private long _preEventPromotions;

    private int _recordingActive;
    private int _camerasOffline;
    private int _camerasOnline;
    private int _mediaGatewayUp; // 1/0

    private readonly ConcurrentDictionary<string, long> _custom = new(StringComparer.Ordinal);

    public void SetRecordingActive(int n) => Interlocked.Exchange(ref _recordingActive, n);
    public void SetCamerasOnline(int n) => Interlocked.Exchange(ref _camerasOnline, n);
    public void SetCamerasOffline(int n) => Interlocked.Exchange(ref _camerasOffline, n);
    public void SetMediaGatewayUp(bool up) => Interlocked.Exchange(ref _mediaGatewayUp, up ? 1 : 0);

    public void IncExport(long durationMs)
    {
        Interlocked.Increment(ref _exportsTotal);
        Interlocked.Add(ref _exportDurationMs, Math.Max(0, durationMs));
    }

    public void IncSegment(long bytes)
    {
        Interlocked.Increment(ref _segmentsIndexed);
        Interlocked.Add(ref _segmentBytes, Math.Max(0, bytes));
    }

    public void IncPurge() => Interlocked.Increment(ref _purgeTotal);
    public void IncGap() => Interlocked.Increment(ref _gapsDetected);
    public void IncMediaDown() => Interlocked.Increment(ref _mediaGatewayDown);
    public void IncMediaRecovery() => Interlocked.Increment(ref _mediaGatewayRecoveries);
    public void IncThumbnail() => Interlocked.Increment(ref _thumbnailsGenerated);
    public void IncArchive() => Interlocked.Increment(ref _archivesMoved);
    public void IncPreEventPromote() => Interlocked.Increment(ref _preEventPromotions);
    public void Inc(string name, long delta = 1) =>
        _custom.AddOrUpdate(name, delta, (_, v) => v + delta);

    public int RecordingActive => Volatile.Read(ref _recordingActive);
    public bool MediaGatewayUp => Volatile.Read(ref _mediaGatewayUp) == 1;

    public string RenderPrometheus()
    {
        var sb = new StringBuilder(2048);
        void C(string name, string help, long val)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" counter\n");
            sb.Append(name).Append(' ').Append(val).Append('\n');
        }
        void G(string name, string help, long val)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" gauge\n");
            sb.Append(name).Append(' ').Append(val).Append('\n');
        }

        G("vms_recording_active", "Câmeras com FFmpeg gravando neste nó", Volatile.Read(ref _recordingActive));
        G("vms_cameras_online", "Câmeras online (último health)", Volatile.Read(ref _camerasOnline));
        G("vms_cameras_offline", "Câmeras offline", Volatile.Read(ref _camerasOffline));
        G("vms_media_gateway_up", "MediaMTX alcançável (1/0)", Volatile.Read(ref _mediaGatewayUp));

        C("vms_exports_total", "Exports de gravação", Interlocked.Read(ref _exportsTotal));
        C("vms_export_duration_ms_total", "Soma de duração de exports (ms)", Interlocked.Read(ref _exportDurationMs));
        C("vms_segments_indexed_total", "Segmentos indexados", Interlocked.Read(ref _segmentsIndexed));
        C("vms_segment_bytes_total", "Bytes de segmentos indexados", Interlocked.Read(ref _segmentBytes));
        C("vms_purge_total", "Segmentos purgados pela retenção", Interlocked.Read(ref _purgeTotal));
        C("vms_recording_gaps_total", "Buracos de gravação detectados", Interlocked.Read(ref _gapsDetected));
        C("vms_media_gateway_down_total", "Transições MediaMTX down", Interlocked.Read(ref _mediaGatewayDown));
        C("vms_media_gateway_recoveries_total", "Recuperações MediaMTX", Interlocked.Read(ref _mediaGatewayRecoveries));
        C("vms_thumbnails_total", "Thumbnails gerados", Interlocked.Read(ref _thumbnailsGenerated));
        C("vms_archive_moved_total", "Segmentos movidos para archive", Interlocked.Read(ref _archivesMoved));
        C("vms_preevent_promotions_total", "Pré-buffer promovido a evento", Interlocked.Read(ref _preEventPromotions));

        foreach (var (k, v) in _custom.OrderBy(x => x.Key))
            C("vms_" + k.Replace('-', '_').Replace('.', '_'), "custom " + k, v);

        return sb.ToString();
    }
}
