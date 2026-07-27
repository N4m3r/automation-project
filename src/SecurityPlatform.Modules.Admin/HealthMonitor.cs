using System.Diagnostics;
using Microsoft.Extensions.Options;
using SecurityPlatform.Modules.Vms;

namespace SecurityPlatform.Modules.Admin;

/// <summary>
/// Saúde do servidor de gravação: CPU, RAM, disco e uptime.
///
/// O disco é a métrica que mais derruba VMS em produção — por isso vem com
/// projeção de quantos dias restam no ritmo atual de gravação.
/// </summary>
public class HealthMonitor(IOptions<VmsOptions> vms)
{
    private static readonly Process Self = Process.GetCurrentProcess();
    private static DateTime _lastSample = DateTime.UtcNow;
    private static TimeSpan _lastCpu = Self.TotalProcessorTime;
    private static readonly object Gate = new();

    public ServerHealth Read()
    {
        var storage = Path.GetFullPath(vms.Value.StoragePath);
        Directory.CreateDirectory(storage);

        var drive = new DriveInfo(Path.GetPathRoot(storage) ?? "/");
        var used = drive.TotalSize - drive.AvailableFreeSpace;

        var recordingBytes = DirectorySize(storage);

        return new ServerHealth(
            MachineName: Environment.MachineName,
            UtcNow: DateTime.UtcNow,
            UptimeSeconds: (long)(DateTime.UtcNow - Self.StartTime.ToUniversalTime()).TotalSeconds,
            CpuPercent: SampleCpu(),
            ProcessMemoryMb: Self.WorkingSet64 / 1024 / 1024,
            Threads: Self.Threads.Count,
            DiskTotalGb: Math.Round(drive.TotalSize / 1024d / 1024 / 1024, 1),
            DiskUsedGb: Math.Round(used / 1024d / 1024 / 1024, 1),
            DiskFreeGb: Math.Round(drive.AvailableFreeSpace / 1024d / 1024 / 1024, 1),
            DiskUsedPercent: Math.Round(used * 100d / drive.TotalSize, 1),
            RecordingsGb: Math.Round(recordingBytes / 1024d / 1024 / 1024, 2),
            StoragePath: storage);
    }

    /// <summary>CPU deste processo entre duas leituras (não a CPU da máquina).</summary>
    private static double SampleCpu()
    {
        lock (Gate)
        {
            Self.Refresh();
            var now = DateTime.UtcNow;
            var cpu = Self.TotalProcessorTime;

            var elapsed = (now - _lastSample).TotalMilliseconds;
            if (elapsed < 500) return 0;                 // amostra curta demais

            var usedMs = (cpu - _lastCpu).TotalMilliseconds;
            _lastSample = now;
            _lastCpu = cpu;

            return Math.Round(usedMs / (Environment.ProcessorCount * elapsed) * 100, 1);
        }
    }

    private static long DirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

public record ServerHealth(
    string MachineName,
    DateTime UtcNow,
    long UptimeSeconds,
    double CpuPercent,
    long ProcessMemoryMb,
    int Threads,
    double DiskTotalGb,
    double DiskUsedGb,
    double DiskFreeGb,
    double DiskUsedPercent,
    double RecordingsGb,
    string StoragePath);
