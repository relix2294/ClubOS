using ClubOS.CloudApi.Auth;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.CloudApi.Security;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Api;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth").RequireRateLimiting(RateLimits.Auth);

        group.MapPost("/login", async (LoginRequest request, ClubOsDbContext db, TokenService tokens, AuditWriter audit,
            TimeProvider time, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
            {
                return Problems.Validation("credentials_required", "Укажите email и пароль.");
            }

            var email = request.Email.Trim().ToLowerInvariant();
            var user = await db.Users.SingleOrDefaultAsync(x => x.Email == email, ct);

            // Проверяем хэш даже для несуществующего пользователя — одинаковое время ответа.
            var valid = PasswordHasher.Verify(request.Password, user?.PasswordHash ?? PasswordHasher.DummyHash);
            if (user is null || !valid || !user.IsActive)
            {
                if (user is not null)
                {
                    audit.Write(user.OrganizationId, null, $"user:{user.Id}", "auth.login", $"user:{user.Id}",
                        AuditResults.Denied);
                    await db.SaveChangesAsync(ct);
                }

                return Problems.Unauthorized("Неверный email или пароль.");
            }

            // Прозрачный переход на Argon2id: хэш старого формата заменяется при успешном входе.
            if (PasswordHasher.NeedsRehash(user.PasswordHash))
            {
                user.PasswordHash = PasswordHasher.Hash(request.Password);
            }

            user.LastLoginAtUtc = time.GetUtcNow();
            var issued = await tokens.IssueAsync(user, ct);
            audit.Write(user.OrganizationId, null, $"user:{user.Id}", "auth.login", $"user:{user.Id}", AuditResults.Success);
            await db.SaveChangesAsync(ct);

            return Results.Ok(await BuildResponse(db, user, issued, ct));
        }).AllowAnonymous();

        group.MapPost("/refresh", async (RefreshRequest request, ClubOsDbContext db, TokenService tokens,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                return Problems.Validation("refresh_required", "Нет refresh-токена.");
            }

            var result = await tokens.RefreshAsync(request.RefreshToken, ct);
            return result is null
                ? Problems.Unauthorized("Refresh-токен недействителен.")
                : Results.Ok(await BuildResponse(db, result.Value.User, result.Value.Tokens, ct));
        }).AllowAnonymous();

        group.MapPost("/logout", async (RefreshRequest request, TokenService tokens, CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                await tokens.RevokeAsync(request.RefreshToken, ct);
            }

            return Results.NoContent();
        }).AllowAnonymous();
    }

    private static async Task<LoginResponse> BuildResponse(ClubOsDbContext db, Domain.User user, IssuedTokens issued,
        CancellationToken ct)
    {
        var org = await db.Organizations.AsNoTracking().SingleAsync(x => x.Id == user.OrganizationId, ct);
        return new LoginResponse(issued.AccessToken, issued.AccessExpiresAtUtc, issued.RefreshToken,
            issued.RefreshExpiresAtUtc, user.ToView(org.Name));
    }
}
