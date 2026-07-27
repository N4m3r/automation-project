using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Modules.Security;

/// <summary>
/// Endurecimento transversal: proteção de segredos, limite de tentativas,
/// cabeçalhos de resposta e supressão de detalhes internos em erros.
/// </summary>
public static class Hardening
{
    /// <summary>Nome da política aplicada ao endpoint de login.</summary>
    public const string LoginRateLimit = "login";

    public static IServiceCollection AddHardening(
        this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        // Chaves de criptografia em disco: sobrevivem a reinicio.
        // Em multi-no este diretorio DEVE ser o mesmo volume em todos os processos
        // (Security:KeyRingPath + ApplicationName fixo abaixo).
        var keyRing = config["Security:KeyRingPath"] ?? "./data/keys";
        if (!Path.IsPathRooted(keyRing))
            keyRing = Path.GetFullPath(Path.Combine(env.ContentRootPath, keyRing));
        Directory.CreateDirectory(keyRing);

        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keyRing))
            .SetApplicationName("SecurityPlatform");

        services.AddSingleton<ISecretProtector, SecretProtector>();

        // Limite por IP no login: a trava por conta sozinha não impede alguém
        // varrer muitos usuários a partir do mesmo endereço.
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            o.AddPolicy(LoginRateLimit, ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });

        return services;
    }

    /// <summary>
    /// Recusa subir com configuração insegura. Falhar no boot é melhor do que
    /// rodar semanas com a chave de exemplo assinando os tokens.
    /// </summary>
    public static void ValidateSecurityConfiguration(this WebApplication app)
    {
        var opt = app.Services.GetRequiredService<IOptions<SecurityOptions>>().Value;
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Security");

        const string exemplo = "troque-esta-chave-por-uma-aleatoria-de-no-minimo-32-caracteres";
        var vazia = string.IsNullOrWhiteSpace(opt.JwtKey);
        var padrao = opt.JwtKey == exemplo;
        var curta = !vazia && opt.JwtKey.Length < 32;

        if (!vazia && !padrao && !curta)
        {
            log.LogInformation(
                "JWT configurado ({Len} chars). Prefira Security__JwtKey no ambiente em produção.",
                opt.JwtKey.Length);
            return;
        }

        var motivo = vazia ? "Security:JwtKey vazia (defina Security__JwtKey no ambiente)"
                   : padrao ? "Security:JwtKey ainda e a chave de exemplo"
                            : "Security:JwtKey tem menos de 32 caracteres";

        if (app.Environment.IsDevelopment())
        {
            log.LogWarning("{Motivo}. Aceito apenas em desenvolvimento — use appsettings.Development.json.", motivo);
            if (vazia)
                throw new InvalidOperationException(
                    $"{motivo}. Em Development, preencha Security:JwtKey em appsettings.Development.json.");
            return;
        }

        throw new InvalidOperationException(
            $"{motivo}. Defina a variável de ambiente Security__JwtKey (aleatória, ≥32 chars) " +
            "ou um cofre — não grave a chave de produção no appsettings.json.");
    }

    /// <summary>
    /// Regrava em formato cifrado os segredos que ficaram em claro de versões
    /// anteriores. Idempotente: rodar de novo não faz nada.
    /// </summary>
    public static async Task MigrateSecretsAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Security");

        // O conversor do EF ja decifra na leitura; basta marcar como alterado
        // para que a gravacao saia cifrada.
        var brutos = await db.Database
            .SqlQuery<int>($"SELECT Id FROM Devices WHERE Password <> '' AND Password NOT LIKE 'enc:v1:%'")
            .ToListAsync();

        if (brutos.Count == 0) return;

        foreach (var device in await db.Devices.Where(d => brutos.Contains(d.Id)).ToListAsync())
            db.Entry(device).Property(d => d.Password).IsModified = true;

        await db.SaveChangesAsync();
        log.LogInformation("Credenciais de {Count} dispositivo(s) migradas para armazenamento cifrado.",
            brutos.Count);
    }

    /// <summary>
    /// Cabeçalhos de resposta e supressão do banner do servidor.
    /// A UI é servida pela própria API, então a CSP pode ser restritiva.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(
        this IApplicationBuilder app, string mediaPublicHost)
    {
        // O player busca HLS/WebRTC no no de midia, que roda em outra porta —
        // precisa entrar explicitamente na CSP.
        var midia = SanitizeOrigin(mediaPublicHost);
        // hls.js e servido de /lib (self) — CDN opcional so no connect/script se alguem
        // ainda apontar para jsdelivr. frame-ancestors 'self' permite admin no iframe.
        var midiaPart = string.IsNullOrWhiteSpace(midia) ? "" : " " + midia;
        var csp = string.Join("; ",
            "default-src 'self'",
            "script-src 'self' 'unsafe-inline'",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data: blob:",
            "font-src 'self' data:",
            "media-src 'self' blob:" + midiaPart,
            "connect-src 'self' ws: wss: http: https:" + midiaPart,
            "worker-src 'self' blob:",
            "frame-src 'self'",
            "object-src 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "frame-ancestors 'self'");

        return app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "SAMEORIGIN";
            h["Referrer-Policy"] = "no-referrer";
            h["Content-Security-Policy"] = csp;
            // microphone=() bloqueava talk-back no monitor; libera self.
            h["Permissions-Policy"] = "camera=(), microphone=(self), geolocation=(), payment=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["X-Permitted-Cross-Domain-Policies"] = "none";

            // Identificar o servidor so ajuda quem esta procurando alvo.
            h.Remove("Server");
            h.Remove("X-Powered-By");

            await next();
        });
    }

    /// <summary>
    /// Converte erro não tratado em resposta genérica. O detalhe fica no log do
    /// servidor; o cliente recebe só um identificador para correlacionar.
    /// </summary>
    public static IApplicationBuilder UseGenericErrors(this IApplicationBuilder app)
        => app.UseExceptionHandler(branch => branch.Run(async ctx =>
        {
            var feature = ctx.Features.Get<IExceptionHandlerFeature>();
            var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("UnhandledException");

            var id = ctx.TraceIdentifier;
            log.LogError(feature?.Error, "Falha nao tratada em {Method} {Path} (trace {Trace})",
                ctx.Request.Method, ctx.Request.Path, id);

            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "Erro interno. Consulte o log do servidor com o identificador informado.",
                trace = id
            });
        }));

    /// <summary>Mantém só esquema://host:porta — o resto não pertence a uma CSP.</summary>
    private static string SanitizeOrigin(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}:*"
            : "";

    /// <summary>Aceita IP exato ou CIDR. Compartilhado com a faixa por usuário.</summary>
    public static bool IpMatches(string ranges, string ip) => AuthService.IpAllowed(ranges, ip);
}
