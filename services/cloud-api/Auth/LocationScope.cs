using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Auth;

/// <summary>
/// Какие локации доступны текущему сотруднику (ТЗ §8, D-013). Owner и сотрудники с «все локации» видят все
/// локации своей организации, остальные — только назначенные. Недоступная локация выглядит так же, как чужая:
/// 404, без подсказки о существовании. Загружается один раз на запрос, читается из БД — изменение доступа
/// действует со следующего запроса.
/// </summary>
public sealed class LocationScope(ClubOsDbContext db, IHttpContextAccessor http)
{
    private LocationAccess? _access;

    public async Task<LocationAccess> GetAsync(CancellationToken ct)
    {
        if (_access is not null)
        {
            return _access;
        }

        var staff = StaffContext.From(http.HttpContext!.User);
        _access = await LoadAsync(db, staff.UserId, staff.TenantId, ct);
        return _access;
    }

    public async Task<bool> CanAccessAsync(string locationId, CancellationToken ct) =>
        (await GetAsync(ct)).Contains(locationId);

    public static async Task<LocationAccess> LoadAsync(ClubOsDbContext db, string userId, string tenantId,
        CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(x => x.Id == userId && x.OrganizationId == tenantId)
            .Select(x => new { x.Role, x.AllLocations }).SingleOrDefaultAsync(ct);
        var tenantLocations = await db.Locations.AsNoTracking().Where(x => x.OrganizationId == tenantId)
            .Select(x => x.Id).ToListAsync(ct);
        if (user is null)
        {
            return new LocationAccess(false, new HashSet<string>());
        }

        if (user.Role == Roles.Owner || user.AllLocations)
        {
            return new LocationAccess(true, tenantLocations.ToHashSet());
        }

        var assigned = await db.StaffLocationAccess.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => x.LocationId).ToListAsync(ct);
        return new LocationAccess(false, assigned.Intersect(tenantLocations).ToHashSet());
    }
}

/// <param name="All">Доступ ко всей организации: видны и события без локации (персонал, входы).</param>
public sealed record LocationAccess(bool All, IReadOnlySet<string> LocationIds)
{
    public bool Contains(string? locationId) => locationId is null ? All : LocationIds.Contains(locationId);
}
