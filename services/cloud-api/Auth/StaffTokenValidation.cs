using System.Globalization;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Auth;

/// <summary>
/// Проверка access-токена сотрудника по БД на каждом запросе: пользователь существует, активен,
/// версия токенов и роль совпадают. Отключение сотрудника, смена роли или пароля действуют сразу,
/// а не через 15 минут до истечения токена.
/// </summary>
public static class StaffTokenValidation
{
    public static async Task OnTokenValidated(TokenValidatedContext context)
    {
        var principal = context.Principal;
        var userId = principal?.FindFirst("sub")?.Value;
        var tokenVersion = principal?.FindFirst(StaffContext.TokenVersionClaim)?.Value;
        if (userId is null || tokenVersion is null ||
            !int.TryParse(tokenVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
        {
            context.Fail("token without user version");
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<ClubOsDbContext>();
        var user = await db.Users.AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.IsActive, x.TokenVersion, x.Role, x.OrganizationId })
            .SingleOrDefaultAsync(context.HttpContext.RequestAborted);

        if (user is null || !user.IsActive || user.TokenVersion != version ||
            user.Role != principal!.FindFirst("role")?.Value ||
            user.OrganizationId != principal.FindFirst(StaffContext.TenantClaim)?.Value)
        {
            context.Fail("token revoked");
        }
    }
}
