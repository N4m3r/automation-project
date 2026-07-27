using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Modules.Security;

/// <summary>
/// OIDC Authorization Code (sem middleware externo): discover → authorize → token → JWT local.
/// </summary>
public sealed class OidcAuthService(
    PlatformDbContext db,
    PasswordHasher hasher,
    AuditService audit,
    AuthService auth,
    IOptionsMonitor<SecurityOptions> options,
    ILogger<OidcAuthService> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private OidcOptions Oidc => options.CurrentValue.Oidc;

    public bool Enabled =>
        Oidc.Enabled
        && !string.IsNullOrWhiteSpace(Oidc.Authority)
        && !string.IsNullOrWhiteSpace(Oidc.ClientId);

    public async Task<(string Url, string State)?> BuildAuthorizeUrlAsync(CancellationToken ct = default)
    {
        if (!Enabled) return null;
        var disco = await DiscoverAsync(ct);
        if (disco is null) return null;

        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var redirect = Oidc.RedirectUri ?? "";
        var q =
            "response_type=code" +
            "&client_id=" + Uri.EscapeDataString(Oidc.ClientId) +
            "&redirect_uri=" + Uri.EscapeDataString(redirect) +
            "&scope=" + Uri.EscapeDataString(Oidc.Scopes) +
            "&state=" + Uri.EscapeDataString(state);
        return ($"{disco.AuthorizationEndpoint}?{q}", state);
    }

    public async Task<LoginResult> HandleCallbackAsync(string code, string ip, CancellationToken ct = default)
    {
        if (!Enabled)
            return new LoginResult(false, Error: "OIDC desabilitado.");

        var disco = await DiscoverAsync(ct);
        if (disco is null)
            return new LoginResult(false, Error: "OIDC discovery falhou.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = Oidc.RedirectUri,
            ["client_id"] = Oidc.ClientId
        };
        if (!string.IsNullOrEmpty(Oidc.ClientSecret))
            form["client_secret"] = Oidc.ClientSecret;

        using var content = new FormUrlEncodedContent(form);
        using var res = await Http.PostAsync(disco.TokenEndpoint, content, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("OIDC token error {Status}: {Body}", (int)res.StatusCode, body);
            return new LoginResult(false, Error: "Falha ao trocar code OIDC.");
        }

        using var doc = JsonDocument.Parse(body);
        var idToken = doc.RootElement.TryGetProperty("id_token", out var it) ? it.GetString() : null;
        if (string.IsNullOrEmpty(idToken))
            return new LoginResult(false, Error: "IdP não devolveu id_token.");

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(idToken);
        var username = jwt.Claims.FirstOrDefault(c =>
                c.Type == Oidc.UsernameClaim || c.Type == "preferred_username" || c.Type == "email" || c.Type == "upn")
            ?.Value
            ?? jwt.Subject
            ?? "";

        if (string.IsNullOrWhiteSpace(username))
            return new LoginResult(false, Error: "Claim de usuário ausente no id_token.");

        // Preferir parte local do email/UPN como username da plataforma.
        if (username.Contains('@'))
            username = username.Split('@')[0];

        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        if (user is null)
        {
            if (!Oidc.AutoProvision)
                return new LoginResult(false, Error: "Usuário OIDC sem conta local.");

            user = new User
            {
                Username = username,
                FullName = jwt.Claims.FirstOrDefault(c => c.Type is "name" or "given_name")?.Value ?? username,
                PasswordHash = hasher.Hash(PasswordHasher.GenerateStrong()),
                IsAdmin = false,
                MustChangePassword = false,
                Active = true
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);

            var grupo = await db.UserGroups.FirstOrDefaultAsync(g => g.Name == Oidc.DefaultGroupName, ct);
            if (grupo is not null)
            {
                db.UserGroupMembers.Add(new UserGroupMember { UserId = user.Id, GroupId = grupo.Id });
                await db.SaveChangesAsync(ct);
            }
            log.LogInformation("Usuário OIDC provisionado: {User}", username);
        }

        if (!user.Active)
            return new LoginResult(false, Error: "Usuário desativado.");

        await audit.LogAsync("login.oidc", ip, username, user.Id, true, ct: ct);
        var expires = DateTime.UtcNow.AddMinutes(options.CurrentValue.TokenMinutes);
        return new LoginResult(true, auth.IssueToken(user, expires), ExpiresAt: expires);
    }

    private async Task<Disco?> DiscoverAsync(CancellationToken ct)
    {
        try
        {
            var url = Oidc.Authority.TrimEnd('/') + "/.well-known/openid-configuration";
            using var res = await Http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return null;
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            return new Disco(
                root.GetProperty("authorization_endpoint").GetString()!,
                root.GetProperty("token_endpoint").GetString()!);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "OIDC discovery falhou em {Auth}", Oidc.Authority);
            return null;
        }
    }

    private sealed record Disco(string AuthorizationEndpoint, string TokenEndpoint);
}
