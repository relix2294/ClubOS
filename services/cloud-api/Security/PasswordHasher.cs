using System.Security.Cryptography;

namespace ClubOS.CloudApi.Security;

/// <summary>
/// Хэширование паролей PBKDF2-HMAC-SHA256 (встроено в BCL, без внешних зависимостей).
/// ТЗ §27.2 предпочитает Argon2id; на M0 используется PBKDF2 как современный эквивалент
/// (зафиксировано в docs/DEVIATIONS.md). Формат: {iterations}.{saltB64}.{hashB64}.
/// </summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;      // 128 бит
    private const int KeySize = 32;       // 256 бит
    private const int Iterations = 210_000;
    private static readonly HashAlgorithmName Algo = HashAlgorithmName.SHA256;

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algo, KeySize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encoded)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        var parts = encoded.Split('.', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algo, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
