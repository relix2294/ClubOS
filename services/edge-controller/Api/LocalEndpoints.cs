using System.Net;
using ClubOS.EdgeController.Cloud;
using ClubOS.EdgeController.Storage;
using Microsoft.Extensions.Options;

namespace ClubOS.EdgeController.Api;

public sealed record LocalStartSessionRequest(string DeviceId, string? Actor);

public sealed record LocalEndSessionRequest(string? Actor);

/// <summary>
/// Локальный admin API для edge-cli (ТЗ §25.2.4): старт/стоп сессии при недоступном Cloud без прямого SQL.
/// Только loopback + локальный токен.
/// </summary>
public static class LocalEndpoints
{
    public static void MapLocalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/local/v1").AddEndpointFilter(RequireLocalAdmin);

        group.MapGet("/status", Status);
        group.MapGet("/devices", (EdgeStore store) => Results.Ok(store.ListDevices().Select(d => new
        {
            d.DeviceId,
            d.DisplayName,
            d.ZoneId,
            d.Simulated,
            d.Online,
            status = d.AgentStatus,
            d.LastHeartbeatUtc
        })));
        group.MapGet("/sessions", (EdgeStore store, bool? active) =>
            Results.Ok(active == true ? store.GetActiveSessions() : store.ListRecentSessions(50)));
        group.MapPost("/sessions", StartSession);
        group.MapPost("/sessions/{sessionId}/end", EndSession);
    }

    private static async ValueTask<object?> RequireLocalAdmin(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var options = http.RequestServices.GetRequiredService<IOptions<EdgeOptions>>().Value;
        if (options.LocalApiEnforceLoopback &&
            (http.Connection.LocalPort != options.LocalApiPort || http.Connection.RemoteIpAddress is not { } ip ||
             !IPAddress.IsLoopback(ip)))
        {
            return Results.NotFound();
        }

        string? header = http.Request.Headers.Authorization;
        var token = header?.StartsWith("Bearer ", StringComparison.Ordinal) == true ? header[7..] : null;
        if (!http.RequestServices.GetRequiredService<LocalAdminToken>().Matches(token))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }

    private static IResult Status(EdgeIdentityStore identity, EdgeStore store, CloudClient cloud) => Results.Ok(new
    {
        enrolled = identity.IsEnrolled,
        edgeId = identity.Current?.EdgeId,
        locationId = identity.Current?.LocationId,
        locationName = store.GetKv("location_name"),
        cloudReachable = cloud.IsReachable,
        lastCloudContactUtc = cloud.LastSuccessUtc,
        pendingOutboxEvents = store.CountPendingEvents(),
        activeSessions = store.GetActiveSessions().Count,
        devices = store.ListDevices().Count
    });

    private static async Task<IResult> StartSession(LocalStartSessionRequest request, EdgeStore store, CancellationToken ct)
    {
        var result = await store.StartLocalSessionAsync(request.DeviceId, Actor(request.Actor), ct);
        return ToResult(result);
    }

    private static async Task<IResult> EndSession(string sessionId, LocalEndSessionRequest? request, EdgeStore store,
        CancellationToken ct)
    {
        var result = await store.EndSessionAsync(sessionId, Actor(request?.Actor), ct);
        return ToResult(result);
    }

    private static string Actor(string? actor) =>
        $"edge-cli:{(string.IsNullOrWhiteSpace(actor) ? Environment.UserName : actor.Trim())}";

    private static IResult ToResult(SessionResult result) => result.Outcome switch
    {
        SessionOutcome.Started or SessionOutcome.Ended => Results.Ok(new { outcome = result.Outcome.ToString(), result.Session }),
        SessionOutcome.AlreadyStarted or SessionOutcome.AlreadyEnded => Results.Ok(new { outcome = result.Outcome.ToString(), result.Session }),
        SessionOutcome.NotFound => Results.Problem(statusCode: 404, title: result.Error),
        _ => Results.Problem(statusCode: 409, title: result.Error)
    };
}
