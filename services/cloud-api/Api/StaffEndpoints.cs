using System.Text.Json;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.CloudApi.Security;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;

using ClubOS.CloudApi.Edges;

namespace ClubOS.CloudApi.Api;

/// <summary>
/// API для Admin Web (сотрудники). Каждый запрос ограничен tenant'ом из JWT:
/// чужие объекты возвращают 404 (не 403), чтобы не раскрывать их существование.
/// </summary>
public static class StaffEndpoints
{
    public const int MaxTitleLength = 80;
    public const int MaxMessageLength = 500;
    public const int DefaultCommandTtlSeconds = 120;
    public const int MaxCommandTtlSeconds = 3600;

    public static void MapStaffEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        // /me доступен и с временным паролем: UI узнаёт, что нужно сменить пароль, и права пользователя.
        api.MapGet("/me", GetMe).WithTags("Auth").RequireAuthorization(Policies.Staff);

        api.MapGet("/locations/{locationId}/devices", ListDevices).WithTags("Devices").RequirePermission(Permissions.DevicesView);
        api.MapGet("/devices/{deviceId}", GetDevice).WithTags("Devices").RequirePermission(Permissions.DevicesView);
        api.MapGet("/devices/{deviceId}/commands", ListCommands).WithTags("Commands").RequirePermission(Permissions.DevicesView);
        api.MapPost("/devices/{deviceId}/commands", IssueCommand).WithTags("Commands").RequirePermission(Permissions.DevicesCommand);
        api.MapGet("/devices/{deviceId}/sessions", ListSessions).WithTags("Sessions").RequirePermission(Permissions.DevicesView);
        api.MapPost("/devices/{deviceId}/sessions", StartSession).WithTags("Sessions").RequirePermission(Permissions.SessionsManage);
        api.MapPost("/sessions/{sessionId}/end", EndSession).WithTags("Sessions").RequirePermission(Permissions.SessionsManage);
        api.MapGet("/audit", ListAudit).WithTags("Audit").RequirePermission(Permissions.AuditView);

        var enrollment = app.MapGroup("/api/v1/enrollment-tokens").WithTags("Enrollment")
            .RequirePermission(Permissions.EnrollmentManage);
        enrollment.MapPost("/device", CreateDeviceToken);
        enrollment.MapPost("/edge", CreateEdgeToken);
    }

    private static async Task<IResult> GetMe(HttpContext http, ClubOsDbContext db, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == staff.UserId, ct);
        var org = await db.Organizations.AsNoTracking().SingleAsync(x => x.Id == staff.TenantId, ct);
        var locations = await db.Locations.AsNoTracking().Where(x => x.OrganizationId == staff.TenantId)
            .OrderBy(x => x.Name).ToListAsync(ct);
        var locationIds = locations.Select(x => x.Id).ToList();
        var zones = await db.Zones.AsNoTracking().Where(x => locationIds.Contains(x.LocationId)).OrderBy(x => x.Name)
            .ToListAsync(ct);
        var edges = await db.Edges.AsNoTracking().Where(x => x.TenantId == staff.TenantId).ToListAsync(ct);
        var now = time.GetUtcNow();

        var views = locations.Select(l => new LocationView(l.Id, l.Name, l.Timezone, l.Currency,
            zones.Where(z => z.LocationId == l.Id).Select(z => new ZoneView(z.Id, z.Name, z.PricePerHourMinorUnits)).ToList(),
            edges.Where(e => e.LocationId == l.Id).Select(e => e.ToView(now)).ToList())).ToList();

        return Results.Ok(new MeResponse(user.ToView(org.Name), views));
    }

    private static async Task<IResult> ListDevices(string locationId, HttpContext http, ClubOsDbContext db,
        TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        if (!await db.Locations.AnyAsync(x => x.Id == locationId && x.OrganizationId == staff.TenantId, ct))
        {
            return Problems.NotFound("Локация");
        }

        var devices = await db.Devices.AsNoTracking()
            .Where(x => x.TenantId == staff.TenantId && x.LocationId == locationId)
            .OrderBy(x => x.DisplayName).ToListAsync(ct);
        var zones = await db.Zones.AsNoTracking().Where(x => x.LocationId == locationId)
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var deviceIds = devices.Select(x => x.Id).ToList();
        var openSessions = await db.Sessions.AsNoTracking()
            .Where(x => deviceIds.Contains(x.DeviceId) &&
                        (x.State == SessionState.Created || x.State == SessionState.Active))
            .ToListAsync(ct);
        var now = time.GetUtcNow();

        return Results.Ok(devices.Select(d => d.ToView(zones.GetValueOrDefault(d.ZoneId, "?"),
            openSessions.Where(s => s.DeviceId == d.Id).OrderByDescending(s => s.RequestedAtUtc)
                .Select(s => s.ToView()).FirstOrDefault(), now)).ToList());
    }

    private static async Task<IResult> GetDevice(string deviceId, HttpContext http, ClubOsDbContext db, TimeProvider time,
        CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var device = await db.Devices.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == deviceId && x.TenantId == staff.TenantId, ct);
        if (device is null)
        {
            return Problems.NotFound("Устройство");
        }

        var zone = await db.Zones.AsNoTracking().SingleAsync(x => x.Id == device.ZoneId, ct);
        var open = await db.Sessions.AsNoTracking()
            .Where(x => x.DeviceId == deviceId && (x.State == SessionState.Created || x.State == SessionState.Active))
            .OrderByDescending(x => x.RequestedAtUtc).FirstOrDefaultAsync(ct);
        return Results.Ok(device.ToView(zone.Name, open?.ToView(), time.GetUtcNow()));
    }

    private static async Task<IResult> ListCommands(string deviceId, HttpContext http, ClubOsDbContext db,
        CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        if (!await db.Devices.AnyAsync(x => x.Id == deviceId && x.TenantId == staff.TenantId, ct))
        {
            return Problems.NotFound("Устройство");
        }

        var commands = await db.DeviceCommands.AsNoTracking()
            .Where(x => x.DeviceId == deviceId && x.TenantId == staff.TenantId)
            .OrderByDescending(x => x.IssuedAtUtc).Take(50).ToListAsync(ct);
        return Results.Ok(commands.Select(x => x.ToView()).ToList());
    }

    private static async Task<IResult> IssueCommand(string deviceId, IssueCommandRequest request, HttpContext http,
        ClubOsDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId && x.TenantId == staff.TenantId, ct);
        if (device is null)
        {
            return Problems.NotFound("Устройство");
        }

        // Allow-list команд и строгая валидация payload (ТЗ §10.2 CMD-004). Произвольных команд нет.
        JsonElement payload;
        switch (request.CommandType)
        {
            case CommandType.ShowMessage:
                var title = request.Title?.Trim() ?? string.Empty;
                var message = request.Message?.Trim() ?? string.Empty;
                if (title.Length is 0 or > MaxTitleLength || message.Length is 0 or > MaxMessageLength)
                {
                    return Problems.Validation("invalid_payload",
                        $"Заголовок 1–{MaxTitleLength} символов, сообщение 1–{MaxMessageLength} символов.");
                }

                payload = ContractJson.ToElement(new ShowMessagePayload { Title = title, Message = message });
                break;
            case CommandType.LockTestMode:
                if (request.Lock is null || (request.Reason?.Length ?? 0) > MaxTitleLength)
                {
                    return Problems.Validation("invalid_payload", "Укажите lock=true/false; reason не длиннее 80 символов.");
                }

                payload = ContractJson.ToElement(new LockTestModePayload { Lock = request.Lock.Value, Reason = request.Reason?.Trim() });
                break;
            default:
                return Problems.Validation("unsupported_command", "Команда не поддерживается в M0.");
        }

        var ttl = request.TtlSeconds ?? DefaultCommandTtlSeconds;
        if (ttl is < 10 or > MaxCommandTtlSeconds)
        {
            return Problems.Validation("invalid_ttl", $"TTL от 10 до {MaxCommandTtlSeconds} секунд.");
        }

        var commandId = string.IsNullOrWhiteSpace(request.CommandId) ? Ids.New("cmd") : request.CommandId.Trim();
        if (commandId.Length > 64)
        {
            return Problems.Validation("invalid_command_id", "commandId не длиннее 64 символов.");
        }

        // Идемпотентность по commandId: повтор запроса возвращает существующую команду (CMD-002).
        var existing = await db.DeviceCommands.AsNoTracking().SingleOrDefaultAsync(x => x.Id == commandId, ct);
        if (existing is not null)
        {
            return existing.TenantId == staff.TenantId && existing.DeviceId == deviceId
                ? Results.Ok(existing.ToView())
                : Problems.Conflict("command_id_taken", "commandId уже использован.");
        }

        var now = time.GetUtcNow();
        var correlationId = Ids.New("cor");
        var envelope = new CommandEnvelope
        {
            CommandId = commandId,
            CommandType = request.CommandType,
            TargetDeviceIds = [deviceId],
            IssuedBy = staff.Actor,
            IssuedAtUtc = now,
            ExpiresAtUtc = now.AddSeconds(ttl),
            CorrelationId = correlationId,
            Payload = payload
        };

        var command = new DeviceCommand
        {
            Id = commandId,
            TenantId = staff.TenantId,
            DeviceId = deviceId,
            LocationId = device.LocationId,
            CommandType = request.CommandType,
            PayloadJson = payload.GetRawText(),
            IssuedBy = staff.Actor,
            IssuedAtUtc = now,
            ExpiresAtUtc = envelope.ExpiresAtUtc,
            CorrelationId = correlationId,
            State = CommandState.Queued,
            UpdatedAtUtc = now
        };
        db.DeviceCommands.Add(command);
        EdgeQueue.Enqueue(db, staff.TenantId, device.LocationId, new EdgeCommand
        {
            Id = commandId,
            Kind = EdgeCommandKind.DeviceCommand,
            IssuedAtUtc = now,
            ExpiresAtUtc = envelope.ExpiresAtUtc,
            DeviceCommand = envelope
        });
        audit.Write(staff.TenantId, device.LocationId, staff.Actor, $"command.{request.CommandType}", $"device:{deviceId}",
            AuditResults.Requested, correlationId, new { commandId, payload });
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/devices/{deviceId}/commands/{commandId}", command.ToView());
    }

    private static async Task<IResult> ListSessions(string deviceId, HttpContext http, ClubOsDbContext db,
        CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        if (!await db.Devices.AnyAsync(x => x.Id == deviceId && x.TenantId == staff.TenantId, ct))
        {
            return Problems.NotFound("Устройство");
        }

        var sessions = await db.Sessions.AsNoTracking().Where(x => x.DeviceId == deviceId && x.TenantId == staff.TenantId)
            .OrderByDescending(x => x.RequestedAtUtc).Take(20).ToListAsync(ct);
        return Results.Ok(sessions.Select(x => x.ToView()).ToList());
    }

    /// <summary>
    /// Запрос старта сессии. Источник истины — Edge (ТЗ §23.3): Cloud фиксирует price snapshot,
    /// ставит StartSession в очередь Edge и возвращает 202; Active наступает по событию SessionStarted.
    /// </summary>
    private static async Task<IResult> StartSession(string deviceId, HttpContext http, ClubOsDbContext db,
        AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId && x.TenantId == staff.TenantId, ct);
        if (device is null)
        {
            return Problems.NotFound("Устройство");
        }

        if (await db.Sessions.AnyAsync(x => x.DeviceId == deviceId &&
                                            (x.State == SessionState.Created || x.State == SessionState.Active), ct))
        {
            return Problems.Conflict("session_already_open", "На устройстве уже есть открытая сессия.");
        }

        var zone = await db.Zones.SingleAsync(x => x.Id == device.ZoneId, ct);
        var location = await db.Locations.SingleAsync(x => x.Id == device.LocationId, ct);
        var now = time.GetUtcNow();
        var session = new Session
        {
            Id = Ids.New("ses"),
            TenantId = staff.TenantId,
            DeviceId = deviceId,
            LocationId = device.LocationId,
            State = SessionState.Created,
            Origin = "cloud",
            RequestedAtUtc = now,
            PricePerHourMinorUnits = zone.PricePerHourMinorUnits,
            Currency = location.Currency,
            Rounding = zone.Rounding,
            RuleVersion = zone.RuleVersion,
            StartedBy = staff.Actor,
            CorrelationId = Ids.New("cor")
        };
        db.Sessions.Add(session);
        EdgeQueue.Enqueue(db, staff.TenantId, device.LocationId, new EdgeCommand
        {
            Id = Ids.New("ecm"),
            Kind = EdgeCommandKind.StartSession,
            IssuedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(10),
            StartSession = new StartSessionCommand
            {
                SessionId = session.Id,
                DeviceId = deviceId,
                PriceSnapshot = session.Snapshot(),
                Actor = staff.Actor,
                CorrelationId = session.CorrelationId
            }
        });
        audit.Write(staff.TenantId, device.LocationId, staff.Actor, "session.start", $"device:{deviceId}",
            AuditResults.Requested, session.CorrelationId, new { sessionId = session.Id, zone.PricePerHourMinorUnits });
        await db.SaveChangesAsync(ct);

        return Results.Accepted($"/api/v1/devices/{deviceId}/sessions", session.ToView());
    }

    /// <summary>Идемпотентно: повторный запрос завершения не создаёт второй EndSession (ТЗ §5.1).</summary>
    private static async Task<IResult> EndSession(string sessionId, HttpContext http, ClubOsDbContext db,
        AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.TenantId == staff.TenantId, ct);
        if (session is null)
        {
            return Problems.NotFound("Сессия");
        }

        if (!session.IsOpen() || session.EndRequestedAtUtc is not null)
        {
            return Results.Ok(session.ToView());
        }

        var now = time.GetUtcNow();
        session.EndRequestedAtUtc = now;
        EdgeQueue.Enqueue(db, staff.TenantId, session.LocationId, new EdgeCommand
        {
            Id = Ids.New("ecm"),
            Kind = EdgeCommandKind.EndSession,
            IssuedAtUtc = now,
            ExpiresAtUtc = now.AddDays(7), // завершение нельзя «потерять»: ждёт возвращения Edge
            EndSession = new EndSessionCommand
            {
                SessionId = session.Id,
                Actor = staff.Actor,
                CorrelationId = session.CorrelationId
            }
        });
        audit.Write(staff.TenantId, session.LocationId, staff.Actor, "session.end", $"device:{session.DeviceId}",
            AuditResults.Requested, session.CorrelationId, new { sessionId = session.Id });
        await db.SaveChangesAsync(ct);

        return Results.Accepted($"/api/v1/devices/{session.DeviceId}/sessions", session.ToView());
    }

    private static async Task<IResult> ListAudit(string? locationId, string? target, int? limit, HttpContext http,
        ClubOsDbContext db, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var take = Math.Clamp(limit ?? 100, 1, 500);
        var query = db.AuditEvents.AsNoTracking().Where(x => x.TenantId == staff.TenantId);
        if (!string.IsNullOrWhiteSpace(locationId))
        {
            query = query.Where(x => x.LocationId == locationId);
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            query = query.Where(x => x.Target == target);
        }

        var items = await query.OrderByDescending(x => x.OccurredAtUtc).Take(take).ToListAsync(ct);
        var names = await ActorNames(db, staff.TenantId, ct);

        return Results.Ok(items.Select(x => new AuditEventView(x.Id, x.OccurredAtUtc, x.Actor,
            names.GetValueOrDefault(x.Actor, x.Actor), x.Action, x.Target, x.Result, x.CorrelationId,
            x.DetailsJson is null ? null : JsonDocument.Parse(x.DetailsJson).RootElement.Clone())).ToList());
    }

    private static async Task<Dictionary<string, string>> ActorNames(ClubOsDbContext db, string tenantId,
        CancellationToken ct)
    {
        var names = new Dictionary<string, string>();
        foreach (var u in await db.Users.AsNoTracking().Where(x => x.OrganizationId == tenantId)
                     .Select(x => new { x.Id, x.Email }).ToListAsync(ct))
        {
            names[$"user:{u.Id}"] = u.Email;
        }

        foreach (var e in await db.Edges.AsNoTracking().Where(x => x.TenantId == tenantId)
                     .Select(x => new { x.Id, x.Name }).ToListAsync(ct))
        {
            names[$"edge:{e.Id}"] = $"Edge «{e.Name}»";
        }

        foreach (var d in await db.Devices.AsNoTracking().Where(x => x.TenantId == tenantId)
                     .Select(x => new { x.Id, x.DisplayName }).ToListAsync(ct))
        {
            names[$"device:{d.Id}"] = d.DisplayName;
        }

        return names;
    }

    private static async Task<IResult> CreateDeviceToken(EnrollmentTokenRequest request, HttpContext http,
        ClubOsDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var name = request.DisplayName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 64)
        {
            return Problems.Validation("invalid_name", "Имя устройства 1–64 символа.");
        }

        var locationOk = await db.Locations.AnyAsync(x => x.Id == request.LocationId && x.OrganizationId == staff.TenantId, ct);
        var zoneOk = await db.Zones.AnyAsync(x => x.Id == request.ZoneId && x.LocationId == request.LocationId, ct);
        if (!locationOk || !zoneOk)
        {
            return Problems.NotFound("Локация или зона");
        }

        return await CreateToken(db, audit, time, staff, EnrollmentKinds.Device, request.LocationId, request.ZoneId, name,
            request.Simulated, ct);
    }

    private static async Task<IResult> CreateEdgeToken(EdgeEnrollmentTokenRequest request, HttpContext http,
        ClubOsDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 64)
        {
            return Problems.Validation("invalid_name", "Имя Edge 1–64 символа.");
        }

        if (!await db.Locations.AnyAsync(x => x.Id == request.LocationId && x.OrganizationId == staff.TenantId, ct))
        {
            return Problems.NotFound("Локация");
        }

        return await CreateToken(db, audit, time, staff, EnrollmentKinds.Edge, request.LocationId, null, name, false, ct);
    }

    private static async Task<IResult> CreateToken(ClubOsDbContext db, AuditWriter audit, TimeProvider time,
        StaffContext staff, string kind, string locationId, string? zoneId, string name, bool simulated,
        CancellationToken ct)
    {
        var secret = Ids.NewSecret();
        var now = time.GetUtcNow();
        var token = new EnrollmentToken
        {
            Id = Ids.New("enr"),
            TenantId = staff.TenantId,
            Kind = kind,
            LocationId = locationId,
            ZoneId = zoneId,
            DisplayName = name,
            Simulated = simulated,
            TokenHash = Ids.HashSecret(secret),
            CreatedBy = staff.Actor,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(24)
        };
        db.EnrollmentTokens.Add(token);
        // В аудит — только ID токена, не сам секрет (ТЗ §27.3).
        audit.Write(staff.TenantId, locationId, staff.Actor, $"enrollment.{kind.ToLowerInvariant()}.token_created",
            $"enrollment:{token.Id}", AuditResults.Success, details: new { name, zoneId, simulated });
        await db.SaveChangesAsync(ct);

        return Results.Ok(new EnrollmentTokenResponse { EnrollmentToken = secret, ExpiresAtUtc = token.ExpiresAtUtc });
    }
}
