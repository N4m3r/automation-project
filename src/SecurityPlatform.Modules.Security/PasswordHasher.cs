using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace SecurityPlatform.Modules.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256, 210.000 iteracoes (recomendacao OWASP 2023).
/// Formato: pbkdf2$&lt;iter&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;
/// </summary>
public class PasswordHasher(IOptionsMonitor<SecurityOptions> options)
{
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    private SecurityOptions _opt => options.CurrentValue;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;

        var iterations = int.Parse(parts[1]);
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        // Comparacao em tempo constante: evita timing attack.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Retorna null se a senha atende a politica; caso contrario, o motivo.</summary>
    public string? Validate(string password)
    {
        if (password.Length < _opt.PasswordMinLength)
            return $"A senha deve ter no minimo {_opt.PasswordMinLength} caracteres.";

        if (!_opt.RequireStrongPassword) return null;

        if (!password.Any(char.IsUpper)) return "A senha deve conter letra maiuscula.";
        if (!password.Any(char.IsLower)) return "A senha deve conter letra minuscula.";
        if (!password.Any(char.IsDigit)) return "A senha deve conter numero.";
        if (password.All(char.IsLetterOrDigit)) return "A senha deve conter caractere especial.";

        return null;
    }

    public static string GenerateStrong(int length = 16)
    {
        const string chars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%&*";
        return new string(RandomNumberGenerator.GetItems<char>(chars, length));
    }
}
