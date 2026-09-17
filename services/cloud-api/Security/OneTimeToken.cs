using System.Security.Cryptography;
using System.Text;

namespace ClubOS.CloudApi.Security;

/// <summary>
/// Одноразовые enrollment-токены (ТЗ §8 AUTH-007). Токен — высокоэнтропийная случайная
/// строка; в БД хранится только SHA-256-хэш (ТЗ §27.3: секреты не хранятся в открытом виде).
/// </summary>
public static class OneTimeToken
{
    private const int TokenBytes = 32; // 256 бит энтропии

    public static string Generate() =>
        Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

    public static string Hash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(bytes);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
