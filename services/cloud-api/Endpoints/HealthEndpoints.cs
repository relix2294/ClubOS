using ClubOS.CloudApi.Data;

namespace ClubOS.CloudApi.Endpoints;

/// <summary>Health-эндпоинты (ТЗ §25.2): liveness и readiness (проверка БД).</summary>
public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
            .WithTags("Health")
            .AllowAnonymous();

        app.MapGet("/health/ready", async (ClubOsDbContext db, CancellationToken ct) =>
        {
            var canConnect = await db.Database.CanConnectAsync(ct);
            return canConnect
                ? Results.Ok(new { status = "ready" })
                : Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        })
            .WithTags("Health")
            .AllowAnonymous();
    }
}
