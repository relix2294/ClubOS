namespace ClubOS.CloudApi.Auth;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Ключ подписи JWT (HS256), минимум 32 байта. Только из env/секретов, не из git.</summary>
    public string? SigningKey { get; set; }

    public string Issuer { get; set; } = "clubos-cloud";
    public string Audience { get; set; } = "clubos-admin";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>
    /// Роли, для которых MFA (TOTP) обязательна (ТЗ §8), через запятую. Без настроенной MFA сотрудник такой роли
    /// после входа может только настроить её. Пустая строка — MFA добровольная (dev-стек, e2e).
    /// </summary>
    public string MfaRequiredRoles { get; set; } = "Owner,Admin";

    /// <summary>Ключ шифрования секретов TOTP в БД (≥32 байт); по умолчанию выводится из SigningKey.</summary>
    public string? MfaEncryptionKey { get; set; }

    /// <summary>Имя в приложении-аутентификаторе.</summary>
    public string MfaIssuer { get; set; } = "ClubOS";

    public bool IsMfaRequired(string role) =>
        MfaRequiredRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(role, StringComparer.Ordinal);
}
