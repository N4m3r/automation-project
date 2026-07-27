using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// HA ativo/passivo do gravador via lease no banco (sem Redis).
/// Cada câmera tem <c>cam:{id}</c>; renovação a cada LeaseSeconds/3.
/// </summary>
public class RecorderLeaseService(
    IServiceScopeFactory scopes,
    IOptions<VmsOptions> options,
    ILogger<RecorderLeaseService> log) : BackgroundService
{
    private readonly VmsOptions _opt = options.Value;
    private readonly string _node = options.Value.ResolveNodeId();

    public string NodeId => _node;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.HaEnabled)
        {
            log.LogInformation("HA do gravador desligado (Vms:HaEnabled=false).");
            return;
        }

        log.LogInformation("HA do gravador ativo — nó {Node}", _node);
        while (!ct.IsCancellationRequested)
        {
            try { await RenewOwnedAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Falha ao renovar leases do gravador");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _opt.LeaseSeconds / 3)), ct);
        }
    }

    /// <summary>
    /// Tenta adquirir ou renovar o lease da câmera. True = este nó pode gravar.
    /// Com HA desligado, sempre true (respeitando só o sharding do chamador).
    /// </summary>
    public async Task<bool> TryAcquireAsync(int deviceId, CancellationToken ct = default)
    {
        if (!_opt.HaEnabled) return true;
        if (!_opt.OwnsDevice(deviceId)) return false;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var key = Key(deviceId);
        var agora = DateTime.UtcNow;
        var exp = agora.AddSeconds(Math.Max(_opt.LeaseSeconds, 10));

        var lease = await db.RecorderLeases.FirstOrDefaultAsync(l => l.ResourceKey == key, ct);
        if (lease is null)
        {
            db.RecorderLeases.Add(new RecorderLease
            {
                ResourceKey = key,
                NodeId = _node,
                ExpiresAt = exp,
                UpdatedAt = agora
            });
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateException)
            {
                // Outro nó inseriu no mesmo instante.
                return false;
            }
        }

        if (lease.NodeId == _node || lease.ExpiresAt < agora)
        {
            lease.NodeId = _node;
            lease.ExpiresAt = exp;
            lease.UpdatedAt = agora;
            await db.SaveChangesAsync(ct);
            return true;
        }

        return false;
    }

    private async Task RenewOwnedAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var agora = DateTime.UtcNow;
        var exp = agora.AddSeconds(Math.Max(_opt.LeaseSeconds, 10));

        var mine = await db.RecorderLeases
            .Where(l => l.NodeId == _node)
            .ToListAsync(ct);

        foreach (var l in mine)
        {
            l.ExpiresAt = exp;
            l.UpdatedAt = agora;
        }
        if (mine.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    public static string Key(int deviceId) => $"cam:{deviceId}";
}
