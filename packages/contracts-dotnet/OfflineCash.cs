using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClubOS.Contracts;

/// <summary>
/// PIN офлайн-кассы (D-023): 6–8 цифр, отдельно от пароля. Хэш PBKDF2-SHA256 уходит на Edge клуба в конфигурации,
/// чтобы кассир мог войти в кассу Edge без интернета. Формат: <c>pbkdf2-sha256$итерации$соль$хэш</c>.
/// </summary>
public static class OfflinePin
{
    public const int Iterations = 210_000;
    private const string Prefix = "pbkdf2-sha256";

    public static bool IsValidFormat(string? pin) =>
        pin is { Length: >= 6 and <= 8 } && pin.All(char.IsAsciiDigit) && !IsTrivial(pin);

    /// <summary>Одинаковые цифры и простые последовательности (123456, 654321) запрещены.</summary>
    public static bool IsTrivial(string pin) =>
        pin.Distinct().Count() == 1 || "0123456789012345".Contains(pin, StringComparison.Ordinal) ||
        "9876543210987654".Contains(pin, StringComparison.Ordinal);

    public static string Hash(string pin, int iterations = Iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"{Prefix}${iterations.ToString(CultureInfo.InvariantCulture)}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string pin, string? encoded)
    {
        var parts = encoded?.Split('$');
        if (parts is not [Prefix, var iterText, var saltText, var hashText] ||
            !int.TryParse(iterText, NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) ||
            iterations is < 1_000 or > 5_000_000)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(saltText);
            var expected = Convert.FromBase64String(hashText);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Сотрудник, который может принимать оплату в кассе Edge без интернета.</summary>
public sealed record EdgeOfflineStaff
{
    public required string UserId { get; init; }
    public required string DisplayName { get; init; }
    public required string PinHash { get; init; }
}

/// <summary>Cloud → Edge: сколько оплачено по сессии в Cloud (для офлайн-долга на Edge).</summary>
public sealed record CashSyncCommand
{
    public required string SessionId { get; init; }
    public required long PaidMinorUnits { get; init; }
}

/// <summary>Edge → Cloud: оплата, принятая в кассе Edge (наличные или карта). Cloud проводит её в смену.</summary>
public sealed record OfflinePaymentRecordedPayload
{
    public required string PaymentId { get; init; }
    public required string SessionId { get; init; }
    public required string DeviceId { get; init; }
    public required long AmountMinorUnits { get; init; }

    /// <summary>Cash или Card.</summary>
    public required string Method { get; init; }

    /// <summary>Сотрудник Cloud, вошедший в кассу Edge по PIN.</summary>
    public required string UserId { get; init; }
    public required DateTimeOffset RecordedAtUtc { get; init; }
}
