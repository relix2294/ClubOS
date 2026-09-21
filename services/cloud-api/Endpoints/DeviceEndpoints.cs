using System.Text.Json;
using ClubOS.CloudApi.Api;
using ClubOS.CloudApi.Common;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Endpoints;

/// <summary>Устройства и команды (ТЗ §9, §10.2). Все запросы изолированы по организации.</summary>
public static class DeviceEndpoints
{
    private const int DefaultCommandTtlSeconds = 120;

    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/devices").WithTags("Devices").RequireAuthorization();

        group.MapGet("/", async (
            string? locationId,
            ClubOsDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            var caller = Caller.From(http.User);
            if (caller is null)
            {
                return Results.Unauthorized();
            }

            var query = ScopedDevices(db, caller.OrganizationId);
            if (!string.IsNullOrEmpty(locationId))
            {
                query = query.Where(d => d.LocationId == locationId);
            }

            var devices = await query
                .OrderBy(d => d.DisplayName)
                .ToListAsync(ct);

            return Results.Ok(devices.Select(ToResponse));
        });

        group.MapGet("/{id}", async (
            string id,
            ClubOsDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            var caller = Caller.From(http.User);
            if (caller is null)
            {
                return Results.Unauthorized();
            }

            var device = await ScopedDevices(db, caller.OrganizationId)
                .SingleOrDefaultAsync(d => d.Id == id, ct);

            return device is null ? Results.NotFound() : Results.Ok(ToResponse(device));
        });

        // Постановка команды ShowMessage в очередь (allow-list — ТЗ §10.2 CMD-004).
        group.MapPost("/{id}/commands", async (
            string id,
            IssueShowMessageRequest request,
            ClubOsDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            var caller = Caller.From(http.User);
            if (caller is null)
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest(new { error = "message обязателен." });
            }

            var device = await ScopedDevices(db, caller.OrganizationId)
                .SingleOrDefaultAsync(d => d.Id == id, ct);

            if (device is null)
            {
                return Results.NotFound();
            }

            var now = DateTimeOffset.UtcNow;
            var ttl = request.TtlSeconds is > 0 ? request.TtlSeconds.Value : DefaultCommandTtlSeconds;
            var payload = JsonSerializer.Serialize(new ShowMessagePayload
            {
                Title = request.Title ?? string.Empty,
                Message = request.Message,
            });

            var command = new DeviceCommand
            {
                Id = Ids.New(),
                DeviceId = device.Id,
                LocationId = device.LocationId,
                CommandType = CommandType.ShowMessage,
                PayloadJson = payload,
                IssuedBy = caller.UserId,
                IssuedAtUtc = now,
                ExpiresAtUtc = now.AddSeconds(ttl),
                CorrelationId = Ids.New(),
                State = CommandState.Queued,
                UpdatedAtUtc = now,
            };

            db.DeviceCommands.Add(command);
            AuditLog.Add(db, caller, "device.command.issued", $"{device.Id}:ShowMessage", "queued",
                device.LocationId, command.CorrelationId);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/devices/{device.Id}/commands/{command.Id}", ToResponse(command));
        });

        group.MapGet("/{id}/commands", async (
            string id,
            ClubOsDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            var caller = Caller.From(http.User);
            if (caller is null)
            {
                return Results.Unauthorized();
            }

            var owns = await ScopedDevices(db, caller.OrganizationId).AnyAsync(d => d.Id == id, ct);
            if (!owns)
            {
                return Results.NotFound();
            }

            var commands = await db.DeviceCommands
                .AsNoTracking()
                .Where(c => c.DeviceId == id)
                .OrderByDescending(c => c.IssuedAtUtc)
                .ToListAsync(ct);

            return Results.Ok(commands.Select(ToResponse));
        });

        // Приём результата исполнения от устройства/Edge (ТЗ §10.2 CMD-007). Идемпотентно и вперёд.
        group.MapPost("/{id}/commands/{commandId}/result", async (
            string id,
            string commandId,
            CommandResult result,
            ClubOsDbContext db,
            CancellationToken ct) =>
        {
            var command = await db.DeviceCommands
                .SingleOrDefaultAsync(c => c.Id == commandId && c.DeviceId == id, ct);

            if (command is null)
            {
                return Results.NotFound();
            }

            if (CommandStateMachine.CanAdvance(command.State, result.State))
            {
                command.State = result.State;
                command.Error = result.Error;
                command.UpdatedAtUtc = result.ReportedAtUtc.ToUniversalTime();
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(ToResponse(command));
        }).AllowAnonymous();
    }

    private static IQueryable<Device> ScopedDevices(ClubOsDbContext db, string organizationId) =>
        from d in db.Devices.AsNoTracking()
        join l in db.Locations.AsNoTracking() on d.LocationId equals l.Id
        where l.OrganizationId == organizationId
        select d;

    private static DeviceResponse ToResponse(Device d) => new()
    {
        Id = d.Id,
        LocationId = d.LocationId,
        ZoneId = d.ZoneId,
        DisplayName = d.DisplayName,
        Simulated = d.Simulated,
        Status = d.Status,
        LastHeartbeatUtc = d.LastHeartbeatUtc,
        Hostname = d.Hostname,
        WindowsVersion = d.WindowsVersion,
        Cpu = d.Cpu,
        RamMegabytes = d.RamMegabytes,
        Ipv4 = d.Ipv4,
        AgentVersion = d.AgentVersion,
    };

    private static CommandResponse ToResponse(DeviceCommand c) => new()
    {
        Id = c.Id,
        DeviceId = c.DeviceId,
        CommandType = c.CommandType,
        State = c.State,
        IssuedBy = c.IssuedBy,
        IssuedAtUtc = c.IssuedAtUtc,
        ExpiresAtUtc = c.ExpiresAtUtc,
        CorrelationId = c.CorrelationId,
        Error = c.Error,
        UpdatedAtUtc = c.UpdatedAtUtc,
    };
}
