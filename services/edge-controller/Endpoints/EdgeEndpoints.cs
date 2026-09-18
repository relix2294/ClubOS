using System.Text.Json;
using ClubOS.Contracts;
using ClubOS.EdgeController.Data;
using ClubOS.EdgeController.Sessions;
using ClubOS.EdgeController.Sync;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.EdgeController.Endpoints;

/// <summary>
/// Локальный API Edge (ТЗ §25.2.4): здоровье, сессии (offline start/end), приём heartbeat
/// от Agent, ретрансляция команд и приём их результатов. Всё работает при недоступном Cloud.
/// </summary>
public static class EdgeEndpoints
{
    /// <summary>Запрос старта сессии от edge-cli / Admin (offline).</summary>
    public sealed record StartSessionBody(string DeviceId, string? Actor);

    /// <summary>Команда для агента (без служебных полей Cloud).</summary>
    public sealed record AgentCommand(
        string CommandId,
        string DeviceId,
        CommandType CommandType,
        JsonElement Payload,
        DateTimeOffset ExpiresAtUtc);

    public static void MapEdgeEndpoints(this IEndpointRouteBuilder app)
    {
        MapHealth(app);
        MapSessions(app);
        MapAgent(app);
    }

    private static void MapHealth(IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).WithTags("Health");

        app.MapGet("/health/ready", async (EdgeDbContext db, CancellationToken ct) =>
        {
            var ok = await db.Database.CanConnectAsync(ct);
            return ok
                ? Results.Ok(new { status = "ready" })
                : Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).WithTags("Health");
    }

    private static void MapSessions(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/edge/sessions").WithTags("Sessions");

        group.MapPost("/start", async (StartSessionBody body, SessionService sessions, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.DeviceId))
            {
                return Results.BadRequest(new { error = "deviceId обязателен." });
            }

            var result = await sessions.StartAsync(body.DeviceId, body.Actor ?? "edge-cli", ct);
            return result.Outcome == StartOutcome.Conflict
                ? Results.Conflict(new { error = "На устройстве уже есть активная сессия." })
                : Results.Created($"/api/edge/sessions/{result.Summary!.SessionId}", result.Summary);
        });

        group.MapPost("/{id}/end", async (string id, SessionService sessions, CancellationToken ct) =>
        {
            var summary = await sessions.EndAsync(id, ct);
            return summary is null ? Results.NotFound() : Results.Ok(summary);
        });

        group.MapGet("/{id}", async (string id, SessionService sessions, CancellationToken ct) =>
        {
            var summary = await sessions.GetAsync(id, ct);
            return summary is null ? Results.NotFound() : Results.Ok(summary);
        });
    }

    private static void MapAgent(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/edge/agent").WithTags("Agent");

        // Heartbeat от Agent: обновляем состояние устройства и ставим событие в outbox для Cloud.
        group.MapPost("/heartbeat", async (HeartbeatMessage hb, EdgeDbContext db, CancellationToken ct) =>
        {
            var device = await db.Devices.SingleOrDefaultAsync(d => d.DeviceId == hb.DeviceId, ct);
            if (device is null)
            {
                device = new EdgeDeviceState { DeviceId = hb.DeviceId };
                db.Devices.Add(device);
            }

            device.Status = hb.Status;
            device.LastHeartbeatUtc = hb.ClockUtc.ToUniversalTime();
            if (hb.Inventory is { } inv)
            {
                device.Hostname = inv.Hostname;
                device.WindowsVersion = inv.WindowsVersion;
                device.Cpu = inv.Cpu;
                device.RamMegabytes = inv.RamMegabytes;
                device.Ipv4 = inv.Ipv4;
                device.AgentVersion = inv.AgentVersion;
            }

            OutboxWriter.Enqueue(db, "DeviceHeartbeat", hb.DeviceId, hb, hb.ClockUtc.ToUniversalTime());
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        // Приём команды от Cloud (идемпотентно по commandId — ТЗ §4).
        app.MapPost("/api/edge/commands", async (CommandEnvelope envelope, EdgeDbContext db, CancellationToken ct) =>
        {
            var already = await db.Inbox.AnyAsync(r => r.Id == envelope.CommandId, ct);
            if (already)
            {
                return Results.Ok(new { status = "duplicate" });
            }

            db.Inbox.Add(new InboxReceipt
            {
                Id = envelope.CommandId,
                Kind = "command",
                ReceivedAtUtc = DateTimeOffset.UtcNow,
            });

            var payloadJson = envelope.Payload.ValueKind == JsonValueKind.Undefined
                ? "{}"
                : envelope.Payload.GetRawText();
            var single = envelope.TargetDeviceIds.Count == 1;
            foreach (var deviceId in envelope.TargetDeviceIds)
            {
                db.Commands.Add(new EdgeCommand
                {
                    Id = single ? envelope.CommandId : $"{envelope.CommandId}:{deviceId}",
                    DeviceId = deviceId,
                    CommandType = envelope.CommandType,
                    PayloadJson = payloadJson,
                    IssuedAtUtc = envelope.IssuedAtUtc.ToUniversalTime(),
                    ExpiresAtUtc = envelope.ExpiresAtUtc.ToUniversalTime(),
                    State = CommandState.Queued,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                });
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { status = "accepted" });
        }).WithTags("Commands");

        // Agent забирает свои команды; помечаем доставленными, просроченные — Expired.
        group.MapGet("/{deviceId}/commands", async (string deviceId, EdgeDbContext db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var candidates = await db.Commands
                .Where(c => c.DeviceId == deviceId
                    && (c.State == CommandState.Queued || c.State == CommandState.Delivered))
                .OrderBy(c => c.IssuedAtUtc)
                .ToListAsync(ct);

            var result = new List<AgentCommand>();
            foreach (var cmd in candidates)
            {
                if (cmd.ExpiresAtUtc <= now)
                {
                    cmd.State = CommandState.Expired;
                    cmd.UpdatedAtUtc = now;
                    continue;
                }

                if (cmd.State == CommandState.Queued)
                {
                    cmd.State = CommandState.Delivered;
                    cmd.UpdatedAtUtc = now;
                }

                using var doc = JsonDocument.Parse(cmd.PayloadJson);
                result.Add(new AgentCommand(cmd.Id, cmd.DeviceId, cmd.CommandType, doc.RootElement.Clone(), cmd.ExpiresAtUtc));
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        });

        // Agent сообщает результат; переход вперёд по машине состояний + событие в outbox для Cloud.
        group.MapPost("/commands/{id}/result", async (string id, CommandResult result, EdgeDbContext db, CancellationToken ct) =>
        {
            var cmd = await db.Commands.SingleOrDefaultAsync(c => c.Id == id, ct);
            if (cmd is null)
            {
                return Results.NotFound();
            }

            if (CommandStateMachine.CanAdvance(cmd.State, result.State))
            {
                cmd.State = result.State;
                cmd.Error = result.Error;
                cmd.UpdatedAtUtc = result.ReportedAtUtc.ToUniversalTime();

                OutboxWriter.Enqueue(db, "CommandResult", cmd.Id, new
                {
                    commandId = cmd.Id,
                    deviceId = cmd.DeviceId,
                    state = cmd.State.ToString(),
                    error = cmd.Error,
                    reportedAtUtc = cmd.UpdatedAtUtc,
                }, cmd.UpdatedAtUtc);

                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(new { cmd.Id, state = cmd.State.ToString() });
        });
    }
}
