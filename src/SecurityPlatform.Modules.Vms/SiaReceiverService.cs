using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Events;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Receptora UDP mínima SIA-DC09 / Contact ID (texto).
/// Porta configurável em Vms:SiaUdpPort (padrão 9999).
/// Formatos aceitos (simplificados):
/// - SIA: "SIA-DCS" ... "|Nri1/BA001|" ...
/// - Contact ID: "181101001" style account+code+zone
/// - Livre: ACCOUNT|CODE|ZONE
/// </summary>
public sealed class SiaReceiverService(
    IServiceScopeFactory scopes,
    IEventBus bus,
    IOptions<VmsOptions> opt,
    ILogger<SiaReceiverService> log) : BackgroundService
{
    private readonly VmsOptions _opt = opt.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = _opt.SiaUdpPort > 0 ? _opt.SiaUdpPort : 9999;
        using var udp = new UdpClient(port);
        log.LogInformation("Receptora SIA UDP ouvindo em :{Port}", port);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(stoppingToken);
                var raw = Encoding.ASCII.GetString(result.Buffer).Trim();
                if (string.IsNullOrEmpty(raw)) continue;

                var (account, code, zone) = Parse(raw);
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var ev = await PersistAsync(db, account, code, zone, raw, tenantId: 1);
                await bus.PublishAsync(new DeviceEvent
                {
                    TenantId = ev.TenantId,
                    DeviceId = null,
                    Type = "alarm_" + (string.IsNullOrEmpty(code) ? "unknown" : code.ToLowerInvariant()),
                    Severity = ev.Severity,
                    Payload = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        source = "sia",
                        account = ev.Account,
                        code = ev.Code,
                        zone = ev.Zone,
                        alarmEventId = ev.Id
                    })
                }, stoppingToken);
                log.LogInformation("SIA conta={Account} code={Code} zone={Zone}", account, code, zone);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                log.LogWarning(e, "Falha ao processar datagrama SIA");
            }
        }
    }

    public static async Task<AlarmEvent> PersistAsync(
        PlatformDbContext db, string account, string code, string zone, string raw, int tenantId)
    {
        var panel = await db.AlarmPanels.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Account == account && p.Active);

        var severity = code.StartsWith("E", StringComparison.OrdinalIgnoreCase)
            || code is "BA" or "FA" or "PA" or "QA" or "TA" ? 3 : 2;

        var ev = new AlarmEvent
        {
            TenantId = tenantId,
            PanelId = panel?.Id,
            Account = account,
            Code = code,
            Zone = zone,
            Raw = raw.Length > 2000 ? raw[..2000] : raw,
            Severity = severity
        };
        db.AlarmEvents.Add(ev);
        await db.SaveChangesAsync();
        return ev;
    }

    internal static (string Account, string Code, string Zone) Parse(string raw)
    {
        // ACCOUNT|CODE|ZONE simples (ingest manual) — 3 tokens curtos sem lixo SIA.
        if (raw.Contains('|'))
        {
            var p = raw.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 3
                && p[0].Length is > 0 and <= 16
                && p[1].Length is > 0 and <= 8
                && p[2].Length <= 8
                && !p[0].Contains("SIA", StringComparison.OrdinalIgnoreCase)
                && !p[0].Contains('*'))
                return (p[0], p[1], p[2]);
            if (p.Length == 2 && p[0].Length <= 16 && p[1].Length <= 8
                && !p[0].Contains("SIA", StringComparison.OrdinalIgnoreCase))
                return (p[0], p[1], "");
        }

        // SIA-DC09 textual: ...|#acct|Nri1/BA001|...
        var sia = Regex.Match(raw, @"\|[A-Za-z]{0,3}(?:ri\d+/)?([A-Za-z]{2})(\d{1,4})\|",
            RegexOptions.IgnoreCase);
        if (sia.Success)
        {
            var acctM = Regex.Match(raw, @"#(\d{3,8})");
            if (!acctM.Success)
                acctM = Regex.Match(raw, @"\b(\d{3,8})\b");
            var acct = acctM.Success ? acctM.Groups[1].Value : "";
            return (acct, sia.Groups[1].Value.ToUpperInvariant(), sia.Groups[2].Value);
        }

        // Contact ID compact: AAA Q XYZ GG CCC  (simplificado)
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length >= 10)
            return (digits[..4], digits.Substring(4, 3), digits.Substring(7, 3));

        return ("", "UNK", "");
    }
}
