using System.Security.Cryptography;

namespace ClubOS.CloudApi.Infrastructure;

/// <summary>Генерация идентификаторов: префикс + GUID v7 (сортируется по времени создания).</summary>
public static class Ids
{
    public static string New(string prefix) => $"{prefix}_{Guid.CreateVersion7():N}";

    /// <summary>Криптостойкий одноразовый секрет (enrollment/refresh token), base64url 256 бит.</summary>
    public static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>SHA-256 секрета в hex — в БД хранятся только хэши секретов (ТЗ §27.3).</summary>
    public static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));
}
