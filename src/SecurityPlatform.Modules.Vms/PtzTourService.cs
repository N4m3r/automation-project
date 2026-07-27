using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Patrulha PTZ em memória: cicla presets com dwell time até stop ou cancelamento.
/// </summary>
public sealed class PtzTourService(IServiceScopeFactory scopes, ILogger<PtzTourService> log)
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _tours = new();

    public bool IsRunning(int deviceId) => _tours.ContainsKey(deviceId);

    public bool Start(int deviceId, IReadOnlyList<string> presets, int dwellSeconds)
    {
        if (presets.Count == 0) return false;
        dwellSeconds = Math.Clamp(dwellSeconds, 2, 300);

        Stop(deviceId);
        var cts = new CancellationTokenSource();
        if (!_tours.TryAdd(deviceId, cts))
        {
            cts.Dispose();
            return false;
        }

        _ = RunAsync(deviceId, presets.ToList(), dwellSeconds, cts.Token);
        return true;
    }

    public bool Stop(int deviceId)
    {
        if (!_tours.TryRemove(deviceId, out var cts)) return false;
        try { cts.Cancel(); } catch { /* ignore */ }
        cts.Dispose();
        return true;
    }

    private async Task RunAsync(int deviceId, List<string> presets, int dwell, CancellationToken ct)
    {
        try
        {
            var i = 0;
            while (!ct.IsCancellationRequested)
            {
                var preset = presets[i % presets.Count];
                i++;

                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var registry = scope.ServiceProvider.GetRequiredService<DriverRegistry>();
                var cam = await db.Devices.FindAsync([deviceId], ct);
                if (cam is null || cam.Kind != DeviceKind.Camera) break;

                var result = await registry.Resolve(cam).CommandAsync(cam, "ptz_preset",
                    new Dictionary<string, string> { ["preset"] = preset }, ct);
                if (!result.Ok)
                    log.LogWarning("Tour PTZ câmera {Id} preset {P}: {Err}", deviceId, preset, result.Error);

                await Task.Delay(TimeSpan.FromSeconds(dwell), ct);
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (Exception e)
        {
            log.LogError(e, "Tour PTZ da câmera {Id} encerrou com erro", deviceId);
        }
        finally
        {
            _tours.TryRemove(deviceId, out _);
        }
    }
}
