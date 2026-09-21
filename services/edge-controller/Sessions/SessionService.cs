using ClubOS.Contracts;
using ClubOS.EdgeController.Config;
using ClubOS.EdgeController.Data;
using ClubOS.EdgeController.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClubOS.EdgeController.Sessions;

/// <summary>Итог операции старта: успех либо конфликт (уже есть активная сессия).</summary>
public enum StartOutcome
{
    Started,
    Conflict,
}

public sealed record StartResult(StartOutcome Outcome, SessionSummary? Summary);

/// <summary>
/// Локальные тестовые сессии Edge (ТЗ §12, §25.2.4). Работают при недоступном Cloud:
/// сессия и событие пишутся в SQLite, событие уходит в outbox для последующего синка.
/// Активная сессия переживает перезапуск (persist в SQLite).
/// </summary>
public sealed class SessionService(EdgeDbContext db, IOptions<EdgeOptions> options)
{
    private const int RuleVersion = 1;
    private readonly EdgeOptions _options = options.Value;

    public async Task<StartResult> StartAsync(string deviceId, string actor, CancellationToken ct)
    {
        var active = await db.Sessions
            .AnyAsync(s => s.DeviceId == deviceId
                && (s.State == SessionState.Active || s.State == SessionState.Ending), ct);

        if (active)
        {
            return new StartResult(StartOutcome.Conflict, null);
        }

        var now = DateTimeOffset.UtcNow;
        var session = new EdgeSession
        {
            Id = Guid.NewGuid().ToString("N"),
            DeviceId = deviceId,
            State = SessionState.Active,
            StartedAtUtc = now,
            PricePerHourMinorUnits = _options.DefaultPricePerHourMinorUnits,
            Currency = _options.DefaultCurrency,
            Rounding = RoundingRule.CeilingPerMinute,
            RuleVersion = RuleVersion,
            Actor = string.IsNullOrWhiteSpace(actor) ? "edge-cli" : actor,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };

        db.Sessions.Add(session);
        OutboxWriter.Enqueue(db, "SessionStarted", session.Id, new
        {
            sessionId = session.Id,
            deviceId = session.DeviceId,
            startedAtUtc = session.StartedAtUtc,
            pricePerHourMinorUnits = session.PricePerHourMinorUnits,
            currency = session.Currency,
            rounding = session.Rounding.ToString(),
            ruleVersion = session.RuleVersion,
            actor = session.Actor,
        }, session.StartedAtUtc, session.CorrelationId);

        await db.SaveChangesAsync(ct);
        return new StartResult(StartOutcome.Started, ToSummary(session));
    }

    /// <summary>Завершение сессии. Идемпотентно: повторный вызов не пересчитывает итог.</summary>
    public async Task<SessionSummary?> EndAsync(string sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return null;
        }

        if (session.State == SessionState.Ended)
        {
            return ToSummary(session);
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

        OutboxWriter.Enqueue(db, "SessionEnded", session.Id, new
        {
            sessionId = session.Id,
            deviceId = session.DeviceId,
            startedAtUtc = session.StartedAtUtc,
            endedAtUtc = session.EndedAtUtc,
            totalMinorUnits = session.TotalMinorUnits,
            currency = session.Currency,
        }, now, session.CorrelationId);

        await db.SaveChangesAsync(ct);
        return ToSummary(session);
    }

    public async Task<SessionSummary?> GetAsync(string sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId, ct);
        return session is null ? null : ToSummary(session);
    }

    private static SessionSummary ToSummary(EdgeSession s) => new()
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
