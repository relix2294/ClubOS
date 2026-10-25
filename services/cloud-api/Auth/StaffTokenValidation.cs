using System.Globalization;
using System.Security.Claims;
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
        var db = context.HttpContext.RequestServices.GetRequiredService<ClubOsDbContext>();
        if (context.Principal is null || !await IsCurrentAsync(db, context.Principal, context.HttpContext.RequestAborted))
        {
            context.Fail("token revoked");
        }
    }

    /// <summary>
    /// Токен всё ещё действителен по БД. Используется и для долгих соединений (live-поток), где проверка
    /// при подключении недостаточна: отключённый сотрудник теряет поток при следующей перепроверке.
    /// </summary>
    public static async Task<bool> IsCurrentAsync(ClubOsDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = principal.FindFirst("sub")?.Value;
        var tokenVersion = principal.FindFirst(StaffContext.TokenVersionClaim)?.Value;
        if (userId is null || tokenVersion is null ||
            !int.TryParse(tokenVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
        {
            return false;
        }

        var user = await db.Users.AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.IsActive, x.TokenVersion, x.Role, x.OrganizationId })
            .SingleOrDefaultAsync(ct);

        return user is not null && user.IsActive && user.TokenVersion == version &&
               user.Role == principal.FindFirst("role")?.Value &&
               user.OrganizationId == principal.FindFirst(StaffContext.TenantClaim)?.Value;
    }
}
