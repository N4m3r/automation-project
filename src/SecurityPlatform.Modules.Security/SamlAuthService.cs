using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Modules.Security;

/// <summary>
/// SAML 2.0 mínimo: redirect SSO + ACS que extrai NameID de Assertion.
/// Não valida assinatura XML (configure proxy/IdP confiado em produção).
/// </summary>
public sealed class SamlAuthService(
    PlatformDbContext db,
    PasswordHasher hasher,
    AuditService audit,
    AuthService auth,
    IOptionsMonitor<SecurityOptions> options,
    ILogger<SamlAuthService> log)
{
    private SamlOptions Saml => options.CurrentValue.Saml;

    public bool Enabled =>
        Saml.Enabled && !string.IsNullOrWhiteSpace(Saml.IdpSsoUrl);

    public string? BuildRedirectUrl(string? relayState = null)
    {
        if (!Enabled) return null;
        var req = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"""
            <samlp:AuthnRequest xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol"
              ID="_{Guid.NewGuid():N}" Version="2.0" IssueInstant="{DateTime.UtcNow:o}"
              AssertionConsumerServiceURL="{Saml.AcsPath}"
              Destination="{Saml.IdpSsoUrl}">
              <saml:Issuer xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion">{Saml.EntityId}</saml:Issuer>
            </samlp:AuthnRequest>
            """));
        var q = "SAMLRequest=" + Uri.EscapeDataString(req);
        if (!string.IsNullOrEmpty(relayState))
            q += "&RelayState=" + Uri.EscapeDataString(relayState);
        var sep = Saml.IdpSsoUrl.Contains('?') ? "&" : "?";
        return Saml.IdpSsoUrl + sep + q;
    }

    public async Task<LoginResult> HandleAcsAsync(string? samlResponseB64, string ip, CancellationToken ct = default)
    {
        if (!Enabled)
            return new LoginResult(false, Error: "SAML desabilitado.");
        if (string.IsNullOrWhiteSpace(samlResponseB64))
            return new LoginResult(false, Error: "SAMLResponse ausente.");

        string xml;
        try
        {
            var bytes = Convert.FromBase64String(samlResponseB64.Trim());
            xml = Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return new LoginResult(false, Error: "SAMLResponse inválido (base64).");
        }

        string? nameId = null;
        try
        {
            var doc = XDocument.Parse(xml);
            nameId = doc.Descendants().FirstOrDefault(e =>
                e.Name.LocalName is "NameID" or "NameId")?.Value?.Trim();
            if (string.IsNullOrEmpty(nameId))
            {
                // Atributo comum email
                nameId = doc.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName == "AttributeValue"
                    && e.Parent?.Attributes().Any(a =>
                        a.Value.Contains("email", StringComparison.OrdinalIgnoreCase)
                        || a.Value.Contains("uid", StringComparison.OrdinalIgnoreCase)) == true)
                    ?.Value?.Trim();
            }
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Falha ao parsear SAML Response");
            return new LoginResult(false, Error: "XML SAML inválido.");
        }

        if (string.IsNullOrWhiteSpace(nameId))
            return new LoginResult(false, Error: "NameID ausente na Assertion.");

        var username = nameId.Contains('@') ? nameId.Split('@')[0] : nameId;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        if (user is null)
        {
            if (!Saml.AutoProvision)
                return new LoginResult(false, Error: "Usuário SAML sem conta local.");

            user = new User
            {
                Username = username,
                FullName = nameId,
                PasswordHash = hasher.Hash(PasswordHasher.GenerateStrong()),
                IsAdmin = false,
                MustChangePassword = false,
                Active = true
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            var grupo = await db.UserGroups.FirstOrDefaultAsync(g => g.Name == Saml.DefaultGroupName, ct);
            if (grupo is not null)
            {
                db.UserGroupMembers.Add(new UserGroupMember { UserId = user.Id, GroupId = grupo.Id });
                await db.SaveChangesAsync(ct);
            }
        }

        if (!user.Active)
            return new LoginResult(false, Error: "Usuário desativado.");

        await audit.LogAsync("login.saml", ip, username, user.Id, true, ct: ct);
        var expires = DateTime.UtcNow.AddMinutes(options.CurrentValue.TokenMinutes);
        return new LoginResult(true, auth.IssueToken(user, expires), ExpiresAt: expires);
    }
}
