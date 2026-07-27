using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Assinatura HMAC-SHA256 de arquivos exportados (prova de integridade).
/// Chave: Security:LicenseSigningKey ou Security:JwtKey.
/// </summary>
public sealed class ExportSigner(IConfiguration config)
{
    public string? SignFile(string path)
    {
        var sig = ComputeSignature(path);
        if (sig is null || !File.Exists(path)) return null;

        try { File.WriteAllText(path + ".sig", sig); }
        catch (IOException) { /* sidecar opcional */ }

        return sig;
    }

    /// <summary>Calcula HMAC sem gravar sidecar.</summary>
    public string? ComputeSignature(string path)
    {
        var key = ResolveKey();
        if (key is null || !File.Exists(path)) return null;

        using var fs = File.OpenRead(path);
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(fs);
        return Convert.ToBase64String(hash);
    }

    /// <summary>Valida assinatura em memória ou sidecar .sig.</summary>
    public bool Verify(string path, string? expectedSignature = null)
    {
        var actual = ComputeSignature(path);
        if (actual is null) return false;

        if (!string.IsNullOrWhiteSpace(expectedSignature)
            && string.Equals(actual, expectedSignature.Trim(), StringComparison.Ordinal))
            return true;

        var sigFile = path + ".sig";
        if (!File.Exists(sigFile)) return false;
        var onDisk = File.ReadAllText(sigFile).Trim();
        return string.Equals(actual, onDisk, StringComparison.Ordinal);
    }

    private byte[]? ResolveKey()
    {
        var material = config["Security:LicenseSigningKey"];
        if (string.IsNullOrWhiteSpace(material))
            material = config["Security:JwtKey"];
        if (string.IsNullOrWhiteSpace(material) || material.Length < 16)
            return null;
        return SHA256.HashData(Encoding.UTF8.GetBytes(material));
    }
}
