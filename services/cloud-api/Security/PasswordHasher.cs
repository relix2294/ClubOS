using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace ClubOS.CloudApi.Security;

/// <summary>
/// Хэширование паролей персонала: Argon2id (ТЗ §27.2), параметры OWASP (m=19 MiB, t=2, p=1).
/// Формат: <c>argon2id$v=19$m=19456,t=2,p=1$&lt;saltB64&gt;$&lt;hashB64&gt;</c>.
/// Хэши M0 (PBKDF2-SHA256, формат <c>{iterations}.{salt}.{hash}</c>) по-прежнему проверяются
/// и прозрачно перехэшируются в Argon2id при следующем успешном входе (<see cref="NeedsRehash"/>).
/// </summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int MemoryKib = 19_456;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const string Prefix = "argon2id$";

    /// <summary>Хэш случайного пароля — для выравнивания времени ответа при неизвестном email.</summary>
    public static string DummyHash { get; } = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)));

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Argon2(password, salt, MemoryKib, Iterations, Parallelism, HashSize);
        return $"{Prefix}v=19$m={MemoryKib},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encoded)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        return encoded.StartsWith(Prefix, StringComparison.Ordinal)
            ? VerifyArgon2(password, encoded)
            : VerifyLegacyPbkdf2(password, encoded);
    }

    /// <summary>true — хэш старого формата или с устаревшими параметрами: перехэшировать после входа.</summary>
    public static bool NeedsRehash(string encoded) =>
        !encoded.StartsWith($"{Prefix}v=19$m={MemoryKib},t={Iterations},p={Parallelism}$", StringComparison.Ordinal);

    private static bool VerifyArgon2(string password, string encoded)
    {
        // argon2id $ v=19 $ m=..,t=..,p=.. $ salt $ hash
        var parts = encoded.Split('$');
        if (parts.Length != 5)
        {
            return false;
        }

        int memory = 0, iterations = 0, parallelism = 0;
        foreach (var kv in parts[2].Split(','))
        {
            var pair = kv.Split('=', 2);
            if (pair.Length != 2 || !int.TryParse(pair[1], out var value))
            {
                return false;
            }

            switch (pair[0])
            {
                case "m": memory = value; break;
                case "t": iterations = value; break;
                case "p": parallelism = value; break;
            }
        }

        // Защита от «ядовитых» параметров в БД (DoS по памяти/времени).
        if (memory is < 8_192 or > 262_144 || iterations is < 1 or > 10 || parallelism is < 1 or > 8)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Argon2(password, salt, memory, iterations, parallelism, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool VerifyLegacyPbkdf2(string password, string encoded)
    {
        var parts = encoded.Split('.', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations) || iterations is < 10_000 or > 5_000_000)
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

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Argon2(string password, byte[] salt, int memoryKib, int iterations, int parallelism, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism
        };
        return argon.GetBytes(length);
    }
}
