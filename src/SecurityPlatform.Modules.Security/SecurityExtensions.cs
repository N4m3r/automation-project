using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Modules.Security;

public static class SecurityExtensions
{
    public static IServiceCollection AddSecurityModule(this IServiceCollection services)
    {
        services.AddScoped<PasswordHasher>();
        services.AddScoped<PermissionService>();
        services.AddScoped<AuditService>();
        services.AddScoped<AuthService>();
        services.AddScoped<LdapAuthService>();
        services.AddScoped<OidcAuthService>();
        services.AddScoped<SamlAuthService>();
        services.AddSingleton<LicenseSigner>();
        services.AddSingleton<RuntimeSecurityWriter>();
        services.AddSingleton<PlatformMetrics>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configurado via DI (nao via BuildServiceProvider) para respeitar o ciclo de vida.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<SecurityOptions>>((jwt, sec) =>
            {
                var opt = sec.Value;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = opt.JwtIssuer,
                    ValidAudience = opt.JwtAudience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opt.JwtKey)),
                    ClockSkew = TimeSpan.FromSeconds(30)
                };

                // WebSocket e <video src> nao enviam Authorization — token na query.
                // /ws/* (eventos) e /api/vms/recordings/*/file (playback progressivo com Range).
                jwt.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var path = ctx.Request.Path;
                        var precisaQuery = path.StartsWithSegments("/ws")
                            || (path.StartsWithSegments("/api/vms/recordings")
                                && path.Value?.Contains("/file", StringComparison.OrdinalIgnoreCase) == true);

                        if (precisaQuery)
                        {
                            var q = ctx.Request.Query["access_token"].FirstOrDefault()
                                 ?? ctx.Request.Query["token"].FirstOrDefault()
                                 ?? ctx.Request.Query["jwt"].FirstOrDefault();
                            if (!string.IsNullOrEmpty(q))
                                ctx.Token = q;
                        }
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization(o =>
            o.AddPolicy("admin", p => p.RequireRole("admin")));

        return services;
    }

    public static string ClientIp(this HttpContext ctx)
        => ctx.Connection.RemoteIpAddress?.ToString() ?? "";

    /// <summary>
    /// Cria o administrador inicial no primeiro boot. A senha vem da
    /// configuracao ou e gerada aleatoriamente e exibida uma unica vez no log,
    /// sempre com troca obrigatoria no primeiro acesso.
    /// </summary>
    public static async Task SeedSecurityAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<PlatformDbContext>();
        var hasher = sp.GetRequiredService<PasswordHasher>();
        var opt = sp.GetRequiredService<IOptions<SecurityOptions>>().Value;
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Security");

        // Perfis de fabrica: existem antes de qualquer usuario e sao reaplicados
        // em instalacoes ja rodando (por isso ficam fora do "primeiro boot").
        await SeedRolesAsync(db);

        if (await db.Users.AnyAsync()) return;

        var generated = string.IsNullOrWhiteSpace(opt.BootstrapAdminPassword);
        var password = generated ? PasswordHasher.GenerateStrong() : opt.BootstrapAdminPassword;

        db.Users.Add(new User
        {
            Username = "admin",
            FullName = "Administrador",
            PasswordHash = hasher.Hash(password),
            IsAdmin = true,
            MustChangePassword = true
        });

        // Grupo de exemplo apontando para o perfil de fabrica: uma linha de
        // direito, nao uma por permissao. Trocar o perfil do grupo muda o acesso.
        var operador = await db.Roles.FirstAsync(r => r.Name == "Operador");
        var group = new UserGroup
        {
            Name = "Operadores",
            Description = "Monitoramento e playback",
            RoleId = operador.Id
        };
        db.UserGroups.Add(group);
        await db.SaveChangesAsync();

        db.ObjectRights.Add(new ObjectRight
        {
            SubjectType = SubjectType.Group,
            SubjectId = group.Id,
            ObjectType = ObjectTypes.Camera,
            ObjectId = null,                     // todas as cameras
            Permission = Permissions.FromRole,   // as permissoes do perfil
            Effect = RightEffect.Allow
        });
        await db.SaveChangesAsync();

        if (generated)
        {
            log.LogWarning("=================================================================");
            log.LogWarning(" Usuario inicial: admin");
            log.LogWarning(" Senha temporaria: {Password}", password);
            log.LogWarning(" Troca obrigatoria no primeiro acesso. Esta senha nao sera exibida novamente.");
            log.LogWarning("=================================================================");
        }
        else
        {
            log.LogInformation("Usuario 'admin' criado com a senha definida em Security:BootstrapAdminPassword.");
        }
    }

    /// <summary>
    /// Quatro perfis prontos cobrem o uso normal de um CFTV. Criar um grupo
    /// passa a ser "nome + perfil + quais grupos de camera" em vez de marcar
    /// nove permissoes vezes N cameras.
    ///
    /// Idempotente: perfil de fabrica que ja existe tem apenas as permissoes
    /// reconciliadas, entao uma atualizacao da plataforma pode acrescentar uma
    /// permissao nova sem quebrar quem ja esta usando.
    /// </summary>
    private static async Task SeedRolesAsync(PlatformDbContext db)
    {
        var padrao = new (string Nome, string Descricao, string[] Permissoes)[]
        {
            ("Visualizador", "Somente ao vivo",
                [Permissions.CameraView]),

            ("Operador", "Ao vivo, playback, PTZ e tratamento de eventos",
                [Permissions.CameraView, Permissions.CameraPlayback,
                 Permissions.CameraPtz, Permissions.EventAck]),

            ("Supervisor", "Tudo do operador, mais exportacao e auditoria",
                [Permissions.CameraView, Permissions.CameraPlayback,
                 Permissions.CameraPtz, Permissions.EventAck,
                 Permissions.CameraExport, Permissions.AuditView]),

            ("Administrador", "Acesso completo, incluindo configuracao",
                [.. Permissions.All])
        };

        foreach (var (nome, descricao, permissoes) in padrao)
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == nome);
            if (role is null)
            {
                role = new Role { Name = nome, Description = descricao, BuiltIn = true };
                db.Roles.Add(role);
                await db.SaveChangesAsync();
            }

            var atuais = await db.RolePermissions
                .Where(p => p.RoleId == role.Id).Select(p => p.Permission).ToListAsync();

            foreach (var p in permissoes.Except(atuais))
                db.RolePermissions.Add(new RolePermission { RoleId = role.Id, Permission = p });
        }

        await db.SaveChangesAsync();
    }
}
