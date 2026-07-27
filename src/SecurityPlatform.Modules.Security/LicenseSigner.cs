using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SecurityPlatform.Core.Domain;

namespace SecurityPlatform.Modules.Security;

/// <summary>
/// Licença assinada (HMAC-SHA256) no formato <c>payloadBase64url.assinaturaBase64url</c>.
///
/// Sem chave de assinatura configurada, o modo legado (campos livres no POST)
/// continua aceito — útil em desenvolvimento. Em produção, defina
/// <c>Security:LicenseSigningKey</c> (ou reutilize JwtKey) e só chaves
/// assinadas passam.
/// </summary>
public class LicenseSigner(IOptions<SecurityOptions> options)
{
    private readonly SecurityOptions _opt = options.Value;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Emite uma chave assinada a partir dos campos da licença (uso interno/gerador).</summary>
    public string Issue(LicensePayload payload)
    {
        var key = SigningKey();
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var body = Base64Url(Encoding.UTF8.GetBytes(json));
        var sig = Base64Url(Hmac(key, body));
        return $"{body}.{sig}";
    }

    /// <summary>
    /// Tenta interpretar <paramref name="key"/> como licença assinada.
    /// Retorna null se o formato não for assinado (modo legado).
    /// Lança se for assinado mas inválido.
    /// </summary>
    public LicensePayload? TryValidate(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var parts = key.Trim().Split('.');
        if (parts.Length != 2) return null; // legado: texto livre

        var body = parts[0];
        var sig = parts[1];

        var signing = SigningKey();
        if (string.IsNullOrEmpty(signing))
            throw new InvalidOperationException(
                "Chave de licença assinada recebida, mas Security:LicenseSigningKey (ou JwtKey) não está configurada.");

        var esperado = Base64Url(Hmac(signing, body));
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(esperado),
                Encoding.UTF8.GetBytes(sig)))
            throw new InvalidOperationException("Assinatura da licença inválida.");

        var json = Encoding.UTF8.GetString(FromBase64Url(body));
        var payload = JsonSerializer.Deserialize<LicensePayload>(json, JsonOpts)
            ?? throw new InvalidOperationException("Payload da licença inválido.");

        if (payload.ExpiresAt is { } exp && exp < DateTime.UtcNow)
            throw new InvalidOperationException("Licença expirada.");

        return payload;
    }

    /// <summary>True se a instalação exige chave assinada (chave de assinatura definida e Enforce assinado).</summary>
    public bool RequiresSigned =>
        _opt.RequireSignedLicense && !string.IsNullOrWhiteSpace(SigningKey());

    private string SigningKey()
        => !string.IsNullOrWhiteSpace(_opt.LicenseSigningKey)
            ? _opt.LicenseSigningKey
            : _opt.JwtKey;

    private static byte[] Hmac(string key, string data)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return h.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}

public record LicensePayload(
    string Edition = "Express",
    string CustomerName = "",
    int VideoChannels = 4,
    int AccessPoints = 0,
    int AlarmZones = 0,
    bool Failover = false,
    bool MultiTenant = false,
    bool AnalyticsLpr = false,
    bool AnalyticsFacial = false,
    DateTime? ExpiresAt = null);
