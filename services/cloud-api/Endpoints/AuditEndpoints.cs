using ClubOS.CloudApi.Api;
using ClubOS.CloudApi.Common;
using ClubOS.CloudApi.Data;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Endpoints;

/// <summary>Timeline аудита (ТЗ §27.4). Только события своей организации, новые сверху.</summary>
public static class AuditEndpoints
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/audit").WithTags("Audit").RequireAuthorization();

        group.MapGet("/", async (
            string? locationId,
            int? limit,
            ClubOsDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            var caller = Caller.From(http.User);
            if (caller is null)
            {
                return Results.Unauthorized();
            }

            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

            var query = db.AuditEvents
                .AsNoTracking()
                .Where(a => a.TenantId == caller.OrganizationId);

            if (!string.IsNullOrEmpty(locationId))
            {
                query = query.Where(a => a.LocationId == locationId);
            }

            var events = await query
                .OrderByDescending(a => a.OccurredAtUtc)
                .Take(take)
                .ToListAsync(ct);

            var response = events.Select(a => new AuditResponse
            {
                Id = a.Id,
                OccurredAtUtc = a.OccurredAtUtc,
                Actor = a.Actor,
                Action = a.Action,
                Target = a.Target,
                Result = a.Result,
                CorrelationId = a.CorrelationId,
            });

            return Results.Ok(response);
        });
    }
}
