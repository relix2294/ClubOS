using System.Text.Json;
using ClubOS.CloudApi.Api;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Edges;

/// <summary>
/// Приём sync-batch от Edge (ТЗ §4, §23.3). Каждое событие обрабатывается в своей транзакции:
/// INSERT в inbox_receipts (PK = eventId) + проекция + аудит. Повторное событие даёт конфликт
/// первичного ключа на уровне БД и помечается как дубль — без повторного эффекта.
/// </summary>
public sealed class SyncIngestor(ClubOsDbContext db, AuditWriter audit, TimeProvider time, ILogger<SyncIngestor> logger)
{
    public const int MaxBatchSize = 500;

    public async Task<SyncBatchResponse> IngestAsync(EdgeContext edge, IReadOnlyList<EventEnvelope> events,
        CancellationToken ct)
    {
        var accepted = new List<string>();
        var duplicates = new List<string>();
        var rejected = new List<RejectedEvent>();

        foreach (var evt in events.OrderBy(x => x.Sequence))
        {
            var error = Validate(edge, evt);
            if (error is not null)
            {
                rejected.Add(new RejectedEvent { EventId = evt.EventId, Reason = error });
                continue;
            }

            if (await db.InboxReceipts.AsNoTracking().AnyAsync(x => x.EventId == evt.EventId, ct))
            {
                duplicates.Add(evt.EventId);
                continue;
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                db.InboxReceipts.Add(new InboxReceipt
                {
                    EventId = evt.EventId,
                    TenantId = evt.TenantId,
                    LocationId = evt.LocationId,
                    EdgeId = edge.EdgeId,
                    EventType = evt.EventType,
                    Sequence = evt.Sequence,
                    OccurredAtUtc = evt.OccurredAtUtc,
                    ReceivedAtUtc = time.GetUtcNow()
                });

                var projectionError = await ApplyAsync(edge, evt, ct);
                if (projectionError is not null)
                {
                    await tx.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                    rejected.Add(new RejectedEvent { EventId = evt.EventId, Reason = projectionError });
                    continue;
                }

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                accepted.Add(evt.EventId);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Гонка двух одновременных доставок одного события: вторая упирается в PK. Если же событие так и не
                // записано (конфликт другой строки, например одновременное открытие смены), — не отвечаем ничего:
                // Edge пришлёт его снова, деньги не теряются.
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                if (await db.InboxReceipts.AsNoTracking().AnyAsync(x => x.EventId == evt.EventId, ct))
                {
                    duplicates.Add(evt.EventId);
                }
                else
                {
                    logger.LogWarning("Event {EventId}: conflict, will be retried by Edge", evt.EventId);
                }
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        return new SyncBatchResponse { Accepted = accepted, Duplicates = duplicates, Rejected = rejected };
    }

    private static string? Validate(EdgeContext edge, EventEnvelope evt)
    {
        if (string.IsNullOrWhiteSpace(evt.EventId) || evt.EventId.Length > 64)
        {
            return "invalid eventId";
        }

        if (evt.TenantId != edge.TenantId || evt.LocationId != edge.LocationId)
        {
            return "event outside of edge tenant/location"; // tenant isolation (ТЗ §4)
        }

        if (evt.SchemaVersion > SchemaVersions.Event)
        {
            return $"unsupported schemaVersion {evt.SchemaVersion}";
        }

        return null;
    }

    private async Task<string?> ApplyAsync(EdgeContext edge, EventEnvelope evt, CancellationToken ct)
    {
        switch (evt.EventType)
        {
            case EventTypes.SessionStarted:
                return await ApplySessionStarted(edge, evt, ContractJson.FromElement<SessionStartedPayload>(evt.Payload), ct);
            case EventTypes.SessionStartRejected:
                return await ApplySessionRejected(edge, evt, ContractJson.FromElement<SessionStartRejectedPayload>(evt.Payload), ct);
            case EventTypes.SessionEnded:
                return await ApplySessionEnded(edge, evt, ContractJson.FromElement<SessionEndedPayload>(evt.Payload), ct);
            case EventTypes.SessionExtended:
                return await ApplySessionExtended(edge, evt, ContractJson.FromElement<SessionExtendedPayload>(evt.Payload), ct);
            case EventTypes.CommandStateChanged:
                return await ApplyCommandState(edge, evt, ContractJson.FromElement<CommandStateChangedPayload>(evt.Payload), ct);
            case EventTypes.DeviceConnectivityChanged:
                return await ApplyConnectivity(edge, evt, ContractJson.FromElement<DeviceConnectivityChangedPayload>(evt.Payload), ct);
            case EventTypes.OfflinePaymentRecorded:
                return await ApplyOfflinePayment(edge, evt, ContractJson.FromElement<OfflinePaymentRecordedPayload>(evt.Payload), ct);
            default:
                return $"unknown eventType {evt.EventType}";
        }
    }

    /// <summary>
    /// Оплата в кассе Edge без интернета (D-023). Деньги уже получены — событие не отклоняется из-за состояния
    /// Cloud: нет открытой смены — она открывается автоматически (остаток на начало 0), сумма больше долга —
    /// проводится как есть (в «К расчёту» будет переплата). Идемпотентность — ключ операции из PaymentId.
    /// </summary>
    private async Task<string?> ApplyOfflinePayment(EdgeContext edge, EventEnvelope evt, OfflinePaymentRecordedPayload p,
        CancellationToken ct)
    {
        if (p.AmountMinorUnits is < 1 or > CashMath.MaxAmountMinorUnits || p.Method is not (PaymentMethods.Cash or PaymentMethods.Card))
        {
            return "invalid offline payment";
        }

        var key = $"edge-{p.PaymentId}";
        if (key.Length > 64 || await db.CashOperations.AnyAsync(x => x.TenantId == edge.TenantId && x.IdempotencyKey == key, ct))
        {
            return key.Length > 64 ? "invalid paymentId" : null; // уже проведена (повтор после потери ответа)
        }

        var session = await db.Sessions.SingleOrDefaultAsync(
            x => x.Id == p.SessionId && x.TenantId == edge.TenantId && x.LocationId == edge.LocationId, ct);
        var location = await db.Locations.AsNoTracking().SingleAsync(x => x.Id == edge.LocationId, ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == p.UserId && x.OrganizationId == edge.TenantId, ct);
        var actor = user is null ? edge.Actor : $"user:{user.Id}";

        var shift = await CashEndpoints.LockOpenShiftAsync(db, edge.TenantId, edge.LocationId, ct);
        if (shift is null)
        {
            shift = new CashShift
            {
                Id = Ids.New("shf"),
                TenantId = edge.TenantId,
                LocationId = edge.LocationId,
                Currency = location.Currency,
                OpenedBy = actor,
                OpenedAtUtc = time.GetUtcNow(),
                OpeningCashMinorUnits = 0
            };
            db.CashShifts.Add(shift);
            audit.Write(edge.TenantId, edge.LocationId, actor, "cash.shift_opened", $"location:{edge.LocationId}", AuditResults.Success,
                evt.CorrelationId, new { shiftId = shift.Id, openingCash = 0, automatic = true, reason = "offline_payment" });
        }

        var operation = new CashOperation
        {
            Id = Ids.New("cop"),
            TenantId = edge.TenantId,
            LocationId = edge.LocationId,
            ShiftId = shift.Id,
            Kind = CashOperationKinds.SessionPayment,
            Method = p.Method,
            AmountMinorUnits = p.AmountMinorUnits,
            Currency = shift.Currency,
            SessionId = session?.Id,
            DeviceId = p.DeviceId,
            Reason = $"Касса Edge без интернета, {p.RecordedAtUtc:yyyy-MM-dd HH:mm} UTC",
            CreatedBy = actor,
            CreatedAtUtc = time.GetUtcNow(),
            IdempotencyKey = key
        };
        db.CashOperations.Add(operation);
        audit.Write(edge.TenantId, edge.LocationId, actor, "cash.payment", $"device:{p.DeviceId}", AuditResults.Success, evt.CorrelationId,
            new { operationId = operation.Id, sessionId = p.SessionId, amount = p.AmountMinorUnits, p.Method, offline = true, recordedAt = p.RecordedAtUtc, via = edge.Actor });
        if (session is not null)
        {
            var paid = CashMath.Paid(await db.CashOperations.Where(x => x.SessionId == session.Id).ToListAsync(ct)) + p.AmountMinorUnits;
            CashEndpoints.EnqueueCashSync(db, edge.TenantId, edge.LocationId, session.Id, paid, time.GetUtcNow());
        }

        return null;
    }

    private async Task<Device?> FindDevice(EdgeContext edge, string deviceId, CancellationToken ct) =>
        await db.Devices.SingleOrDefaultAsync(
            x => x.Id == deviceId && x.TenantId == edge.TenantId && x.LocationId == edge.LocationId, ct);

    private async Task<string?> ApplySessionStarted(EdgeContext edge, EventEnvelope evt, SessionStartedPayload p,
        CancellationToken ct)
    {
        if (await FindDevice(edge, p.DeviceId, ct) is null)
        {
            return "unknown device";
        }

        var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == p.SessionId, ct);
        if (session is null)
        {
            // Сессия начата на Edge локально (offline / edge-cli) — Cloud узнаёт о ней из sync.
            session = NewSession(edge, p.SessionId, p.DeviceId, p.PriceSnapshot, p.Actor, p.Origin, evt);
            db.Sessions.Add(session);
        }
        else if (session.TenantId != edge.TenantId)
        {
            return "session belongs to another tenant";
        }

        if (session.State == SessionState.Created)
        {
            session.State = SessionState.Active;
        }

        session.StartedAtUtc ??= p.StartedAtUtc;
        if (p.PlannedEndAtUtc is { } plannedEnd)
        {
            // Продление могло прийти раньше (другой порядок невозможен в одном batch, но защищаемся): берём позднее.
            session.PlannedEndAtUtc = session.PlannedEndAtUtc is { } known && known > plannedEnd ? known : plannedEnd;
            session.DurationMinutes ??= (int)Math.Round((plannedEnd - p.StartedAtUtc).TotalMinutes);
        }

        audit.Write(edge.TenantId, edge.LocationId, p.Actor, "session.started", $"device:{p.DeviceId}",
            AuditResults.Success, evt.CorrelationId,
            new
            {
                sessionId = p.SessionId,
                p.StartedAtUtc,
                p.PriceSnapshot.PricePerHourMinorUnits,
                p.Origin,
                p.PlannedEndAtUtc,
                via = edge.Actor
            });
        return null;
    }

    private async Task<string?> ApplySessionExtended(EdgeContext edge, EventEnvelope evt, SessionExtendedPayload p,
        CancellationToken ct)
    {
        if (await FindDevice(edge, p.DeviceId, ct) is null)
        {
            return "unknown device";
        }

        var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == p.SessionId && x.TenantId == edge.TenantId, ct);
        if (session is null)
        {
            return "unknown session";
        }

        if (session.State != SessionState.Ended &&
            (session.PlannedEndAtUtc is null || session.PlannedEndAtUtc < p.PlannedEndAtUtc))
        {
            session.PlannedEndAtUtc = p.PlannedEndAtUtc;
        }

        audit.Write(edge.TenantId, edge.LocationId, p.Actor, "session.extended", $"device:{p.DeviceId}",
            AuditResults.Success, evt.CorrelationId,
            new { sessionId = p.SessionId, p.AddedMinutes, p.PlannedEndAtUtc, via = edge.Actor });
        return null;
    }

    private async Task<string?> ApplySessionRejected(EdgeContext edge, EventEnvelope evt, SessionStartRejectedPayload p,
        CancellationToken ct)
    {
        var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == p.SessionId && x.TenantId == edge.TenantId, ct);
        if (session is not null && SessionStateMachine.CanTransition(session.State, SessionState.Failed))
        {
            session.State = SessionState.Failed;
            session.FailureReason = p.Reason;
        }

        audit.Write(edge.TenantId, edge.LocationId, edge.Actor, "session.started", $"device:{p.DeviceId}",
            AuditResults.Failed, evt.CorrelationId, new { sessionId = p.SessionId, p.Reason });
        return null;
    }

    private async Task<string?> ApplySessionEnded(EdgeContext edge, EventEnvelope evt, SessionEndedPayload p,
        CancellationToken ct)
    {
        if (await FindDevice(edge, p.DeviceId, ct) is null)
        {
            return "unknown device";
        }

        var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == p.SessionId, ct);
        if (session is null)
        {
            session = NewSession(edge, p.SessionId, p.DeviceId, p.PriceSnapshot, p.Actor, p.Origin, evt);
            session.State = SessionState.Active;
            session.StartedAtUtc = p.StartedAtUtc;
            db.Sessions.Add(session);
        }
        else if (session.TenantId != edge.TenantId)
        {
            return "session belongs to another tenant";
        }

        if (session.State == SessionState.Ended)
        {
            return null; // завершённая сессия не редактируется (ТЗ §12.2.1)
        }

        // Итог считает Edge по снимку тарифа; Cloud фиксирует его как есть и сверяет расчёт.
        var expected = BillingCalculator.CalculateMinorUnits(p.PriceSnapshot, p.StartedAtUtc, p.EndedAtUtc - p.StartedAtUtc);
        if (expected != p.TotalMinorUnits)
        {
            logger.LogWarning("Session {SessionId}: edge total {EdgeTotal} != cloud recalculation {Expected}",
                p.SessionId, p.TotalMinorUnits, expected);
        }

        session.State = SessionState.Ended;
        session.StartedAtUtc = p.StartedAtUtc;
        session.EndedAtUtc = p.EndedAtUtc;
        session.TotalMinorUnits = p.TotalMinorUnits;
        session.ApplySnapshot(p.PriceSnapshot);
        session.EndedBy = p.Actor;
        session.EndReason = p.Reason;
        audit.Write(edge.TenantId, edge.LocationId, p.Actor, "session.ended", $"device:{p.DeviceId}",
            AuditResults.Success, evt.CorrelationId,
            new
            {
                sessionId = p.SessionId,
                p.StartedAtUtc,
                p.EndedAtUtc,
                p.TotalMinorUnits,
                currency = p.PriceSnapshot.Currency,
                via = edge.Actor,
                reason = p.Reason,
                recalculationMatches = expected == p.TotalMinorUnits
            });
        return null;
    }

    private async Task<string?> ApplyCommandState(EdgeContext edge, EventEnvelope evt, CommandStateChangedPayload p,
        CancellationToken ct)
    {
        var command = await db.DeviceCommands.SingleOrDefaultAsync(
            x => x.Id == p.CommandId && x.TenantId == edge.TenantId && x.LocationId == edge.LocationId, ct);
        if (command is null)
        {
            return "unknown command";
        }

        if (!CommandStateMachine.CanTransition(command.State, p.State))
        {
            // Устаревший или повторный переход (например, Delivered после Succeeded) — не ошибка протокола.
            logger.LogInformation("Command {CommandId}: ignore transition {From} -> {To}", command.Id, command.State, p.State);
            return null;
        }

        command.State = p.State;
        command.Error = p.Error;
        command.UpdatedAtUtc = p.AtUtc;
        if (p.State == CommandState.Succeeded && p.Output is { ValueKind: JsonValueKind.Object } output)
        {
            var raw = output.GetRawText();
            command.ResultJson = raw.Length <= RemoteLimits.MaxOutputChars ? raw : null;
        }
        var result = p.State switch
        {
            CommandState.Succeeded => AuditResults.Success,
            CommandState.Failed or CommandState.Expired => AuditResults.Failed,
            _ => p.State.ToString().ToLowerInvariant()
        };
        audit.Write(edge.TenantId, edge.LocationId, $"device:{p.DeviceId}", $"command.{command.CommandType}",
            $"device:{p.DeviceId}", result, command.CorrelationId, new { commandId = p.CommandId, state = p.State, p.Error });
        return null;
    }

    private async Task<string?> ApplyConnectivity(EdgeContext edge, EventEnvelope evt, DeviceConnectivityChangedPayload p,
        CancellationToken ct)
    {
        if (await FindDevice(edge, p.DeviceId, ct) is null)
        {
            return "unknown device";
        }

        audit.Write(edge.TenantId, edge.LocationId, edge.Actor, p.Online ? "device.online" : "device.offline",
            $"device:{p.DeviceId}", AuditResults.Success, evt.CorrelationId, new { p.AtUtc });
        return null;
    }

    private static Session NewSession(EdgeContext edge, string sessionId, string deviceId, PriceSnapshot snapshot, string actor,
        string origin, EventEnvelope evt)
    {
        var session = new Session
        {
            Id = sessionId,
            TenantId = edge.TenantId,
            DeviceId = deviceId,
            LocationId = edge.LocationId,
            State = SessionState.Created,
            Origin = origin,
            RequestedAtUtc = evt.OccurredAtUtc,
            PricePerHourMinorUnits = snapshot.PricePerHourMinorUnits,
            Currency = snapshot.Currency,
            Rounding = snapshot.Rounding,
            RuleVersion = snapshot.RuleVersion,
            StartedBy = actor,
            CorrelationId = evt.CorrelationId ?? evt.EventId
        };
        session.ApplySnapshot(snapshot);
        return session;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };
}
