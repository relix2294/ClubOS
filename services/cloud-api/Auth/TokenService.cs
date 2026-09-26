using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ClubOS.CloudApi.Auth;

/// <summary>Выпуск access JWT и ротация refresh-токенов (минимальная реальная аутентификация, D-004).</summary>
public sealed class TokenService(ClubOsDbContext db, IOptions<AuthOptions> options, SigningKeyProvider keys, TimeProvider time)
{
    private readonly AuthOptions _options = options.Value;

    public async Task<IssuedTokens> IssueAsync(User user, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var accessExpires = now.AddMinutes(_options.AccessTokenMinutes);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(StaffContext.TenantClaim, user.OrganizationId),
            new Claim("role", user.Role),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: accessExpires.UtcDateTime,
            signingCredentials: new SigningCredentials(keys.Key, SecurityAlgorithms.HmacSha256));

        var refreshSecret = Ids.NewSecret();
        var refresh = new RefreshToken
        {
            Id = Ids.New("rt"),
            UserId = user.Id,
            TokenHash = Ids.HashSecret(refreshSecret),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(_options.RefreshTokenDays)
        };
        db.RefreshTokens.Add(refresh);
        await db.SaveChangesAsync(ct);

        return new IssuedTokens(new JwtSecurityTokenHandler().WriteToken(token), accessExpires, refreshSecret,
            refresh.ExpiresAtUtc, refresh.Id);
    }

    /// <summary>
    /// Ротация: старый refresh отзывается, выдаётся новый. Повторное использование отозванного токена
    /// (признак кражи) отзывает всю цепочку пользователя.
    /// </summary>
    public async Task<(User User, IssuedTokens Tokens)?> RefreshAsync(string refreshSecret, CancellationToken ct)
    {
        var hash = Ids.HashSecret(refreshSecret);
        var stored = await db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        var now = time.GetUtcNow();
        if (stored is null)
        {
            return null;
        }

        if (stored.RevokedAtUtc is not null)
        {
            await db.RefreshTokens.Where(x => x.UserId == stored.UserId && x.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, now), ct);
            return null;
        }

        if (stored.ExpiresAtUtc <= now)
        {
            return null;
        }

        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == stored.UserId && x.IsActive, ct);
        if (user is null)
        {
            return null;
        }

        stored.RevokedAtUtc = now;
        var tokens = await IssueAsync(user, ct);
        stored.ReplacedById = tokens.RefreshTokenId;
        await db.SaveChangesAsync(ct);
        return (user, tokens);
    }

    public async Task RevokeAsync(string refreshSecret, CancellationToken ct)
    {
        var hash = Ids.HashSecret(refreshSecret);
        var now = time.GetUtcNow();
        await db.RefreshTokens.Where(x => x.TokenHash == hash && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, now), ct);
    }
}

public sealed record IssuedTokens(string AccessToken, DateTimeOffset AccessExpiresAtUtc, string RefreshToken,
    DateTimeOffset RefreshExpiresAtUtc, string RefreshTokenId);

/// <summary>
/// Ключ подписи JWT. Если Auth:SigningKey не задан (только Development) — генерируется случайный
/// ключ на время жизни процесса (токены станут невалидны после рестарта). В Production обязателен.
/// </summary>
public sealed class SigningKeyProvider
{
    public SigningKeyProvider(IOptions<AuthOptions> options, IHostEnvironment env, ILogger<SigningKeyProvider> logger)
    {
        var configured = options.Value.SigningKey;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var bytes = Encoding.UTF8.GetBytes(configured);
            if (bytes.Length < 32)
            {
                throw new InvalidOperationException("Auth:SigningKey должен быть не короче 32 байт.");
            }

            Key = new SymmetricSecurityKey(bytes);
            return;
        }

        if (!env.IsDevelopment())
        {
            throw new InvalidOperationException("Auth:SigningKey обязателен вне Development (env CLUBOS_Auth__SigningKey).");
        }

        logger.LogWarning("Auth:SigningKey не задан — используется временный ключ процесса (только Development).");
        Key = new SymmetricSecurityKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    }

    public SymmetricSecurityKey Key { get; }
}
