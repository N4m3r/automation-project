namespace SecurityPlatform.Core.Security;

/// <summary>
/// Cifra segredos operacionais em repouso — senhas de câmera, SMTP e afins.
///
/// Diferente da senha de usuário (que usa hash irreversível), estes segredos
/// precisam ser recuperados em claro para autenticar no equipamento, então a
/// proteção é criptografia reversível com chave fora do banco.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Cifra. Texto vazio permanece vazio.</summary>
    string Protect(string? plaintext);

    /// <summary>
    /// Decifra. Valores gravados antes da criptografia (sem o prefixo de versão)
    /// são devolvidos como estão, o que permite migração sem downtime.
    /// </summary>
    string Unprotect(string? stored);

    /// <summary>True quando o valor já está cifrado.</summary>
    bool IsProtected(string? value);
}

/// <summary>
/// Implementação nula: usada apenas em contextos sem proteção configurada
/// (ex.: ferramentas de linha de comando). Não cifra nada.
/// </summary>
public sealed class NullSecretProtector : ISecretProtector
{
    public string Protect(string? plaintext) => plaintext ?? string.Empty;
    public string Unprotect(string? stored) => stored ?? string.Empty;
    public bool IsProtected(string? value) => false;
}
