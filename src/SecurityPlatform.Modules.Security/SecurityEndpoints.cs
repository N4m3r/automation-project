using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Modules.Security;

public static class SecurityEndpoints
{
    public static IEndpointRouteBuilder MapSecurityModule(this IEndpointRouteBuilder app)
    {
        MapAuth(app);
        MapUsers(app);
        MapRoles(app);
        MapGroups(app);
        MapRights(app);
        MapAudit(app);
        return app;
    }

    // ---------------------------------------------------------------- auth
    private static void MapAuth(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Autenticacao");

        // Limite por IP alem da trava por conta: sem ele, um mesmo endereco
        // poderia varrer muitos usuarios sem nunca bloquear nenhuma conta.
        g.MapPost("/login", async (LoginRequest req, HttpContext ctx, AuthService auth) =>
        {
            var result = await auth.LoginAsync(req, ctx.ClientIp());
            return result.Ok ? Results.Ok(result) : Results.Json(result, statusCode: 401);
        }).AllowAnonymous().RequireRateLimiting(Hardening.LoginRateLimit);

        g.MapGet("/me", async (HttpContext ctx, PlatformDbContext db, PermissionService perms) =>
        {
            var user = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == ctx.User.UserId());
            if (user is null) return Results.Unauthorized();

            return Results.Ok(new
            {
                user.Id,
                user.Username,
                user.FullName,
                user.IsAdmin,
                user.TwoFactorEnabled,
                user.MustChangePassword,
                visibleCameras = await perms.VisibleCameraIdsAsync(user.Id)
            });
        }).RequireAuthorization();

        g.MapPost("/change-password", async (
            ChangePasswordInput input, HttpContext ctx, AuthService auth) =>
        {
            var error = await auth.ChangePasswordAsync(
                ctx.User.UserId(), input.CurrentPassword, input.NewPassword, ctx.ClientIp());
            return error is null ? Results.NoContent() : Results.BadRequest(new { error });
        }).RequireAuthorization();

        g.MapPost("/2fa/setup", async (HttpContext ctx, AuthService auth) =>
        {
            var (secret, uri) = await auth.SetupTwoFactorAsync(ctx.User.UserId());
            return Results.Ok(new { secret, uri });
        }).RequireAuthorization();

        g.MapPost("/2fa/confirm", async (TotpInput input, HttpContext ctx, AuthService auth) =>
            await auth.ConfirmTwoFactorAsync(ctx.User.UserId(), input.Code)
                ? Results.NoContent()
                : Results.BadRequest(new { error = "Codigo invalido." })
        ).RequireAuthorization();

        // ---- SSO OIDC ----
        g.MapGet("/oidc/status", (OidcAuthService oidc, SamlAuthService saml, Microsoft.Extensions.Options.IOptionsMonitor<SecurityOptions> opt) =>
            Results.Ok(new
            {
                oidc = new { enabled = oidc.Enabled, authority = opt.CurrentValue.Oidc.Authority },
                saml = new { enabled = saml.Enabled, idp = opt.CurrentValue.Saml.IdpEntityId },
                ldap = new
                {
                    enabled = opt.CurrentValue.Ldap.Enabled,
                    syncGroups = opt.CurrentValue.Ldap.SyncGroups,
                    host = opt.CurrentValue.Ldap.Host
                }
            })).AllowAnonymous();

        g.MapGet("/oidc/login", async (OidcAuthService oidc, HttpContext ctx) =>
        {
            var built = await oidc.BuildAuthorizeUrlAsync(ctx.RequestAborted);
            if (built is null)
                return Results.BadRequest(new { error = "OIDC não configurado (Security:Oidc)." });
            ctx.Response.Cookies.Append("oidc_state", built.Value.State, new CookieOptions
            {
                HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10)
            });
            return Results.Redirect(built.Value.Url);
        }).AllowAnonymous();

        g.MapGet("/oidc/callback", async (
            string? code, string? state, string? error, HttpContext ctx, OidcAuthService oidc) =>
        {
            if (!string.IsNullOrEmpty(error))
                return Results.BadRequest(new { error = "IdP: " + error });
            if (string.IsNullOrEmpty(code))
                return Results.BadRequest(new { error = "code ausente." });

            var cookieState = ctx.Request.Cookies["oidc_state"];
            if (!string.IsNullOrEmpty(cookieState) && cookieState != state)
                return Results.BadRequest(new { error = "state inválido." });

            var result = await oidc.HandleCallbackAsync(code, ctx.ClientIp(), ctx.RequestAborted);
            if (!result.Ok)
                return Results.Json(result, statusCode: 401);

            // HTML que grava o token e volta ao portal (SPA simples).
            var html = $"""
                <!doctype html><meta charset=utf-8><title>SSO</title>
                <script>
                  localStorage.setItem('token',{System.Text.Json.JsonSerializer.Serialize(result.Token)});
                  sessionStorage.setItem('token',{System.Text.Json.JsonSerializer.Serialize(result.Token)});
                  location.href='/monitor.html';
                </script>
                <p>Login OIDC ok. Redirecionando…</p>
                """;
            return Results.Content(html, "text/html; charset=utf-8");
        }).AllowAnonymous();

        // ---- SSO SAML ----
        g.MapGet("/saml/login", (SamlAuthService saml) =>
        {
            var url = saml.BuildRedirectUrl();
            return url is null
                ? Results.BadRequest(new { error = "SAML não configurado (Security:Saml)." })
                : Results.Redirect(url);
        }).AllowAnonymous();

        g.MapPost("/saml/acs", async (HttpContext ctx, SamlAuthService saml) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var resp = form["SAMLResponse"].ToString();
            var result = await saml.HandleAcsAsync(resp, ctx.ClientIp(), ctx.RequestAborted);
            if (!result.Ok)
                return Results.Json(result, statusCode: 401);
            var html = $"""
                <!doctype html><meta charset=utf-8><title>SSO SAML</title>
                <script>
                  localStorage.setItem('token',{System.Text.Json.JsonSerializer.Serialize(result.Token)});
                  sessionStorage.setItem('token',{System.Text.Json.JsonSerializer.Serialize(result.Token)});
                  location.href='/monitor.html';
                </script>
                <p>Login SAML ok. Redirecionando…</p>
                """;
            return Results.Content(html, "text/html; charset=utf-8");
        }).AllowAnonymous();
    }

    // --------------------------------------------------------------- users
    private static void MapUsers(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/users").WithTags("Usuarios").RequireAuthorization("admin");

        g.MapGet("/", async (PlatformDbContext db) =>
            await db.Users.AsNoTracking().Select(u => new
            {
                u.Id, u.Username, u.FullName, u.Email, u.IsAdmin, u.Active,
                u.TwoFactorEnabled, u.AllowedIpRanges, u.LastLoginAt, u.LockedUntil
            }).ToListAsync());

        g.MapPost("/", async (
            UserInput input, HttpContext ctx,
            PlatformDbContext db, PasswordHasher hasher, AuditService audit) =>
        {
            if (await db.Users.AnyAsync(u => u.Username == input.Username))
                return Results.Conflict(new { error = "Usuario ja existe." });

            if (hasher.Validate(input.Password) is { } problem)
                return Results.BadRequest(new { error = problem });

            var user = new User
            {
                Username = input.Username,
                FullName = input.FullName,
                Email = input.Email,
                IsAdmin = input.IsAdmin,
                AllowedIpRanges = input.AllowedIpRanges,
                PasswordHash = hasher.Hash(input.Password),
                MustChangePassword = true
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();

            foreach (var groupId in input.GroupIds)
                db.UserGroupMembers.Add(new UserGroupMember { UserId = user.Id, GroupId = groupId });
            await db.SaveChangesAsync();

            await audit.LogAsync("user.create", ctx.ClientIp(), ctx.User.UserName(),
                ctx.User.UserId(), objectType: "user", objectId: user.Id.ToString());

            return Results.Created($"/api/users/{user.Id}", new { user.Id, user.Username });
        });

        g.MapPost("/{id:int}/reset-password", async (
            int id, HttpContext ctx, PlatformDbContext db,
            PasswordHasher hasher, AuditService audit) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null) return Results.NotFound();

            var temp = PasswordHasher.GenerateStrong();
            user.PasswordHash = hasher.Hash(temp);
            user.MustChangePassword = true;
            user.FailedAttempts = 0;
            user.LockedUntil = null;
            await db.SaveChangesAsync();

            await audit.LogAsync("user.reset_password", ctx.ClientIp(), ctx.User.UserName(),
                ctx.User.UserId(), objectType: "user", objectId: id.ToString());

            return Results.Ok(new { temporaryPassword = temp });
        });

        g.MapPost("/{id:int}/active/{active:bool}", async (
            int id, bool active, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null) return Results.NotFound();

            user.Active = active;
            await db.SaveChangesAsync();
            await audit.LogAsync(active ? "user.enable" : "user.disable", ctx.ClientIp(),
                ctx.User.UserName(), ctx.User.UserId(), objectType: "user", objectId: id.ToString());

            return Results.NoContent();
        });

        g.MapDelete("/{id:int}", async (int id, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null) return Results.NotFound();
            if (user.Id == ctx.User.UserId())
                return Results.BadRequest(new { error = "Nao e possivel excluir o proprio usuario." });

            db.Users.Remove(user);
            db.UserGroupMembers.RemoveRange(db.UserGroupMembers.Where(m => m.UserId == id));

            // Direito orfao apontando para um usuario excluido concederia acesso
            // ao proximo id reaproveitado.
            db.ObjectRights.RemoveRange(db.ObjectRights
                .Where(r => r.SubjectType == SubjectType.User && r.SubjectId == id));
            await db.SaveChangesAsync();

            await audit.LogAsync("user.delete", ctx.ClientIp(), ctx.User.UserName(),
                ctx.User.UserId(), objectType: "user", objectId: id.ToString());
            return Results.NoContent();
        });
    }

    // --------------------------------------------------------------- roles
    // Perfil = "o que pode fazer". Existe para o administrador escolher
    // "Operador" em vez de marcar nove permissoes, e para que ajustar o perfil
    // se propague a todos os grupos que o usam.
    private static void MapRoles(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/roles").WithTags("Perfis").RequireAuthorization("admin");

        g.MapGet("/", async (PlatformDbContext db) =>
        {
            var roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
            var perms = await db.RolePermissions.AsNoTracking().ToListAsync();
            var usos = await db.UserGroups.AsNoTracking()
                .Where(x => x.RoleId != null)
                .GroupBy(x => x.RoleId!.Value)
                .Select(x => new { RoleId = x.Key, Total = x.Count() })
                .ToListAsync();

            return roles.Select(r => new
            {
                r.Id, r.Name, r.Description, r.BuiltIn,
                permissoes = perms.Where(p => p.RoleId == r.Id).Select(p => p.Permission).ToList(),
                gruposUsando = usos.FirstOrDefault(u => u.RoleId == r.Id)?.Total ?? 0
            });
        });

        g.MapPost("/", async (RoleInput input, HttpContext ctx,
            PlatformDbContext db, AuditService audit) =>
        {
            if (input.Permissions.FirstOrDefault(p => !Permissions.All.Contains(p)) is { } ruim)
                return Results.BadRequest(new { error = $"Permissao desconhecida: {ruim}" });

            if (await db.Roles.AnyAsync(r => r.Name == input.Name))
                return Results.Conflict(new { error = "Ja existe um perfil com este nome." });

            var role = new Role { Name = input.Name, Description = input.Description };
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            foreach (var p in input.Permissions.Distinct())
                db.RolePermissions.Add(new RolePermission { RoleId = role.Id, Permission = p });
            await db.SaveChangesAsync();

            await audit.WriteAsync(ctx, "role.create", "role", role.Id.ToString(),
                detail: string.Join(", ", input.Permissions));

            return Results.Created($"/api/roles/{role.Id}", new { role.Id, role.Name });
        });

        g.MapPut("/{id:int}", async (int id, RoleInput input, HttpContext ctx,
            PlatformDbContext db, AuditService audit) =>
        {
            var role = await db.Roles.FindAsync(id);
            if (role is null) return Results.NotFound();
            if (role.BuiltIn) return Results.BadRequest(new { error = "Perfil de fabrica nao pode ser alterado. Duplique-o." });

            if (input.Permissions.FirstOrDefault(p => !Permissions.All.Contains(p)) is { } ruim)
                return Results.BadRequest(new { error = $"Permissao desconhecida: {ruim}" });

            role.Name = input.Name;
            role.Description = input.Description;

            db.RolePermissions.RemoveRange(db.RolePermissions.Where(p => p.RoleId == id));
            foreach (var p in input.Permissions.Distinct())
                db.RolePermissions.Add(new RolePermission { RoleId = id, Permission = p });

            await db.SaveChangesAsync();
            await audit.WriteAsync(ctx, "role.update", "role", id.ToString(),
                detail: string.Join(", ", input.Permissions));

            return Results.Ok(new { role.Id, role.Name });
        });

        // Duplicar um perfil de fabrica e o caminho para personalizar sem
        // perder a referencia original.
        g.MapPost("/{id:int}/duplicate", async (int id, PlatformDbContext db) =>
        {
            var origem = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
            if (origem is null) return Results.NotFound();

            var nome = await UniqueNameAsync(origem.Name, n => db.Roles.AnyAsync(r => r.Name == n));
            var copia = new Role { Name = nome, Description = origem.Description, TenantId = origem.TenantId };
            db.Roles.Add(copia);
            await db.SaveChangesAsync();

            var perms = await db.RolePermissions.AsNoTracking()
                .Where(p => p.RoleId == id).Select(p => p.Permission).ToListAsync();
            foreach (var p in perms)
                db.RolePermissions.Add(new RolePermission { RoleId = copia.Id, Permission = p });
            await db.SaveChangesAsync();

            return Results.Created($"/api/roles/{copia.Id}", new { copia.Id, copia.Name });
        });

        g.MapDelete("/{id:int}", async (int id, HttpContext ctx,
            PlatformDbContext db, AuditService audit) =>
        {
            var role = await db.Roles.FindAsync(id);
            if (role is null) return Results.NotFound();
            if (role.BuiltIn) return Results.BadRequest(new { error = "Perfil de fabrica nao pode ser excluido." });

            // Grupo apontando para um perfil inexistente perderia acesso em
            // silencio; melhor recusar e mostrar quem esta usando.
            var emUso = await db.UserGroups.Where(x => x.RoleId == id).Select(x => x.Name).ToListAsync();
            if (emUso.Count > 0)
                return Results.Conflict(new { error = "Perfil em uso.", grupos = emUso });

            db.Roles.Remove(role);
            db.RolePermissions.RemoveRange(db.RolePermissions.Where(p => p.RoleId == id));
            await db.SaveChangesAsync();

            await audit.WriteAsync(ctx, "role.delete", "role", id.ToString());
            return Results.NoContent();
        });
    }

    // -------------------------------------------------------------- groups
    private static void MapGroups(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/groups").WithTags("Grupos").RequireAuthorization("admin");

        g.MapGet("/", async (PlatformDbContext db) =>
        {
            var groups = await db.UserGroups.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
            var roles = await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name);
            var membros = await db.UserGroupMembers.AsNoTracking()
                .GroupBy(m => m.GroupId)
                .Select(x => new { GroupId = x.Key, Total = x.Count() })
                .ToListAsync();

            return groups.Select(x => new
            {
                x.Id, x.Name, x.Description, x.RoleId,
                perfil = x.RoleId is int r ? roles.GetValueOrDefault(r) : null,
                membros = membros.FirstOrDefault(m => m.GroupId == x.Id)?.Total ?? 0
            });
        });

        // Visao completa do grupo: quem esta dentro, qual perfil e o que alcanca.
        // Sem isso o administrador nao consegue conferir uma configuracao.
        g.MapGet("/{id:int}", async (int id, PlatformDbContext db, PermissionService perms) =>
        {
            var group = await db.UserGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (group is null) return Results.NotFound();

            var membros = await db.UserGroupMembers.Where(m => m.GroupId == id)
                .Join(db.Users, m => m.UserId, u => u.Id,
                      (_, u) => new { u.Id, u.Username, u.FullName, u.Active })
                .ToListAsync();

            var rights = await db.ObjectRights.AsNoTracking()
                .Where(r => r.SubjectType == SubjectType.Group && r.SubjectId == id)
                .ToListAsync();

            var perfil = group.RoleId is int rid
                ? await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid)
                : null;

            var permissoesDoPerfil = perfil is null
                ? []
                : await db.RolePermissions.AsNoTracking()
                    .Where(p => p.RoleId == perfil.Id).Select(p => p.Permission).ToListAsync();

            return Results.Ok(new
            {
                group.Id, group.Name, group.Description, group.RoleId,
                perfil = perfil?.Name,
                permissoes = permissoesDoPerfil,
                membros,
                acessos = rights.Select(r => new { r.Id, r.ObjectType, r.ObjectId, r.Permission, r.Effect }),
                camerasAlcancadas = await perms.CamerasForSubjectAsync(SubjectType.Group, id)
            });
        });

        g.MapPost("/", async (UserGroup group, PlatformDbContext db) =>
        {
            db.UserGroups.Add(group);
            await db.SaveChangesAsync();
            return Results.Created($"/api/groups/{group.Id}", group);
        });

        g.MapPut("/{id:int}", async (int id, GroupInput input, PlatformDbContext db) =>
        {
            var group = await db.UserGroups.FindAsync(id);
            if (group is null) return Results.NotFound();

            if (input.RoleId is int rid && !await db.Roles.AnyAsync(r => r.Id == rid))
                return Results.BadRequest(new { error = $"Perfil {rid} nao existe." });

            group.Name = input.Name ?? group.Name;
            group.Description = input.Description ?? group.Description;
            if (input.RoleId is not null) group.RoleId = input.RoleId;

            await db.SaveChangesAsync();
            return Results.Ok(new { group.Id, group.Name, group.RoleId });
        });

        // Excluir o grupo sem levar junto os direitos deixaria linhas orfas
        // apontando para um SubjectId que nao existe mais.
        g.MapDelete("/{id:int}", async (int id, HttpContext ctx,
            PlatformDbContext db, AuditService audit) =>
        {
            var group = await db.UserGroups.FindAsync(id);
            if (group is null) return Results.NotFound();

            db.UserGroups.Remove(group);
            db.UserGroupMembers.RemoveRange(db.UserGroupMembers.Where(m => m.GroupId == id));
            db.ObjectRights.RemoveRange(db.ObjectRights
                .Where(r => r.SubjectType == SubjectType.Group && r.SubjectId == id));
            await db.SaveChangesAsync();

            await audit.WriteAsync(ctx, "group.delete", "group", id.ToString(), detail: group.Name);
            return Results.NoContent();
        });

        // Configuracao completa em uma chamada: perfil + alcance + membros.
        // Antes o front precisava orquestrar N requisicoes e podia deixar a
        // configuracao pela metade se uma falhasse.
        g.MapPut("/{id:int}/access", async (
            int id, GroupAccessInput input, HttpContext ctx,
            PlatformDbContext db, PermissionService perms, AuditService audit) =>
        {
            var group = await db.UserGroups.FindAsync(id);
            if (group is null) return Results.NotFound();

            if (input.RoleId is int rid && !await db.Roles.AnyAsync(r => r.Id == rid))
                return Results.BadRequest(new { error = $"Perfil {rid} nao existe." });

            var gruposInvalidos = input.CameraGroupIds
                .Where(cg => !db.CameraGroups.Any(x => x.Id == cg)).ToList();
            if (gruposInvalidos.Count > 0)
                return Results.BadRequest(new { error = "Grupo de camera inexistente.", ids = gruposInvalidos });

            await using var tx = await db.Database.BeginTransactionAsync();

            group.RoleId = input.RoleId;
            if (input.Name is not null) group.Name = input.Name;
            if (input.Description is not null) group.Description = input.Description;

            // Troca o alcance inteiro: aplicar duas vezes nao duplica direito.
            db.ObjectRights.RemoveRange(db.ObjectRights
                .Where(r => r.SubjectType == SubjectType.Group && r.SubjectId == id));

            // Sem grupo e sem camera marcada = acesso a todas as cameras.
            if (input.AllCameras || (input.CameraGroupIds.Length == 0 && input.CameraIds.Length == 0))
                db.ObjectRights.Add(Direito(id, ObjectTypes.Camera, null, RightEffect.Allow));

            foreach (var cg in input.CameraGroupIds.Distinct())
                db.ObjectRights.Add(Direito(id, ObjectTypes.CameraGroup, cg, RightEffect.Allow));

            foreach (var cam in input.CameraIds.Distinct())
                db.ObjectRights.Add(Direito(id, ObjectTypes.Camera, cam, RightEffect.Allow));

            // Excecoes pontuais: negar vence tudo acima.
            foreach (var cam in input.DeniedCameraIds.Distinct())
                db.ObjectRights.Add(Direito(id, ObjectTypes.Camera, cam, RightEffect.Deny));

            if (input.UserIds is not null)
            {
                db.UserGroupMembers.RemoveRange(db.UserGroupMembers.Where(m => m.GroupId == id));
                foreach (var userId in input.UserIds.Distinct())
                    db.UserGroupMembers.Add(new UserGroupMember { GroupId = id, UserId = userId });
            }

            await db.SaveChangesAsync();
            await tx.CommitAsync();

            await audit.WriteAsync(ctx, "group.access", "group", id.ToString(),
                detail: $"perfil={input.RoleId}, grupos=[{string.Join(",", input.CameraGroupIds)}], " +
                        $"cameras=[{string.Join(",", input.CameraIds)}], negadas=[{string.Join(",", input.DeniedCameraIds)}]");

            return Results.Ok(new
            {
                group.Id, group.Name, group.RoleId,
                camerasAlcancadas = await perms.CamerasForSubjectAsync(SubjectType.Group, id)
            });
        });

        // Previa antes de salvar: "este grupo vera 12 cameras: [...]".
        // Erro de configuracao de permissao costuma ser silencioso; mostrar o
        // resultado antes do commit e o que o torna visivel.
        g.MapPost("/preview", async (GroupAccessInput input, PlatformDbContext db) =>
        {
            var alcancadas = new HashSet<int>();

            if (input.AllCameras || (input.CameraGroupIds.Length == 0 && input.CameraIds.Length == 0))
                alcancadas.UnionWith(await db.Devices
                    .Where(d => d.Kind == DeviceKind.Camera).Select(d => d.Id).ToListAsync());

            foreach (var cg in input.CameraGroupIds)
                alcancadas.UnionWith(await CamerasOfGroupAsync(db, cg));

            alcancadas.UnionWith(input.CameraIds);
            alcancadas.ExceptWith(input.DeniedCameraIds);

            var nomes = await db.Devices.AsNoTracking()
                .Where(d => alcancadas.Contains(d.Id))
                .Select(d => new { d.Id, d.Name }).ToListAsync();

            var permissoes = input.RoleId is int rid
                ? await db.RolePermissions.AsNoTracking()
                    .Where(p => p.RoleId == rid).Select(p => p.Permission).ToListAsync()
                : [];

            return Results.Ok(new { total = nomes.Count, cameras = nomes, permissoes });
        });

        // Duplicar: base pronta para o proximo grupo parecido.
        g.MapPost("/{id:int}/duplicate", async (int id, PlatformDbContext db) =>
        {
            var origem = await db.UserGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (origem is null) return Results.NotFound();

            var nome = await UniqueNameAsync(origem.Name, n => db.UserGroups.AnyAsync(x => x.Name == n));
            var copia = new UserGroup
            {
                Name = nome, Description = origem.Description,
                RoleId = origem.RoleId, TenantId = origem.TenantId
            };
            db.UserGroups.Add(copia);
            await db.SaveChangesAsync();

            // Copia o alcance, nao os membros: grupo novo comeca vazio de gente.
            var rights = await db.ObjectRights.AsNoTracking()
                .Where(r => r.SubjectType == SubjectType.Group && r.SubjectId == id).ToListAsync();

            foreach (var r in rights)
                db.ObjectRights.Add(new ObjectRight
                {
                    TenantId = r.TenantId, SubjectType = SubjectType.Group, SubjectId = copia.Id,
                    ObjectType = r.ObjectType, ObjectId = r.ObjectId,
                    Permission = r.Permission, Effect = r.Effect
                });
            await db.SaveChangesAsync();

            return Results.Created($"/api/groups/{copia.Id}", new { copia.Id, copia.Name });
        });

        g.MapPost("/{groupId:int}/members/{userId:int}", async (
            int groupId, int userId, PlatformDbContext db) =>
        {
            if (await db.UserGroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId))
                return Results.NoContent();

            db.UserGroupMembers.Add(new UserGroupMember { GroupId = groupId, UserId = userId });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapDelete("/{groupId:int}/members/{userId:int}", async (
            int groupId, int userId, PlatformDbContext db) =>
        {
            var member = await db.UserGroupMembers
                .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId);
            if (member is null) return Results.NotFound();

            db.UserGroupMembers.Remove(member);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        static ObjectRight Direito(int groupId, string objectType, int? objectId, RightEffect effect)
            => new()
            {
                SubjectType = SubjectType.Group,
                SubjectId = groupId,
                ObjectType = objectType,
                ObjectId = objectId,
                // O curinga faz o direito seguir o perfil do grupo: mudar o
                // perfil altera o acesso sem reescrever nenhuma linha aqui.
                Permission = Permissions.FromRole,
                Effect = effect
            };
    }

    /// <summary>Cameras de um grupo e de todos os subgrupos.</summary>
    private static async Task<List<int>> CamerasOfGroupAsync(PlatformDbContext db, int groupId)
    {
        var filhos = await db.CameraGroups.AsNoTracking()
            .Where(x => x.ParentId != null)
            .Select(x => new { Pai = x.ParentId!.Value, x.Id }).ToListAsync();

        var alvos = new HashSet<int>();
        var fila = new Queue<int>([groupId]);
        while (fila.Count > 0)
        {
            var atual = fila.Dequeue();
            if (!alvos.Add(atual)) continue;
            foreach (var f in filhos.Where(x => x.Pai == atual)) fila.Enqueue(f.Id);
        }

        return await db.CameraGroupMembers.AsNoTracking()
            .Where(m => alvos.Contains(m.GroupId)).Select(m => m.DeviceId).Distinct().ToListAsync();
    }

    /// <summary>"Operadores" ja existe? Vira "Operadores (copia)", depois "(copia 2)".</summary>
    private static async Task<string> UniqueNameAsync(string baseName, Func<string, Task<bool>> exists)
    {
        var nome = $"{baseName} (copia)";
        for (var i = 2; await exists(nome); i++) nome = $"{baseName} (copia {i})";
        return nome;
    }

    // -------------------------------------------------------------- rights
    private static void MapRights(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/rights").WithTags("Direitos").RequireAuthorization("admin");

        g.MapGet("/permissions", () => Permissions.All);

        g.MapGet("/", async (PlatformDbContext db, SubjectType? subjectType, int? subjectId) =>
        {
            var q = db.ObjectRights.AsNoTracking().AsQueryable();
            if (subjectType is not null) q = q.Where(r => r.SubjectType == subjectType);
            if (subjectId is not null) q = q.Where(r => r.SubjectId == subjectId);
            return await q.ToListAsync();
        });

        g.MapPost("/", async (ObjectRight right, HttpContext ctx,
            PlatformDbContext db, AuditService audit) =>
        {
            if (!Permissions.IsValid(right.Permission))
                return Results.BadRequest(new { error = $"Permissao desconhecida: {right.Permission}" });

            if (!ObjectTypes.All.Contains(right.ObjectType))
                return Results.BadRequest(new { error = $"Tipo de objeto desconhecido: {right.ObjectType}" });

            // Direito apontando para sujeito ou objeto inexistente e o que gera
            // permissao fantasma — recusar aqui evita depurar isso depois.
            var sujeitoExiste = right.SubjectType == SubjectType.Group
                ? await db.UserGroups.AnyAsync(x => x.Id == right.SubjectId)
                : await db.Users.AnyAsync(u => u.Id == right.SubjectId);
            if (!sujeitoExiste)
                return Results.BadRequest(new { error = $"{right.SubjectType} {right.SubjectId} nao existe." });

            if (right.ObjectId is int oid)
            {
                var objetoExiste = right.ObjectType == ObjectTypes.CameraGroup
                    ? await db.CameraGroups.AnyAsync(x => x.Id == oid)
                    : await db.Devices.AnyAsync(d => d.Id == oid);
                if (!objetoExiste)
                    return Results.BadRequest(new { error = $"{right.ObjectType} {oid} nao existe." });
            }

            db.ObjectRights.Add(right);
            await db.SaveChangesAsync();

            await audit.LogAsync("right.grant", ctx.ClientIp(), ctx.User.UserName(), ctx.User.UserId(),
                objectType: right.ObjectType, objectId: right.ObjectId?.ToString() ?? "*",
                detail: $"{right.Effect} {right.Permission} para {right.SubjectType} {right.SubjectId}");

            return Results.Created($"/api/rights/{right.Id}", right);
        });

        // Atribuicao em lote ("perfil"): concede um conjunto de permissoes a um
        // grupo/usuario de uma vez. Com Replace, os Allow anteriores do sujeito
        // sao trocados pelos novos — aplicar duas vezes nao duplica direitos.
        // Os Deny nao sao tocados: excecao pontual continua valendo.
        g.MapPost("/assign", async (
            RightsAssignment input, HttpContext ctx, PlatformDbContext db, AuditService audit) =>
        {
            var desconhecidas = input.Permissions.Where(p => !Permissions.IsValid(p)).ToList();
            if (desconhecidas.Count > 0)
                return Results.BadRequest(new { error = $"Permissao desconhecida: {string.Join(", ", desconhecidas)}" });

            if (input.Replace)
                db.ObjectRights.RemoveRange(await db.ObjectRights
                    .Where(r => r.ObjectType == input.ObjectType
                             && r.SubjectType == input.SubjectType
                             && r.SubjectId == input.SubjectId
                             && r.Effect == RightEffect.Allow)
                    .ToListAsync());

            // Lista de objetos vazia = direito amplo (ObjectId nulo = todas as cameras).
            int?[] alvos = input.ObjectIds.Length == 0
                ? [null]
                : [.. input.ObjectIds.Distinct().Select(i => (int?)i)];

            foreach (var permission in input.Permissions.Distinct())
                foreach (var objectId in alvos)
                    db.ObjectRights.Add(new ObjectRight
                    {
                        SubjectType = input.SubjectType,
                        SubjectId = input.SubjectId,
                        ObjectType = input.ObjectType,
                        ObjectId = objectId,
                        Permission = permission,
                        Effect = RightEffect.Allow
                    });

            await db.SaveChangesAsync();

            await audit.LogAsync("right.assign", ctx.ClientIp(), ctx.User.UserName(), ctx.User.UserId(),
                objectType: input.ObjectType, objectId: input.ObjectIds.Length == 0 ? "*" : string.Join(",", input.ObjectIds),
                detail: $"{input.SubjectType} {input.SubjectId}: {string.Join(", ", input.Permissions)}");

            return Results.Ok(new { direitos = input.Permissions.Distinct().Count() * alvos.Length });
        });

        g.MapDelete("/{id:int}", async (int id, PlatformDbContext db) =>
        {
            var right = await db.ObjectRights.FindAsync(id);
            if (right is null) return Results.NotFound();

            db.ObjectRights.Remove(right);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Direitos efetivos com a origem de cada um: responde "por que este
        // usuario ve esta camera?" em vez de apenas sim/nao. Sem a origem,
        // depurar permissao vira tentativa e erro.
        g.MapGet("/effective/{userId:int}", async (int userId, PermissionService perms) =>
            await perms.ExplainAsync(userId));

        // Previa de alcance de um sujeito ja gravado.
        g.MapGet("/reach", async (
            SubjectType subjectType, int subjectId, PermissionService perms,
            PlatformDbContext db, string permission = Permissions.CameraView) =>
        {
            var ids = await perms.CamerasForSubjectAsync(subjectType, subjectId, permission);
            return await db.Devices.AsNoTracking()
                .Where(d => ids.Contains(d.Id))
                .Select(d => new { d.Id, d.Name }).ToListAsync();
        });
    }

    // --------------------------------------------------------------- audit
    private static void MapAudit(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/audit", async (PlatformDbContext db, int take = 200) =>
            await db.AuditLogs.OrderByDescending(a => a.CreatedAt)
                .Take(Math.Clamp(take, 1, 1000)).AsNoTracking().ToListAsync())
            .WithTags("Auditoria")
            .RequireAuthorization("admin");
    }
}

/// <summary>Atribuicao em lote de direitos. ObjectIds vazio = todas as cameras.</summary>
public record RightsAssignment(
    SubjectType SubjectType,
    int SubjectId,
    string[] Permissions,
    int[] ObjectIds = null!,
    bool Replace = true,
    string ObjectType = ObjectTypes.Camera)
{
    public string[] Permissions { get; init; } = Permissions ?? [];
    public int[] ObjectIds { get; init; } = ObjectIds ?? [];
}

public record RoleInput(string Name, string Description = "", string[] Permissions = null!)
{
    public string[] Permissions { get; init; } = Permissions ?? [];
}

public record GroupInput(string? Name = null, string? Description = null, int? RoleId = null);

/// <summary>
/// Configuracao completa de um grupo em uma chamada: o perfil responde "o que
/// pode fazer", os grupos de camera respondem "sobre o que", e as negadas
/// cobrem a excecao pontual.
/// </summary>
public record GroupAccessInput(
    int? RoleId = null,
    string? Name = null,
    string? Description = null,
    int[] CameraGroupIds = null!,
    int[] CameraIds = null!,
    int[] DeniedCameraIds = null!,
    int[]? UserIds = null,
    bool AllCameras = false)
{
    public int[] CameraGroupIds { get; init; } = CameraGroupIds ?? [];
    public int[] CameraIds { get; init; } = CameraIds ?? [];
    public int[] DeniedCameraIds { get; init; } = DeniedCameraIds ?? [];
}

public record ChangePasswordInput(string CurrentPassword, string NewPassword);
public record TotpInput(string Code);

public record UserInput(
    string Username,
    string Password,
    string FullName = "",
    string Email = "",
    bool IsAdmin = false,
    string AllowedIpRanges = "",
    int[] GroupIds = null!)
{
    public int[] GroupIds { get; init; } = GroupIds ?? [];
}
