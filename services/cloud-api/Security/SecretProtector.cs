using System.Security.Cryptography;
using System.Text;
using ClubOS.CloudApi.Auth;
using Microsoft.Extensions.Options;

namespace ClubOS.CloudApi.Security;

/// <summary>
/// Шифрование секретов в БД (секрет TOTP): AES-256-GCM, ключ — HKDF-SHA256 от <c>Auth:MfaEncryptionKey</c>,
/// а если он не задан — от ключа подписи JWT. Дамп БД без ключа не раскрывает секреты второго фактора.
/// Формат: <c>v1:base64(nonce|ciphertext|tag)</c>. Смена ключа делает сохранённые секреты нечитаемыми —
/// сотрудникам придётся настроить MFA заново (runbook).
/// </summary>
public sealed class SecretProtector
{
    private const string Prefix = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public SecretProtector(IOptions<AuthOptions> options, SigningKeyProvider signingKey)
    {
        var material = string.IsNullOrWhiteSpace(options.Value.MfaEncryptionKey)
            ? signingKey.Key.Key
            : Encoding.UTF8.GetBytes(options.Value.MfaEncryptionKey);
        if (material.Length < 32)
        {
            throw new InvalidOperationException("Auth:MfaEncryptionKey должен быть не короче 32 байт.");
        }

        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, material, 32, info: "clubos-secret-protector-v1"u8.ToArray());
    }

    public string Protect(byte[] plaintext)
    {
        var buffer = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = buffer.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, buffer.AsSpan(NonceSize, plaintext.Length), buffer.AsSpan(NonceSize + plaintext.Length));
        return Prefix + Convert.ToBase64String(buffer);
    }

    /// <summary>null — повреждённое значение или другой ключ.</summary>
    public byte[]? Unprotect(string? value)
    {
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var buffer = Convert.FromBase64String(value[Prefix.Length..]);
            if (buffer.Length < NonceSize + TagSize)
            {
                return null;
            }

            var length = buffer.Length - NonceSize - TagSize;
            var plaintext = new byte[length];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(buffer.AsSpan(0, NonceSize), buffer.AsSpan(NonceSize, length), buffer.AsSpan(NonceSize + length),
                plaintext);
            return plaintext;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
