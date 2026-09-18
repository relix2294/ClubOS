using ClubOS.CloudApi.Common;
using ClubOS.CloudApi.Data;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Endpoints;

/// <summary>Локации и их зоны для дашборда Admin Web (ТЗ §9). Изоляция по организации.</summary>
public static class LocationEndpoints
{
    public sealed record ZoneResponse(string Id, string Name);

    public sealed record LocationResponse(
        string Id,
        string Name,
        string Timezone,
        string Currency,
        IReadOnlyList<ZoneResponse> Zones);

    public static void MapLocationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/locations", async (
            ClubOsDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            var caller = Caller.From(http.User);
            if (caller is null)
            {
                return Results.Unauthorized();
            }

            var locations = await db.Locations
                .AsNoTracking()
                .Where(l => l.OrganizationId == caller.OrganizationId)
                .OrderBy(l => l.Name)
                .ToListAsync(ct);

            var zonesByLocation = await db.Zones
                .AsNoTracking()
                .Where(z => locations.Select(l => l.Id).Contains(z.LocationId))
                .ToListAsync(ct);

            var response = locations.Select(l => new LocationResponse(
                l.Id,
                l.Name,
                l.Timezone,
                l.Currency,
                zonesByLocation
                    .Where(z => z.LocationId == l.Id)
                    .OrderBy(z => z.Name)
                    .Select(z => new ZoneResponse(z.Id, z.Name))
                    .ToList()));

            return Results.Ok(response);
        })
            .WithTags("Locations")
            .RequireAuthorization();
    }
}
