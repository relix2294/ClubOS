using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ClubOS.CloudApi.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ClubOS.CloudApi.Auth;

/// <summary>
/// Выпуск и проверка JWT (ТЗ §27.1). Access-токен — короткоживущий; refresh-токен —
/// самостоятельный JWT с бо́льшим TTL и меткой typ=refresh (без обращения к БД).
/// Отзыв refresh-токенов в M0 не реализован — зафиксировано в docs/DEVIATIONS.md.
/// </summary>
public sealed class JwtTokenService
{
    private const string TokenTypeClaim = "typ";
    private const string AccessTokenType = "access";
    private const string RefreshTokenType = "refresh";

    private readonly AuthOptions _options;
    private readonly SymmetricSecurityKey _key;
    private readonly JwtSecurityTokenHandler _handler = new();

    public JwtTokenService(IOptions<AuthOptions> options)
    {
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.SigningKey) || Encoding.UTF8.GetByteCount(_options.SigningKey) < 32)
        {
            throw new InvalidOperationException(
                "Auth:SigningKey не задан или короче 32 байт. Задайте секрет через окружение (CLUBOS_JWT_KEY).");
        }

        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
    }

    public TokenResponse IssueFor(User user, DateTimeOffset now)
    {
        var accessExpires = now.AddMinutes(_options.AccessTokenMinutes);
        var access = Write(user, AccessTokenType, now, accessExpires);
        var refresh = Write(user, RefreshTokenType, now, now.AddDays(_options.RefreshTokenDays));

        return new TokenResponse
        {
            AccessToken = access,
            RefreshToken = refresh,
            AccessTokenExpiresAtUtc = accessExpires,
            Role = user.Role,
        };
    }

    /// <summary>
    /// Проверяет refresh-токен и возвращает (userId, organizationId) при успехе.
    /// null — если токен невалиден, просрочен или это не refresh-токен.
    /// </summary>
    public (string UserId, string OrganizationId)? ValidateRefreshToken(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _key,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        try
        {
            var principal = _handler.ValidateToken(refreshToken, parameters, out _);
            if (principal.FindFirstValue(TokenTypeClaim) != RefreshTokenType)
            {
                return null;
            }

            var userId = principal.FindFirstValue("sub");
            var org = principal.FindFirstValue("org");
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(org))
            {
                return null;
            }

            return (userId, org);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }

    private string Write(User user, string tokenType, DateTimeOffset now, DateTimeOffset expires)
    {
        var claims = new List<Claim>
        {
            new("sub", user.Id),
            new("org", user.OrganizationId),
            new("email", user.Email),
            new("role", user.Role),
            new(TokenTypeClaim, tokenType),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));

        return _handler.WriteToken(token);
    }
}
