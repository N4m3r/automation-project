using Microsoft.AspNetCore.DataProtection;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Modules.Security;

/// <summary>
/// Cifra segredos com o Data Protection do ASP.NET Core (AES-256-CBC + HMAC).
///
/// A chave mestra vive no diretório de chaves configurado, fora do banco: um
/// dump do banco, sozinho, não revela nenhuma senha de equipamento.
/// Em topologia multi-node o diretório precisa ser compartilhado, caso
/// contrário cada nó gera a própria chave e não lê o que o outro cifrou.
/// </summary>
public sealed class SecretProtector : ISecretProtector
{
    /// <summary>Prefixo de versão: distingue cifrado de legado em claro.</summary>
    private const string Prefix = "enc:v1:";

    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("SecurityPlatform.DeviceSecrets.v1");

    public bool IsProtected(string? value)
        => !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        if (IsProtected(plaintext)) return plaintext;          // idempotente
        return Prefix + _protector.Protect(plaintext);
    }

    public string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;

        // Sem prefixo = gravado antes da criptografia. Devolve como está para
        // o sistema seguir funcionando; a migração de startup regrava cifrado.
        if (!IsProtected(stored)) return stored;

        try
        {
            return _protector.Unprotect(stored[Prefix.Length..]);
        }
        catch (Exception)
        {
            // Chave trocada ou perdida: devolver vazio faz o dispositivo cair
            // como "credencial inválida", que é o comportamento seguro — melhor
            // do que derrubar a aplicação inteira.
            return string.Empty;
        }
    }
}
