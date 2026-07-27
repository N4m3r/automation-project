using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OtpNet;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Modules.Security;

public record LoginRequest(string Username, string Password, string? TotpCode = null);

public record LoginResult(
    bool Ok,
    string? Token = null,
    string? Error = null,
    bool TwoFactorRequired = false,
    bool MustChangePassword = false,
    DateTime? ExpiresAt = null);

/// <summary>
/// Autenticacao: senha + 2FA (TOTP), restricao por faixa de IP e bloqueio
/// progressivo por tentativas. Toda tentativa vai para a auditoria.
/// </summary>
public class AuthService(
    PlatformDbContext db,
    PasswordHasher hasher,
    AuditService audit,
    LdapAuthService ldap,
    IOptionsMonitor<SecurityOptions> options)
{
    private SecurityOptions _opt => options.CurrentValue;

    public async Task<LoginResult> LoginAsync(LoginRequest req, string ip, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == req.Username, ct);

        // Mensagem generica: nao revela se o usuario existe.
        const string generic = "Usuario ou senha invalidos.";

        // Conta local inexistente: tenta LDAP (provisionamento JIT).
        if (user is null)
        {
            if (ldap.Enabled)
            {
                var ldapUser = await ldap.AuthenticateAsync(req.Username, req.Password, ip, ct);
                if (ldapUser is not null)
                    return await FinalizeLoginAsync(ldapUser, ip, requireTotp: true, totp: req.TotpCode, ct);
            }

            await audit.LogAsync("login", ip, req.Username, success: false, detail: "usuario inexistente", ct: ct);
            return new LoginResult(false, Error: generic);
        }

        if (!user.Active)
        {
            await audit.LogAsync("login", ip, user.Username, user.Id, false, detail: "usuario inativo", ct: ct);
            return new LoginResult(false, Error: "Usuario desativado.");
        }

        if (user.LockedUntil is { } until && until > DateTime.UtcNow)
        {
            await audit.LogAsync("login", ip, user.Username, user.Id, false, detail: "usuario bloqueado", ct: ct);
            return new LoginResult(false, Error: $"Usuario bloqueado ate {until:HH:mm} UTC.");
        }

        if (!IpAllowed(user.AllowedIpRanges, ip))
        {
            await audit.LogAsync("login", ip, user.Username, user.Id, false, detail: "IP nao autorizado", ct: ct);
            return new LoginResult(false, Error: "Acesso nao permitido a partir deste IP.");
        }

        if (!hasher.Verify(req.Password, user.PasswordHash))
        {
            // Fallback LDAP: senha de domínio válida quando a local falhou.
            if (ldap.Enabled && _opt.Ldap.FallbackOnLocalFailure)
            {
                var ldapUser = await ldap.AuthenticateAsync(req.Username, req.Password, ip, ct);
                if (ldapUser is not null)
                    return await FinalizeLoginAsync(ldapUser, ip, requireTotp: true, totp: req.TotpCode, ct);
            }

            user.FailedAttempts++;
            if (user.FailedAttempts >= _opt.MaxFailedAttempts)
            {
                user.LockedUntil = DateTime.UtcNow.AddMinutes(_opt.LockoutMinutes);
                user.FailedAttempts = 0;
            }
            await db.SaveChangesAsync(ct);
            await audit.LogAsync("login", ip, user.Username, user.Id, false, detail: "senha invalida", ct: ct);
            return new LoginResult(false, Error: generic);
        }

        return await FinalizeLoginAsync(user, ip, requireTotp: true, totp: req.TotpCode, ct);
    }

    private async Task<LoginResult> FinalizeLoginAsync(
        User user, string ip, bool requireTotp, string? totp, CancellationToken ct)
    {
        if (requireTotp && user.TwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(totp))
                return new LoginResult(false, Error: "Codigo 2FA obrigatorio.", TwoFactorRequired: true);

            if (!VerifyTotp(user.TotpSecret, totp))
            {
                await audit.LogAsync("login", ip, user.Username, user.Id, false, detail: "2FA invalido", ct: ct);
                return new LoginResult(false, Error: "Codigo 2FA invalido.", TwoFactorRequired: true);
            }
        }

        if (_opt.PasswordExpiryDays > 0 &&
            user.PasswordChangedAt.AddDays(_opt.PasswordExpiryDays) < DateTime.UtcNow)
        {
            user.MustChangePassword = true;
        }

        user.FailedAttempts = 0;
        user.LockedUntil = null;
        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("login", ip, user.Username, user.Id, true, ct: ct);

        var expires = DateTime.UtcNow.AddMinutes(_opt.TokenMinutes);
        return new LoginResult(true, IssueToken(user, expires), ExpiresAt: expires,
            MustChangePassword: user.MustChangePassword);
    }

    public string IssueToken(User user, DateTime expires, int? tenantOverride = null)
    {
        var tenantId = tenantOverride is > 0 ? tenantOverride.Value : user.TenantId;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new("tenant", tenantId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        if (user.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, "admin"));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.JwtKey));
        var token = new JwtSecurityToken(
            issuer: _opt.JwtIssuer,
            audience: _opt.JwtAudience,
            claims: claims,
            expires: expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Valida um token fora do pipeline de autenticação do ASP.NET.
    /// Usado pelo nó de mídia, que pergunta por HTTP se determinado token pode
    /// ler determinada câmera — ele não fala o protocolo de Bearer header.
    /// </summary>
    public ClaimsPrincipal? ValidateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        try
        {
            return new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _opt.JwtIssuer,
                ValidAudience = _opt.JwtAudience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.JwtKey)),
                ClockSkew = TimeSpan.FromSeconds(30)
            }, out _);
        }
        catch (Exception)
        {
            return null;   // token invalido, expirado ou assinado com outra chave
        }
    }

    public async Task<string?> ChangePasswordAsync(
        int userId, string currentPassword, string newPassword, string ip, CancellationToken ct = default)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null) return "Usuario nao encontrado.";

        if (!hasher.Verify(currentPassword, user.PasswordHash))
        {
            await audit.LogAsync("password.change", ip, user.Username, user.Id, false, ct: ct);
            return "Senha atual incorreta.";
        }

        if (hasher.Validate(newPassword) is { } problem) return problem;

        user.PasswordHash = hasher.Hash(newPassword);
        user.PasswordChangedAt = DateTime.UtcNow;
        user.MustChangePassword = false;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("password.change", ip, user.Username, user.Id, true, ct: ct);
        return null;
    }

    /// <summary>Gera segredo TOTP e a URI para leitura no app autenticador.</summary>
    public async Task<(string Secret, string Uri)> SetupTwoFactorAsync(int userId, CancellationToken ct = default)
    {
        var user = await db.Users.FindAsync([userId], ct)
                   ?? throw new InvalidOperationException("Usuario nao encontrado.");

        var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        user.TotpSecret = secret;
        await db.SaveChangesAsync(ct);

        var label = Uri.EscapeDataString($"{_opt.JwtIssuer}:{user.Username}");
        return (secret, $"otpauth://totp/{label}?secret={secret}&issuer={_opt.JwtIssuer}");
    }

    /// <summary>Confirma o codigo do app e ativa o 2FA definitivamente.</summary>
    public async Task<bool> ConfirmTwoFactorAsync(int userId, string code, CancellationToken ct = default)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null || !VerifyTotp(user.TotpSecret, code)) return false;

        user.TwoFactorEnabled = true;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static bool VerifyTotp(string? secret, string? code)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code)) return false;
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        // Janela de +/-1 passo cobre relogios levemente dessincronizados.
        return totp.VerifyTotp(code.Trim(), out _, new VerificationWindow(1, 1));
    }

    /// <summary>Delega ao casamento unico de IP do Core.</summary>
    public static bool IpAllowed(string ranges, string ip) => IpRules.Matches(ranges, ip);
}
