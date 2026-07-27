using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Modules.Security;

/// <summary>
/// Autenticação LDAP / Active Directory por bind simples.
///
/// Fluxo: tenta bind com <c>DOMAIN\user</c> ou UPN; se ok, provisiona o
/// usuário local (JIT) com o perfil padrão e emite o JWT da plataforma.
/// Desabilitado por padrão — configure <c>Security:Ldap</c>.
/// </summary>
public class LdapAuthService(
    PlatformDbContext db,
    PasswordHasher hasher,
    AuditService audit,
    IOptionsMonitor<SecurityOptions> options,
    ILogger<LdapAuthService> log)
{
    private SecurityOptions _opt => options.CurrentValue;
    private LdapOptions Ldap => _opt.Ldap;

    public bool Enabled => Ldap.Enabled && !string.IsNullOrWhiteSpace(Ldap.Host);

    /// <summary>
    /// Autentica no diretório e devolve (ou cria) o usuário local correspondente.
    /// Null = credenciais LDAP inválidas ou LDAP desligado.
    /// </summary>
    public async Task<User?> AuthenticateAsync(string username, string password, string ip, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        try
        {
            if (!Bind(username, password))
            {
                await audit.LogAsync("login.ldap", ip, username, success: false, detail: "bind falhou", ct: ct);
                return null;
            }
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Falha de comunicação LDAP com {Host}", Ldap.Host);
            await audit.LogAsync("login.ldap", ip, username, success: false, detail: e.Message, ct: ct);
            return null;
        }

        var local = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        if (local is null)
        {
            if (!Ldap.AutoProvision)
            {
                await audit.LogAsync("login.ldap", ip, username, success: false,
                    detail: "usuário LDAP sem conta local e AutoProvision=false", ct: ct);
                return null;
            }

            local = new User
            {
                Username = username,
                FullName = username,
                // Senha local aleatória — login local não substitui o AD.
                PasswordHash = hasher.Hash(PasswordHasher.GenerateStrong()),
                IsAdmin = false,
                MustChangePassword = false,
                Active = true
            };
            db.Users.Add(local);
            await db.SaveChangesAsync(ct);

            // Grupo padrão de operadores LDAP, se existir; senão cria vínculo vazio.
            var grupo = await db.UserGroups.FirstOrDefaultAsync(g => g.Name == Ldap.DefaultGroupName, ct);
            if (grupo is not null)
            {
                db.UserGroupMembers.Add(new UserGroupMember { UserId = local.Id, GroupId = grupo.Id });
                await db.SaveChangesAsync(ct);
            }

            log.LogInformation("Usuário LDAP provisionado: {User}", username);
        }

        if (!local.Active)
        {
            await audit.LogAsync("login.ldap", ip, username, local.Id, false, detail: "usuário local inativo", ct: ct);
            return null;
        }

        if (Ldap.SyncGroups)
        {
            try { await SyncGroupsAsync(username, password, local, ct); }
            catch (Exception e)
            {
                log.LogWarning(e, "Sync de grupos AD falhou para {User}", username);
            }
        }

        await audit.LogAsync("login.ldap", ip, username, local.Id, true, ct: ct);
        return local;
    }

    private bool Bind(string username, string password)
    {
        using var conn = OpenBound(username, password);
        return conn is not null;
    }

    private LdapConnection? OpenBound(string username, string password)
    {
        try
        {
            var id = BuildBindIdentity(username);
            var conn = new LdapConnection(new LdapDirectoryIdentifier(Ldap.Host, Ldap.Port))
            {
                AuthType = AuthType.Basic,
                SessionOptions = { ProtocolVersion = 3, SecureSocketLayer = Ldap.UseSsl }
            };
            conn.Timeout = TimeSpan.FromSeconds(Math.Clamp(Ldap.TimeoutSeconds, 3, 30));
            conn.Credential = new NetworkCredential(id, password);
            conn.Bind();
            return conn;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lê memberOf do AD e alinha UserGroupMembers aos grupos locais (por CN ou GroupMap).
    /// </summary>
    private async Task SyncGroupsAsync(string username, string password, User local, CancellationToken ct)
    {
        using var conn = OpenBound(username, password);
        if (conn is null) return;

        var filter = (Ldap.UserSearchFilter ?? "(sAMAccountName={user})")
            .Replace("{user}", EscapeLdap(username), StringComparison.OrdinalIgnoreCase);
        var baseDn = string.IsNullOrWhiteSpace(Ldap.SearchBase)
            ? GuessSearchBase(username)
            : Ldap.SearchBase;

        var req = new SearchRequest(baseDn, filter, SearchScope.Subtree, "memberOf", "cn", "displayName");
        var resp = (SearchResponse)conn.SendRequest(req);
        if (resp.Entries.Count == 0) return;

        var entry = resp.Entries[0];
        var adGroups = new List<string>();
        if (entry.Attributes.Contains("memberOf"))
        {
            foreach (var raw in entry.Attributes["memberOf"].GetValues(typeof(string)).Cast<string>())
            {
                var cn = ExtractCn(raw);
                if (!string.IsNullOrEmpty(cn)) adGroups.Add(cn);
            }
        }

        if (adGroups.Count == 0) return;

        var desiredLocal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ad in adGroups)
        {
            if (Ldap.GroupMap.TryGetValue(ad, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
                desiredLocal.Add(mapped.Trim());
            else
                desiredLocal.Add(ad);
        }

        // Garante DefaultGroup se mapeado e lista vazia de grupos locais existentes.
        var localGroups = await db.UserGroups.AsNoTracking()
            .Where(g => g.TenantId == local.TenantId)
            .ToListAsync(ct);

        var desiredIds = localGroups
            .Where(g => desiredLocal.Contains(g.Name))
            .Select(g => g.Id)
            .ToHashSet();

        var current = await db.UserGroupMembers.Where(m => m.UserId == local.Id).ToListAsync(ct);
        var currentIds = current.Select(m => m.GroupId).ToHashSet();

        foreach (var add in desiredIds.Except(currentIds))
            db.UserGroupMembers.Add(new UserGroupMember { UserId = local.Id, GroupId = add });

        // Remove só grupos que existem no AD map e não estão mais no memberOf
        // (não remove grupos manuais sem contraparte AD).
        var adLinkedLocalIds = localGroups
            .Where(g => desiredLocal.Contains(g.Name) || Ldap.GroupMap.Values.Contains(g.Name, StringComparer.OrdinalIgnoreCase)
                        || adGroups.Contains(g.Name, StringComparer.OrdinalIgnoreCase))
            .Select(g => g.Id)
            .ToHashSet();

        foreach (var rem in current.Where(m => adLinkedLocalIds.Contains(m.GroupId) && !desiredIds.Contains(m.GroupId)))
            db.UserGroupMembers.Remove(rem);

        await db.SaveChangesAsync(ct);
        log.LogInformation("Sync AD→grupos {User}: {Groups}", username, string.Join(", ", desiredLocal));
    }

    private static string ExtractCn(string dn)
    {
        // CN=Ops,OU=Groups,DC=corp,DC=local
        foreach (var part in dn.Split(','))
        {
            var p = part.Trim();
            if (p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                return p[3..].Trim();
        }
        return dn.Trim();
    }

    private static string EscapeLdap(string s) =>
        s.Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");

    private string GuessSearchBase(string username)
    {
        if (!string.IsNullOrWhiteSpace(Ldap.UserPrincipalSuffix))
        {
            var parts = Ldap.UserPrincipalSuffix.TrimStart('@').Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
                return string.Join(",", parts.Select(p => "DC=" + p));
        }
        if (!string.IsNullOrWhiteSpace(Ldap.Domain))
            return "DC=" + Ldap.Domain;
        return "";
    }

    private string BuildBindIdentity(string username)
    {
        // Já veio como UPN ou DOMAIN\user.
        if (username.Contains('@') || username.Contains('\\'))
            return username;

        if (!string.IsNullOrWhiteSpace(Ldap.Domain))
            return $"{Ldap.Domain}\\{username}";

        if (!string.IsNullOrWhiteSpace(Ldap.UserPrincipalSuffix))
            return $"{username}@{Ldap.UserPrincipalSuffix.TrimStart('@')}";

        if (!string.IsNullOrWhiteSpace(Ldap.BindDnTemplate))
            return Ldap.BindDnTemplate.Replace("{user}", username, StringComparison.OrdinalIgnoreCase);

        return username;
    }
}
