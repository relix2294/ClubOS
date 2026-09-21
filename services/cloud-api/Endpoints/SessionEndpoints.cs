using ClubOS.CloudApi.Api;
using ClubOS.CloudApi.Common;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Endpoints;

/// <summary>
/// Тестовые сессии (ТЗ §12). Тариф M0 — 120 TJS/час, снимок фиксируется на старте;
/// завершённую сессию нельзя пересчитать (EndSession идемпотентен).
/// </summary>
public static class SessionEndpoints
{
    private const long PricePerHourMinorUnits = 12_000; // 120,00 TJS/час (ТЗ §12.2)
    private const int RuleVersion = 1;

    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sessions").WithTags("Sessions").RequireAuthorization();

        group.MapPost("/start", async (
            StartSessionRequest request,
            ClubOsDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            var caller = Caller.From(http.User);
            if (caller is null)
            {
                return Results.Unauthorized();
            }

            var device = await (
                from d in db.Devices.AsNoTracking()
                join l in db.Locations.AsNoTracking() on d.LocationId equals l.Id
                where l.OrganizationId == caller.OrganizationId && d.Id == request.DeviceId
                select new { Device = d, l.Currency }).SingleOrDefaultAsync(ct);

            if (device is null)
            {
                return Results.NotFound(new { error = "Устройство не найдено в вашей организации." });
            }

            var activeExists = await db.Sessions
                .AnyAsync(s => s.DeviceId == request.DeviceId
                    && (s.State == SessionState.Active || s.State == SessionState.Ending), ct);

            if (activeExists)
            {
                return Results.Conflict(new { error = "На устройстве уже есть активная сессия." });
            }

            var now = DateTimeOffset.UtcNow;
            var session = new Session
            {
                Id = Ids.New(),
                DeviceId = request.DeviceId,
                LocationId = device.Device.LocationId,
                State = SessionState.Active,
                StartedAtUtc = now,
                PricePerHourMinorUnits = PricePerHourMinorUnits,
                Currency = device.Currency,
                Rounding = RoundingRule.CeilingPerMinute,
                RuleVersion = RuleVersion,
                Actor = string.IsNullOrWhiteSpace(request.Actor) ? caller.UserId : request.Actor,
                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId) ? Ids.New() : request.CorrelationId,
            };

            db.Sessions.Add(session);
            AuditLog.Add(db, caller, "session.started", $"{request.DeviceId}:{session.Id}", "success",
                session.LocationId, session.CorrelationId);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/sessions/{session.Id}", ToSummary(session));
        });

        group.MapPost("/{id}/end", async (
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

            var session = await (
                from s in db.Sessions
                join l in db.Locations.AsNoTracking() on s.LocationId equals l.Id
                where l.OrganizationId == caller.OrganizationId && s.Id == id
                select s).SingleOrDefaultAsync(ct);

            if (session is null)
            {
                return Results.NotFound();
            }

            // Идемпотентность: завершённая сессия возвращается как есть, без пересчёта (ТЗ §12.2).
            if (session.State == SessionState.Ended)
            {
                return Results.Ok(ToSummary(session));
            }

            var now = DateTimeOffset.UtcNow;
            var snapshot = new PriceSnapshot
            {
                PricePerHourMinorUnits = session.PricePerHourMinorUnits,
                Currency = session.Currency,
                Rounding = session.Rounding,
                RuleVersion = session.RuleVersion,
            };

            session.EndedAtUtc = now;
            session.TotalMinorUnits = BillingCalculator.CalculateMinorUnits(snapshot, now - session.StartedAtUtc);
            session.State = SessionState.Ended;

            AuditLog.Add(db, caller, "session.ended", $"{session.DeviceId}:{session.Id}",
                $"total={session.TotalMinorUnits}", session.LocationId, session.CorrelationId);
            await db.SaveChangesAsync(ct);

            return Results.Ok(ToSummary(session));
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

            var session = await (
                from s in db.Sessions.AsNoTracking()
                join l in db.Locations.AsNoTracking() on s.LocationId equals l.Id
                where l.OrganizationId == caller.OrganizationId && s.Id == id
                select s).SingleOrDefaultAsync(ct);

            return session is null ? Results.NotFound() : Results.Ok(ToSummary(session));
        });
    }

    private static SessionSummary ToSummary(Session s) => new()
    {
        SessionId = s.Id,
        DeviceId = s.DeviceId,
        State = s.State,
        StartedAtUtc = s.StartedAtUtc,
        EndedAtUtc = s.EndedAtUtc,
        PriceSnapshot = new PriceSnapshot
        {
            PricePerHourMinorUnits = s.PricePerHourMinorUnits,
            Currency = s.Currency,
            Rounding = s.Rounding,
            RuleVersion = s.RuleVersion,
        },
        TotalMinorUnits = s.TotalMinorUnits,
    };
}
