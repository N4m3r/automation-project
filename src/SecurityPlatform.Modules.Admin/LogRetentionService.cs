using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;

namespace SecurityPlatform.Modules.Admin;

/// <summary>
/// Aplica a política de retenção de logs definida em <see cref="SystemSettings"/>:
/// apaga eventos, auditoria e (quando houver) registros vencidos.
/// Roda a cada 6 horas — o mesmo ritmo de um purge LGPD de metadados.
/// </summary>
public class LogRetentionService(
    IServiceScopeFactory scopes,
    ILogger<LogRetentionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Aguarda o boot e o seed antes da primeira passada.
        await Task.Delay(TimeSpan.FromMinutes(2), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PurgeAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Falha ao aplicar retenção de logs");
            }

            await Task.Delay(TimeSpan.FromHours(6), ct);
        }
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var s = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct)
                ?? new SystemSettings();

        var agora = DateTime.UtcNow;
        var corteEventos = agora.AddDays(-Math.Max(s.EventLogRetentionDays, 1));
        var corteAudit = agora.AddDays(-Math.Max(s.AuditRetentionDays, 1));
        // systemLogRetentionDays cobre o log de atividade já espelhado na auditoria;
        // não há tabela separada de "system log" — reutilizamos o prazo mais curto
        // como teto extra sobre eventos de severidade informativa.
        var corteInfo = agora.AddDays(-Math.Max(s.SystemLogRetentionDays, 1));

        var evRem = await db.Events
            .Where(e => e.CreatedAt < corteEventos
                     || (e.Severity <= 1 && e.CreatedAt < corteInfo && e.Acknowledged))
            .ExecuteDeleteAsync(ct);

        var auRem = await db.AuditLogs
            .Where(a => a.CreatedAt < corteAudit)
            .ExecuteDeleteAsync(ct);

        if (evRem > 0 || auRem > 0)
        {
            log.LogInformation(
                "Retenção de logs: {Eventos} eventos e {Audit} registros de auditoria removidos " +
                "(eventos>{EvDias}d, audit>{AuDias}d)",
                evRem, auRem, s.EventLogRetentionDays, s.AuditRetentionDays);
        }
    }
}
