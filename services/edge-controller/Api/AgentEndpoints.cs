using ClubOS.Contracts;
using ClubOS.EdgeController.Cloud;
using ClubOS.EdgeController.Storage;

namespace ClubOS.EdgeController.Api;

/// <summary>API для Windows Agent в LAN клуба (ТЗ §10). Agent говорит только с Edge.</summary>
public static class AgentEndpoints
{
    private const int MaxWaitSeconds = 25;

    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/agent/v1");

        group.MapPost("/enroll", Enroll);
        group.MapPost("/heartbeat", Heartbeat);
        group.MapGet("/commands", Commands);
        group.MapPost("/commands/{commandId}/result", Result);
    }

    /// <summary>Enrollment агента: Edge пересылает запрос в Cloud (нужна связь с Cloud).</summary>
    private static async Task<IResult> Enroll(DeviceEnrollRequest request, EdgeIdentityStore identity, CloudClient cloud,
        EdgeStore store, ILoggerFactory logs, CancellationToken ct)
    {
        if (!identity.IsEnrolled)
        {
            return Results.Problem(statusCode: 503, title: "Edge не зарегистрирован в Cloud.");
        }

        try
        {
            var response = await cloud.EnrollDeviceAsync(request, ct);
            await store.AddEnrolledDeviceAsync(response, request.Inventory, ct);
            logs.CreateLogger("Enrollment").LogInformation("Устройство {DeviceId} ({Name}) зарегистрировано",
                response.DeviceId, response.DisplayName);
            return Results.Ok(response);
        }
        catch (CloudRequestException ex) when ((int)ex.Status is >= 400 and < 500)
        {
            return Results.Problem(statusCode: (int)ex.Status, title: "Cloud отклонил enrollment.",
                detail: "Токен недействителен, истёк или уже использован.");
        }
        catch (HttpRequestException)
        {
            return Results.Problem(statusCode: 503, title: "Cloud недоступен — enrollment требует связи с Cloud.");
        }
    }

    private static async Task<IResult> Heartbeat(HeartbeatMessage heartbeat, HttpContext http, AgentAuth auth,
        EdgeStore store, TimeProvider time, CancellationToken ct)
    {
        var deviceId = auth.Authenticate(http);
        if (deviceId is null || heartbeat.DeviceId != deviceId)
        {
            return Results.Unauthorized();
        }

        await store.RecordHeartbeatAsync(heartbeat, ct);
        return Results.Ok(new HeartbeatAck { ServerTimeUtc = time.GetUtcNow(), State = store.GetAgentState(deviceId) });
    }

    /// <summary>
    /// Long-poll команд. Ответ приходит раньше срока, если есть команды или состояние сессии устройства
    /// отличается от <paramref name="sessionStamp"/>, известного агенту (старт/продление/завершение).
    /// </summary>
    private static async Task<IResult> Commands(int? waitSeconds, string? sessionStamp, HttpContext http, AgentAuth auth,
        EdgeStore store, EdgeSignals signals, TimeProvider time, CancellationToken ct)
    {
        var deviceId = auth.Authenticate(http);
        if (deviceId is null)
        {
            return Results.Unauthorized();
        }

        var deadline = time.GetUtcNow().AddSeconds(Math.Clamp(waitSeconds ?? 0, 0, MaxWaitSeconds));
        while (true)
        {
            var commands = await store.TakeCommandsForDeviceAsync(deviceId, ct);
            var state = store.GetAgentState(deviceId);
            var remaining = deadline - time.GetUtcNow();
            var stateChanged = sessionStamp is not null && state is not null && state.Stamp != sessionStamp;
            if (commands.Count > 0 || stateChanged || remaining <= TimeSpan.Zero)
            {
                return Results.Ok(new AgentCommandsResponse { Commands = commands, State = state });
            }

            await signals.WaitDeviceAsync(deviceId, remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2), ct);
        }
    }

    private static async Task<IResult> Result(string commandId, AgentCommandResultRequest request, HttpContext http,
        AgentAuth auth, EdgeStore store, CancellationToken ct)
    {
        var deviceId = auth.Authenticate(http);
        if (deviceId is null)
        {
            return Results.Unauthorized();
        }

        var changed = await store.ApplyAgentResultAsync(deviceId, commandId, request.State, request.Error, ct);
        return Results.Ok(new { changed, state = store.GetCommandState(commandId) });
    }
}
