namespace ClubOS.CloudApi.Auth;

/// <summary>Запрос входа (Admin Web → Cloud). ТЗ §8 AUTH-001.</summary>
public sealed record LoginRequest
{
    public required string Email { get; init; }
    public required string Password { get; init; }
}

/// <summary>Запрос обновления пары токенов по refresh-токену.</summary>
public sealed record RefreshRequest
{
    public required string RefreshToken { get; init; }
}

/// <summary>Пара токенов, выдаваемая при входе/обновлении.</summary>
public sealed record TokenResponse
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTimeOffset AccessTokenExpiresAtUtc { get; init; }
    public required string Role { get; init; }
}
