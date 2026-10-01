using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClubOS.Agent.Core.PlayerShell;

/// <summary>
/// PIN техника для режима обслуживания. На ПК хранится только хэш (PBKDF2-SHA256, соль 16 байт):
/// <c>pbkdf2-sha256$&lt;итерации&gt;$&lt;соль&gt;$&lt;хэш&gt;</c>. Проверку делает служба (SYSTEM), а не SessionHost
/// пользователя, поэтому хэш не читается из пользовательской сессии.
/// </summary>
public static class TechnicianPin
{
    public const int MinLength = 6;
    public const int MaxLength = 12;
    private const string Prefix = "pbkdf2-sha256";
    private const int Iterations = 210_000;
    private const int MaxIterations = 2_000_000;

    public static string? ValidateFormat(string pin) =>
        pin.Length is < MinLength or > MaxLength || !pin.All(char.IsAsciiDigit)
            ? $"PIN — от {MinLength} до {MaxLength} цифр."
            : null;

    public static string Hash(string pin)
    {
        if (ValidateFormat(pin) is { } error)
        {
            throw new ArgumentException(error, nameof(pin));
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return string.Create(CultureInfo.InvariantCulture,
            $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    public static bool Verify(string? storedHash, string pin)
    {
        if (string.IsNullOrWhiteSpace(storedHash) || pin.Length is 0 or > MaxLength)
        {
            return false;
        }

        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) ||
            iterations is < 10_000 or > MaxIterations)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length < 8 || expected.Length is < 16 or > 64)
            {
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations,
                HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
