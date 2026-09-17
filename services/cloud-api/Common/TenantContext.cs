using System.Security.Claims;

namespace ClubOS.CloudApi.Common;

/// <summary>
/// Учётные данные вызывающего, извлечённые из JWT (ТЗ §5, §27.1).
/// Все запросы к данным арендатора обязаны фильтроваться по <see cref="OrganizationId"/>
/// (изоляция арендаторов — ТЗ §6.1).
/// </summary>
public sealed record Caller(string UserId, string OrganizationId, string Email, string Role)
{
    public static Caller? From(ClaimsPrincipal principal)
    {
        var userId = principal.FindFirstValue("sub");
        var org = principal.FindFirstValue("org");
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(org))
        {
            return null;
        }

        var email = principal.FindFirstValue("email") ?? string.Empty;
        var role = principal.FindFirstValue("role") ?? string.Empty;
        return new Caller(userId, org, email, role);
    }
}
