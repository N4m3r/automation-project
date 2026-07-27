using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SecurityPlatform.Modules.Security;

/// <summary>
/// Persiste overrides de Security/LDAP em <c>data/runtime-security.json</c>
/// (reloadOnChange). Nao grava JwtKey nem segredos de certificado.
/// </summary>
public sealed class RuntimeSecurityWriter(
    IHostEnvironment env,
    IOptionsMonitor<SecurityOptions> options,
    ILogger<RuntimeSecurityWriter> log)
{
    public static string RelativePath => Path.Combine("data", "runtime-security.json");

    public string AbsolutePath =>
        Path.GetFullPath(Path.Combine(env.ContentRootPath, RelativePath));

    public string ResolveKeyRingPath()
    {
        var raw = options.CurrentValue.KeyRingPath;
        if (string.IsNullOrWhiteSpace(raw)) raw = "./data/keys";
        return Path.IsPathRooted(raw)
            ? Path.GetFullPath(raw)
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, raw));
    }

    public object Snapshot()
    {
        var o = options.CurrentValue;
        var keyRing = ResolveKeyRingPath();

        return new
        {
            o.TokenMinutes,
            o.PasswordMinLength,
            o.RequireStrongPassword,
            o.PasswordExpiryDays,
            o.MaxFailedAttempts,
            o.LockoutMinutes,
            keyRingPath = keyRing,
            keyRingExists = Directory.Exists(keyRing),
            jwtConfigured = !string.IsNullOrWhiteSpace(o.JwtKey) && o.JwtKey.Length >= 32,
            o.RequireSignedLicense,
            ldap = new
            {
                o.Ldap.Enabled,
                o.Ldap.Host,
                o.Ldap.Port,
                o.Ldap.UseSsl,
                o.Ldap.TimeoutSeconds,
                o.Ldap.Domain,
                o.Ldap.UserPrincipalSuffix,
                o.Ldap.BindDnTemplate,
                o.Ldap.AutoProvision,
                o.Ldap.DefaultGroupName,
                o.Ldap.FallbackOnLocalFailure
            },
            runtimeFile = AbsolutePath,
            runtimeFileExists = File.Exists(AbsolutePath),
            note = "JwtKey e certificados so por ambiente/appsettings — nao editaveis aqui. Keyring multi-no: Security:KeyRingPath no mesmo volume."
        };
    }

    public async Task WriteAsync(SecuritySettingsInput input, CancellationToken ct = default)
    {
        if (input.TokenMinutes is < 5 or > 24 * 60)
            throw new ArgumentException("TokenMinutes deve estar entre 5 e 1440.");
        if (input.PasswordMinLength is < 6 or > 128)
            throw new ArgumentException("PasswordMinLength deve estar entre 6 e 128.");
        if (input.MaxFailedAttempts is < 1 or > 50)
            throw new ArgumentException("MaxFailedAttempts deve estar entre 1 e 50.");
        if (input.LockoutMinutes is < 1 or > 24 * 60)
            throw new ArgumentException("LockoutMinutes deve estar entre 1 e 1440.");

        var cur = options.CurrentValue;
        var root = new JsonObject
        {
            ["Security"] = new JsonObject
            {
                ["TokenMinutes"] = input.TokenMinutes ?? cur.TokenMinutes,
                ["PasswordMinLength"] = input.PasswordMinLength ?? cur.PasswordMinLength,
                ["RequireStrongPassword"] = input.RequireStrongPassword ?? cur.RequireStrongPassword,
                ["PasswordExpiryDays"] = input.PasswordExpiryDays ?? cur.PasswordExpiryDays,
                ["MaxFailedAttempts"] = input.MaxFailedAttempts ?? cur.MaxFailedAttempts,
                ["LockoutMinutes"] = input.LockoutMinutes ?? cur.LockoutMinutes,
                ["Ldap"] = new JsonObject
                {
                    ["Enabled"] = input.LdapEnabled ?? cur.Ldap.Enabled,
                    ["Host"] = input.LdapHost ?? cur.Ldap.Host ?? "",
                    ["Port"] = input.LdapPort ?? cur.Ldap.Port,
                    ["UseSsl"] = input.LdapUseSsl ?? cur.Ldap.UseSsl,
                    ["TimeoutSeconds"] = input.LdapTimeoutSeconds ?? cur.Ldap.TimeoutSeconds,
                    ["Domain"] = input.LdapDomain ?? cur.Ldap.Domain ?? "",
                    ["UserPrincipalSuffix"] = input.LdapUserPrincipalSuffix ?? cur.Ldap.UserPrincipalSuffix ?? "",
                    ["BindDnTemplate"] = input.LdapBindDnTemplate ?? cur.Ldap.BindDnTemplate ?? "",
                    ["AutoProvision"] = input.LdapAutoProvision ?? cur.Ldap.AutoProvision,
                    ["DefaultGroupName"] = input.LdapDefaultGroupName ?? cur.Ldap.DefaultGroupName ?? "Operadores",
                    ["FallbackOnLocalFailure"] = input.LdapFallbackOnLocalFailure ?? cur.Ldap.FallbackOnLocalFailure
                }
            }
        };

        Directory.CreateDirectory(Path.GetDirectoryName(AbsolutePath)!);
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(AbsolutePath, json, ct);
        log.LogInformation("Overrides de Security/LDAP gravados em {Path}", AbsolutePath);
    }
}

public sealed class SecuritySettingsInput
{
    public int? TokenMinutes { get; set; }
    public int? PasswordMinLength { get; set; }
    public bool? RequireStrongPassword { get; set; }
    public int? PasswordExpiryDays { get; set; }
    public int? MaxFailedAttempts { get; set; }
    public int? LockoutMinutes { get; set; }

    public bool? LdapEnabled { get; set; }
    public string? LdapHost { get; set; }
    public int? LdapPort { get; set; }
    public bool? LdapUseSsl { get; set; }
    public int? LdapTimeoutSeconds { get; set; }
    public string? LdapDomain { get; set; }
    public string? LdapUserPrincipalSuffix { get; set; }
    public string? LdapBindDnTemplate { get; set; }
    public bool? LdapAutoProvision { get; set; }
    public string? LdapDefaultGroupName { get; set; }
    public bool? LdapFallbackOnLocalFailure { get; set; }
}
