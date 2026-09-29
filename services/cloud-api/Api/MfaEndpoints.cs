using ClubOS.CloudApi.Auth;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.CloudApi.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClubOS.CloudApi.Api;

/// <summary>
/// Управление своей MFA (TOTP) и сброс MFA сотрудника владельцем (ТЗ §8). Настройка доступна и с токеном
/// «нужно настроить MFA» (claim mfs=1), но не с временным паролем: сначала пароль, потом второй фактор.
/// </summary>
public static class MfaEndpoints
{
    public static void MapMfaEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me/mfa").WithTags("Auth").RequireAuthorization(Policies.Staff);
        me.MapGet("", Status);
        // Операции с проверкой кода/пароля — под лимитом попыток входа (перебор кода).
        me.MapPost("/setup", Setup).RequireRateLimiting(RateLimits.Auth);
        me.MapPost("/enable", Enable).RequireRateLimiting(RateLimits.Auth);
        me.MapPost("/disable", Disable).RequireRateLimiting(RateLimits.Auth);
        me.MapPost("/recovery-codes", RegenerateCodes).RequireRateLimiting(RateLimits.Auth);

        app.MapPost("/api/v1/staff/{userId}/reset-mfa", ResetForStaff).WithTags("Staff")
            .RequirePermission(Permissions.StaffManage);
    }

    private static IResult? RejectTemporaryPassword(HttpContext http) =>
        http.User.FindFirst(StaffContext.MustChangePasswordClaim)?.Value == "1"
            ? Problems.Forbidden("Сначала смените временный пароль.")
            : null;

    private static Task<User> CurrentUser(HttpContext http, ClubOsDbContext db, CancellationToken ct)
    {
        var me = StaffContext.From(http.User);
        return db.Users.SingleAsync(x => x.Id == me.UserId && x.OrganizationId == me.TenantId, ct);
    }

    private static async Task<IResult> Status(HttpContext http, ClubOsDbContext db, MfaService mfa,
        IOptions<AuthOptions> options, CancellationToken ct)
    {
        var user = await CurrentUser(http, db, ct);
        return Results.Ok(new MfaStatusView(user.MfaEnabled, options.Value.IsMfaRequired(user.Role),
            user.MfaEnabled ? await mfa.RecoveryCodesLeftAsync(user.Id, ct) : 0, user.MfaEnabledAtUtc));
    }

    private static async Task<IResult> Setup(HttpContext http, ClubOsDbContext db, MfaService mfa, CancellationToken ct)
    {
        if (RejectTemporaryPassword(http) is { } rejected)
        {
            return rejected;
        }

        var user = await CurrentUser(http, db, ct);
        if (user.MfaEnabled)
        {
            return Problems.Conflict("mfa_enabled", "MFA уже включена. Чтобы сменить телефон, отключите и настройте заново.");
        }

        var (secret, uri) = mfa.BeginSetup(user);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new MfaSetupResponse(secret, uri));
    }

    private static async Task<IResult> Enable(MfaCodeRequest request, HttpContext http, ClubOsDbContext db, MfaService mfa,
        TokenService tokens, AuditWriter audit, CancellationToken ct)
    {
        if (RejectTemporaryPassword(http) is { } rejected)
        {
            return rejected;
        }

        var user = await CurrentUser(http, db, ct);
        if (user.MfaEnabled)
        {
            return Problems.Conflict("mfa_enabled", "MFA уже включена.");
        }

        var codes = await mfa.CompleteSetupAsync(user, request.Code, ct);
        if (codes is null)
        {
            audit.Write(user.OrganizationId, null, $"user:{user.Id}", "auth.mfa_enabled", $"user:{user.Id}", AuditResults.Denied);
            await db.SaveChangesAsync(ct);
            return Problems.Validation("invalid_code",
                "Код не подошёл. Проверьте время на телефоне или начните настройку заново (действует 15 минут).");
        }

        // Остальные сессии сотрудника вошли без второго фактора — завершаем их.
        await tokens.RevokeAllAsync(user, ct);
        var issued = await tokens.IssueAsync(user, ct);
        audit.Write(user.OrganizationId, null, $"user:{user.Id}", "auth.mfa_enabled", $"user:{user.Id}", AuditResults.Success);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new MfaEnableResponse(await AuthEndpoints.BuildResponse(db, tokens, user, issued, ct), codes));
    }

    private static async Task<IResult> Disable(MfaDisableRequest request, HttpContext http, ClubOsDbContext db,
        MfaService mfa, TokenService tokens, AuditWriter audit, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var user = await CurrentUser(http, db, ct);
        if (!user.MfaEnabled)
        {
            return Problems.Conflict("mfa_disabled", "MFA не включена.");
        }

        if (options.Value.IsMfaRequired(user.Role))
        {
            return Problems.Conflict("mfa_required", "Для вашей роли MFA обязательна. Сменить телефон поможет владелец (сброс MFA).");
        }

        if (!PasswordHasher.Verify(request.Password ?? string.Empty, user.PasswordHash) ||
            !await mfa.VerifyAsync(user, request.Code, request.RecoveryCode, ct))
        {
            audit.Write(user.OrganizationId, null, $"user:{user.Id}", "auth.mfa_disabled", $"user:{user.Id}", AuditResults.Denied);
            await db.SaveChangesAsync(ct);
            return Problems.Validation("invalid_credentials", "Неверный пароль или код.");
        }

        await mfa.DisableAsync(user, ct);
        await tokens.RevokeAllAsync(user, ct);
        var issued = await tokens.IssueAsync(user, ct);
        audit.Write(user.OrganizationId, null, $"user:{user.Id}", "auth.mfa_disabled", $"user:{user.Id}", AuditResults.Success);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await AuthEndpoints.BuildResponse(db, tokens, user, issued, ct));
    }

    private static async Task<IResult> RegenerateCodes(MfaCodeRequest request, HttpContext http, ClubOsDbContext db,
        MfaService mfa, AuditWriter audit, CancellationToken ct)
    {
        var user = await CurrentUser(http, db, ct);
        if (!user.MfaEnabled || !await mfa.VerifyAsync(user, request.Code, null, ct))
        {
            return Problems.Validation("invalid_code", "Неверный код из приложения.");
        }

        var codes = await mfa.ReplaceRecoveryCodesAsync(user, ct);
        audit.Write(user.OrganizationId, null, $"user:{user.Id}", "auth.mfa_recovery_codes", $"user:{user.Id}",
            AuditResults.Success);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new RecoveryCodesResponse(codes));
    }

    /// <summary>Сотрудник потерял телефон и коды: владелец сбрасывает MFA, все сессии сотрудника завершаются.</summary>
    private static async Task<IResult> ResetForStaff(string userId, HttpContext http, ClubOsDbContext db, MfaService mfa,
        TokenService tokens, AuditWriter audit, CancellationToken ct)
    {
        var me = StaffContext.From(http.User);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId && x.OrganizationId == me.TenantId, ct);
        if (user is null)
        {
            return Problems.NotFound("Сотрудник");
        }

        if (user.Id == me.UserId)
        {
            return Problems.Conflict("self_reset", "Свою MFA меняйте в разделе «Мой пароль и вход».");
        }

        await mfa.DisableAsync(user, ct);
        await tokens.RevokeAllAsync(user, ct);
        audit.Write(me.TenantId, null, me.Actor, "staff.mfa_reset", $"user:{user.Id}", AuditResults.Success);
        await db.SaveChangesAsync(ct);
        return Results.Ok(user.ToStaffView());
    }
}
