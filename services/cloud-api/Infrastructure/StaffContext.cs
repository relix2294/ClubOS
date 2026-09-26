using System.Security.Claims;

namespace ClubOS.CloudApi.Infrastructure;

/// <summary>Контекст сотрудника из проверенного JWT. Tenant берётся ТОЛЬКО из токена (AUTH-001).</summary>
public sealed record StaffContext(string UserId, string TenantId, string Role, string Email)
{
    public const string TenantClaim = "org";

    public string Actor => $"user:{UserId}";

    public static StaffContext From(ClaimsPrincipal principal)
    {
        var userId = principal.FindFirstValue("sub")
                     ?? throw new InvalidOperationException("JWT без sub.");
        var tenant = principal.FindFirstValue(TenantClaim)
                     ?? throw new InvalidOperationException("JWT без org.");
        return new StaffContext(userId, tenant,
            principal.FindFirstValue("role") ?? string.Empty,
            principal.FindFirstValue("email") ?? string.Empty);
    }
}

/// <summary>Контекст аутентифицированного Edge (по подписанному токену и сертификату dev CA).</summary>
public sealed record EdgeContext(string EdgeId, string TenantId, string LocationId)
{
    public const string EdgeIdClaim = "edge_id";
    public const string LocationClaim = "location_id";

    public string Actor => $"edge:{EdgeId}";

    public static EdgeContext From(ClaimsPrincipal principal) => new(
        principal.FindFirstValue(EdgeIdClaim) ?? throw new InvalidOperationException("Нет edge_id."),
        principal.FindFirstValue(StaffContext.TenantClaim) ?? throw new InvalidOperationException("Нет org."),
        principal.FindFirstValue(LocationClaim) ?? throw new InvalidOperationException("Нет location_id."));
}
