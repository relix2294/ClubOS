using ClubOS.CloudApi.Auth;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Security;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Endpoints;

/// <summary>Аутентификация (ТЗ §8 AUTH-001, §27.1): login и refresh.</summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");

        group.MapPost("/login", async (
            LoginRequest request,
            ClubOsDbContext db,
            JwtTokenService tokens,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new { error = "email и password обязательны." });
            }

            var user = await db.Users
                .AsNoTracking()
                .SingleOrDefaultAsync(u => u.Email == request.Email, ct);

            // Единый ответ на «нет пользователя» и «неверный пароль» — без утечки существования (ТЗ §27.1).
            var passwordOk = user is not null
                && user.IsActive
                && PasswordHasher.Verify(request.Password, user.PasswordHash);

            if (!passwordOk || user is null)
            {
                return Results.Json(new { error = "Неверный email или пароль." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(tokens.IssueFor(user, DateTimeOffset.UtcNow));
        });

        group.MapPost("/refresh", async (
            RefreshRequest request,
            ClubOsDbContext db,
            JwtTokenService tokens,
            CancellationToken ct) =>
        {
            var validated = tokens.ValidateRefreshToken(request.RefreshToken);
            if (validated is null)
            {
                return Results.Json(new { error = "refresh-токен недействителен." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var user = await db.Users
                .AsNoTracking()
                .SingleOrDefaultAsync(u => u.Id == validated.Value.UserId, ct);

            if (user is null || !user.IsActive || user.OrganizationId != validated.Value.OrganizationId)
            {
                return Results.Json(new { error = "Пользователь недоступен." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(tokens.IssueFor(user, DateTimeOffset.UtcNow));
        });
    }
}
