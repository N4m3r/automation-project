using System.Text.RegularExpressions;

namespace SecurityPlatform.Core.Drivers;

/// <summary>
/// Remove credenciais de URLs antes de qualquer saída legível por pessoas.
///
/// Necessário porque a URL RTSP carrega usuário e senha da câmera
/// (<c>rtsp://user:senha@host/...</c>), e ferramentas externas como o FFmpeg
/// repetem a URL inteira nas mensagens de erro. Sem mascarar, a senha do
/// equipamento vaza para o arquivo de log.
/// </summary>
public static partial class UrlMasking
{
    [GeneratedRegex(@"(?<scheme>[a-zA-Z][a-zA-Z0-9+.-]*://)(?<cred>[^/@\s]+)@",
        RegexOptions.Compiled)]
    private static partial Regex CredentialsInUrl();

    /// <summary>Troca <c>scheme://user:senha@</c> por <c>scheme://***:***@</c>.</summary>
    public static string Mask(string? text)
        => string.IsNullOrEmpty(text)
            ? text ?? string.Empty
            : CredentialsInUrl().Replace(text, "${scheme}***:***@");
}
