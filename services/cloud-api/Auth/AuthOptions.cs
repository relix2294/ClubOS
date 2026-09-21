namespace ClubOS.CloudApi.Auth;

/// <summary>
/// Настройки выпуска/проверки JWT (секция "Auth" в конфигурации).
/// Ключ подписи в проде берётся из секрета окружения, НЕ из appsettings (ТЗ §27.2, §27.3).
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string Issuer { get; set; } = "clubos-cloud-api";
    public string Audience { get; set; } = "clubos-admin-web";

    /// <summary>Симметричный ключ HS256; минимум 32 байта. Dev-значение только для локальной среды.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 14;
}
