namespace SecurityPlatform.Modules.Security;

public class SecurityOptions
{
    public const string Section = "Security";

    /// <summary>
    /// Chave de assinatura do JWT. Prefira variável de ambiente
    /// <c>Security__JwtKey</c> ou cofre — não commitar a de produção.
    /// </summary>
    public string JwtKey { get; set; } = "";

    public string JwtIssuer { get; set; } = "SecurityPlatform";
    public string JwtAudience { get; set; } = "SecurityPlatform";
    public int TokenMinutes { get; set; } = 60;

    // --- Politica de senha
    public int PasswordMinLength { get; set; } = 10;
    public bool RequireStrongPassword { get; set; } = true;

    /// <summary>0 = senha nunca expira.</summary>
    public int PasswordExpiryDays { get; set; } = 0;

    // --- Bloqueio por tentativas
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>Senha do admin no primeiro boot. Vazio = gerada e exibida no log.</summary>
    public string BootstrapAdminPassword { get; set; } = "";

    /// <summary>
    /// Origens de Clientes de Monitoramento hospedados em outro no que podem
    /// consumir esta API (ex.: "https://vms-matriz:8443"). Vazio = so a propria
    /// origem. Curinga nao e aceito: cada origem deve ser listada.
    /// </summary>
    public string[] AllowedClientOrigins { get; set; } = [];

    /// <summary>
    /// Chave HMAC das licenças assinadas. Vazio = reutiliza <see cref="JwtKey"/>.
    /// Prefira <c>Security__LicenseSigningKey</c> no ambiente.
    /// </summary>
    public string LicenseSigningKey { get; set; } = "";

    /// <summary>
    /// Se true e houver chave de assinatura, só aceita licença no formato
    /// <c>payload.assinatura</c>. Em desenvolvimento deixe false.
    /// </summary>
    public bool RequireSignedLicense { get; set; }

    /// <summary>HTTPS no Kestrel (certificado PEM ou PFX).</summary>
    public HttpsOptions Https { get; set; } = new();

    /// <summary>LDAP / Active Directory.</summary>
    public LdapOptions Ldap { get; set; } = new();

    /// <summary>SSO OpenID Connect (Azure AD, Keycloak, Okta, Google…).</summary>
    public OidcOptions Oidc { get; set; } = new();

    /// <summary>SSO SAML 2.0 (ACS simplificado + redirect ao IdP).</summary>
    public SamlOptions Saml { get; set; } = new();

    /// <summary>
    /// Diretório do keyring Data Protection (senhas de câmera, gravações cifradas).
    /// Em multi-nó, use o mesmo path/volume em todos os processos.
    /// </summary>
    public string KeyRingPath { get; set; } = "./data/keys";
}

public class HttpsOptions
{
    /// <summary>Liga o listener HTTPS no Kestrel.</summary>
    public bool Enabled { get; set; }

    public int Port { get; set; } = 8443;

    /// <summary>Caminho do certificado PEM (.crt/.pem) ou PFX.</summary>
    public string CertificatePath { get; set; } = "";

    /// <summary>Chave privada PEM (quando o certificado não é PFX).</summary>
    public string KeyPath { get; set; } = "";

    /// <summary>Senha do PFX, se aplicável.</summary>
    public string CertificatePassword { get; set; } = "";

    /// <summary>Mantém também o listener HTTP (útil atrás de proxy ou em migração).</summary>
    public bool AlsoListenHttp { get; set; } = true;

    public int HttpPort { get; set; } = 8080;
}

public class LdapOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 389;
    public bool UseSsl { get; set; }
    public int TimeoutSeconds { get; set; } = 8;

    /// <summary>NetBIOS (ex.: CORP) para bind DOMAIN\user.</summary>
    public string Domain { get; set; } = "";

    /// <summary>Sufixo UPN (ex.: corp.local) para user@corp.local.</summary>
    public string UserPrincipalSuffix { get; set; } = "";

    /// <summary>Template DN: cn={user},ou=Users,dc=corp,dc=local</summary>
    public string BindDnTemplate { get; set; } = "";

    /// <summary>Cria usuário local na primeira autenticação LDAP bem-sucedida.</summary>
    public bool AutoProvision { get; set; } = true;

    /// <summary>Grupo local ao qual o usuário LDAP provisionado é adicionado.</summary>
    public string DefaultGroupName { get; set; } = "Operadores";

    /// <summary>
    /// Se true, tenta LDAP quando a senha local falha (e o usuário existe ou
    /// AutoProvision está ligado). Admin local com senha válida nunca cai no AD.
    /// </summary>
    public bool FallbackOnLocalFailure { get; set; } = true;

    /// <summary>
    /// Sincroniza memberOf do AD com grupos locais pelo nome CN.
    /// Mapa opcional: "CN do AD" → "Nome grupo local" (JSON no appsettings como objeto).
    /// </summary>
    public bool SyncGroups { get; set; } = true;

    /// <summary>Base DN para busca de usuário (ex.: DC=corp,DC=local).</summary>
    public string SearchBase { get; set; } = "";

    /// <summary>Filtro LDAP; {user} é substituído. Padrão sAMAccountName.</summary>
    public string UserSearchFilter { get; set; } = "(sAMAccountName={user})";

    /// <summary>
    /// Mapeamento AD→local. Chave = CN ou nome do grupo AD; valor = nome do UserGroup local.
    /// Vazio = usa o CN do AD igual ao Name do grupo local.
    /// </summary>
    public Dictionary<string, string> GroupMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>DN de serviço para busca (opcional). Vazio = usa o bind do usuário.</summary>
    public string ServiceBindDn { get; set; } = "";
    public string ServiceBindPassword { get; set; } = "";
}

public class OidcOptions
{
    public bool Enabled { get; set; }
    /// <summary>Authority (ex.: https://login.microsoftonline.com/{tenant}/v2.0).</summary>
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Redirect URI completo registrado no IdP.</summary>
    public string RedirectUri { get; set; } = "";
    public string Scopes { get; set; } = "openid profile email";
    /// <summary>Claim usada como username local.</summary>
    public string UsernameClaim { get; set; } = "preferred_username";
    public bool AutoProvision { get; set; } = true;
    public string DefaultGroupName { get; set; } = "Operadores";
}

public class SamlOptions
{
    public bool Enabled { get; set; }
    /// <summary>Entity ID do SP (esta plataforma).</summary>
    public string EntityId { get; set; } = "SecurityPlatform";
    /// <summary>URL de SSO do IdP (redirect binding).</summary>
    public string IdpSsoUrl { get; set; } = "";
    /// <summary>Entity ID do IdP (metadados).</summary>
    public string IdpEntityId { get; set; } = "";
    public bool AutoProvision { get; set; } = true;
    public string DefaultGroupName { get; set; } = "Operadores";
    /// <summary>ACS path (relativo) — padrão /api/auth/saml/acs.</summary>
    public string AcsPath { get; set; } = "/api/auth/saml/acs";
}
