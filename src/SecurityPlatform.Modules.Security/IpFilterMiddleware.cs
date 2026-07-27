using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Modules.Security;

/// <summary>
/// Aplica os filtros de IP cadastrados em Configurações → Filtros de IP.
///
/// Regras:
/// - qualquer entrada <c>Deny</c> que case bloqueia, sempre;
/// - havendo ao menos uma entrada <c>Allow</c>, só os IPs listados entram;
/// - sem nenhuma regra ativa, tudo passa.
///
/// A lista é lida do banco com cache curto: uma consulta por requisição
/// inviabilizaria o throughput de um servidor de vídeo.
/// </summary>
public class IpFilterMiddleware(
    RequestDelegate next,
    IMemoryCache cache,
    ILogger<IpFilterMiddleware> log)
{
    private const string CacheKey = "ip-filters";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(20);

    public async Task InvokeAsync(HttpContext ctx, IServiceScopeFactory scopes)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
        var regras = await CarregarAsync(scopes);

        if (regras.Count > 0 && !IpRules.Allowed(regras, ip))
        {
            log.LogWarning("Requisicao bloqueada por filtro de IP: {Ip} {Path}", ip, ctx.Request.Path);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "Acesso nao permitido a partir deste endereco." });
            return;
        }

        await next(ctx);
    }

    /// <summary>
    /// O laço local nunca é bloqueado. Sem essa saída, uma única regra Allow
    /// mal digitada trancaria o servidor para sempre — inclusive o painel que
    /// serviria para desfazer a regra.
    /// </summary>
    public static bool IsLoopback(string ip)
        => IPAddress.TryParse(ip, out var addr) && IPAddress.IsLoopback(addr);

    public static bool Permitido(List<IpFilter> regras, string ip)
    {
        if (IsLoopback(ip)) return true;

        if (regras.Any(r => r.Mode == IpFilterMode.Deny && AuthService.IpAllowed(r.Address, ip)))
            return false;

        var permitidos = regras.Where(r => r.Mode == IpFilterMode.Allow).ToList();
        if (permitidos.Count == 0) return true;          // só havia regras Deny

        return permitidos.Any(r => AuthService.IpAllowed(r.Address, ip));
    }

    private async Task<List<IpFilter>> CarregarAsync(IServiceScopeFactory scopes)
    {
        if (cache.TryGetValue(CacheKey, out List<IpFilter>? cacheado) && cacheado is not null)
            return cacheado;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var regras = await db.IpFilters.Where(f => f.Enabled).AsNoTracking().ToListAsync();
        cache.Set(CacheKey, regras, Ttl);
        return regras;
    }
}
