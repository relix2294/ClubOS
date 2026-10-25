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

        group.MapPost("/login", async (LoginRequest request, ClubOsDbContext db, TokenService tokens, MfaService mfa,
            AuditWriter audit, TimeProvider time, CancellationToken ct) =>
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

            // Второй фактор: пароль верный, токены выдаст /auth/mfa после кода из приложения.
            if (user.MfaEnabled)
            {
                await mfa.PurgeExpiredChallengesAsync(ct);
                var (challenge, token) = mfa.CreateChallenge(user);
                audit.Write(user.OrganizationId, null, $"user:{user.Id}", "auth.login", $"user:{user.Id}",
                    AuditResults.Requested, details: new { step = "mfa" });
                await db.SaveChangesAsync(ct);
                return Results.Ok(new MfaChallengeResponse(true, token, challenge.ExpiresAtUtc));
            }

            user.LastLoginAtUtc = time.GetUtcNow();
            var issued = await tokens.IssueAsync(user, ct);
            audit.Write(user.OrganizationId, null, $"user:{user.Id}", "auth.login", $"user:{user.Id}", AuditResults.Success);
            await db.SaveChangesAsync(ct);

            return Results.Ok(await BuildResponse(db, tokens, user, issued, ct));
        }).AllowAnonymous();

        group.MapPost("/mfa", async (MfaLoginRequest request, ClubOsDbContext db, TokenService tokens, MfaService mfa,
            AuditWriter audit, TimeProvider time, CancellationToken ct) =>
        {
            var (result, user) = await mfa.CompleteChallengeAsync(request.MfaToken, request.Code, request.RecoveryCode, ct);
            if (result != MfaCheck.Ok || user is null)
            {
                if (user is not null)
                {
                    audit.Write(user.OrganizationId, null, $"user:{user.Id}", "auth.mfa", $"user:{user.Id}",
                        AuditResults.Denied, details: new { result = result.ToString() });
                }

                await db.SaveChangesAsync(ct);
                return result switch
                {
                    MfaCheck.Invalid => Problems.Unauthorized("Неверный код подтверждения."),
                    MfaCheck.TooManyAttempts => Problems.Unauthorized("Слишком много неверных кодов. Войдите заново."),
                    _ => Problems.Unauthorized("Срок подтверждения истёк. Войдите заново.")
                };
            }

            user.LastLoginAtUtc = time.GetUtcNow();
            var issued = await tokens.IssueAsync(user, ct);
            audit.Write(user.OrganizationId, null, $"user:{user.Id}", "auth.login", $"user:{user.Id}", AuditResults.Success,
                details: new { mfa = string.IsNullOrWhiteSpace(request.RecoveryCode) ? "totp" : "recovery_code" });
            await db.SaveChangesAsync(ct);
            return Results.Ok(await BuildResponse(db, tokens, user, issued, ct));
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
                : Results.Ok(await BuildResponse(db, tokens, result.Value.User, result.Value.Tokens, ct));
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

    public static async Task<LoginResponse> BuildResponse(ClubOsDbContext db, TokenService tokens, Domain.User user,
        IssuedTokens issued, CancellationToken ct)
    {
        var org = await db.Organizations.AsNoTracking().SingleAsync(x => x.Id == user.OrganizationId, ct);
        return new LoginResponse(issued.AccessToken, issued.AccessExpiresAtUtc, issued.RefreshToken,
            issued.RefreshExpiresAtUtc, user.ToView(org.Name, tokens.MfaSetupRequired(user)));
    }
}
