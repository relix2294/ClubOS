using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClubOS.CloudApi.Security;

/// <summary>
/// TOTP (RFC 6238, HMAC-SHA1, 30 с, 6 цифр) — совместим с Google Authenticator, Microsoft Authenticator,
/// Aegis, 1Password. Допуск ±1 шаг на расхождение часов телефона; код одного шага принимается один раз.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const int SecretBytes = 20;

    /// <summary>Сколько шагов назад/вперёд допустимо (часы телефона ±30 с).</summary>
    public const int Window = 1;

    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Проверяет код. Возвращает шаг, которому соответствует код, или null. Коды шагов ≤ <paramref name="lastUsedStep"/>
    /// отклоняются — перехваченный код нельзя использовать повторно (RFC 6238 §5.2).
    /// </summary>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now, long lastUsedStep)
    {
        var normalized = Normalize(code);
        if (normalized is null)
        {
            return null;
        }

        var current = StepAt(now);
        for (var step = current - Window; step <= current + Window; step++)
        {
            if (step > lastUsedStep &&
                CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(normalized)))
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>Пробелы, которые приложения вставляют для читаемости («123 456»), допускаются.</summary>
    public static string? Normalize(string? code)
    {
        if (code is null)
        {
            return null;
        }

        var digits = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return digits.Length == Digits && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    public static string OtpAuthUri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}

/// <summary>Base32 (RFC 4648) без паддинга — формат секретов TOTP.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }

    public static byte[] Decode(string text)
    {
        var clean = text.Trim().TrimEnd('=').Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException("Недопустимый символ Base32.");
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
