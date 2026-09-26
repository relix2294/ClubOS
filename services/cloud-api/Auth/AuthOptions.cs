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
}
