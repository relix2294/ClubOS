using System.Globalization;
using System.Text.RegularExpressions;
using ClubOS.CloudApi.Auth;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.CloudApi.Security;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Api;

/// <summary>
/// Касса локации: смены, оплата сессий, возвраты, внесения и изъятия, отчёт по выручке (ТЗ §12).
/// Операции иммутабельны: ошибка исправляется новой операцией (§12.2.1). Деньги — minor units.
/// Изменения выполняются в транзакции с блокировкой строки открытой смены (и сессии), поэтому две
/// одновременные оплаты не превысят долг, а оплата не попадёт в смену, которую в этот момент закрывают.
/// </summary>
public static partial class CashEndpoints
{
    /// <summary>Сколько дней назад искать неоплаченные завершённые сессии.</summary>
    public const int PayableLookbackDays = 30;

    public const int MaxReportDays = 92;
    public const int MaxOperationsInDesk = 100;

    public static void MapCashEndpoints(this IEndpointRouteBuilder app)
    {
        var cash = app.MapGroup("/api/v1").WithTags("Cash").RequirePermission(Permissions.CashOperate);
        cash.MapGet("/locations/{locationId}/cash", GetDesk);
        cash.MapPost("/locations/{locationId}/cash/shifts", OpenShift);
        cash.MapPost("/cash/shifts/{shiftId}/close", CloseShift);
        cash.MapPost("/locations/{locationId}/cash/movements", AddMovement);
        cash.MapPost("/sessions/{sessionId}/payments", PaySession);
        cash.MapPost("/sessions/{sessionId}/refunds", RefundSession);

        var reports = app.MapGroup("/api/v1").WithTags("Reports").RequirePermission(Permissions.ReportsView);
        reports.MapGet("/locations/{locationId}/cash/shifts", ListShifts);
        reports.MapGet("/cash/shifts/{shiftId}", GetShift);
        reports.MapGet("/reports/revenue", Revenue);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex IdempotencyKeyPattern();

    // ---------------- Чтение ----------------

    private static async Task<IResult> GetDesk(string locationId, HttpContext http, LocationScope scope, ClubOsDbContext db,
        TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var location = await FindLocationAsync(db, scope, staff, locationId, ct);
        if (location is null)
        {
            return Problems.NotFound("Локация");
        }

        var shift = await db.CashShifts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.LocationId == locationId && x.ClosedAtUtc == null, ct);
        var operations = shift is null
            ? []
            : await db.CashOperations.AsNoTracking().Where(x => x.ShiftId == shift.Id)
                .OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);

        var payable = await PayableAsync(db, staff.TenantId, locationId, time.GetUtcNow(), ct);
        var shiftView = shift is null ? null : await ShiftViewAsync(db, shift, operations, ct);
        var operationViews = await OperationViewsAsync(db, operations.Take(MaxOperationsInDesk).ToList(), ct);
        return Results.Ok(new CashDeskView(locationId, location.Currency, shiftView, payable, operationViews));
    }

    private static async Task<IResult> ListShifts(string locationId, int? limit, HttpContext http, LocationScope scope,
        ClubOsDbContext db, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        if (await FindLocationAsync(db, scope, staff, locationId, ct) is null)
        {
            return Problems.NotFound("Локация");
        }

        var shifts = await db.CashShifts.AsNoTracking().Where(x => x.TenantId == staff.TenantId && x.LocationId == locationId)
            .OrderByDescending(x => x.OpenedAtUtc).Take(Math.Clamp(limit ?? 30, 1, 200)).ToListAsync(ct);
        var ids = shifts.Select(x => x.Id).ToList();
        var operations = (await db.CashOperations.AsNoTracking().Where(x => ids.Contains(x.ShiftId)).ToListAsync(ct))
            .ToLookup(x => x.ShiftId);

        var views = new List<CashShiftView>();
        foreach (var shift in shifts)
        {
            views.Add(await ShiftViewAsync(db, shift, operations[shift.Id].ToList(), ct));
        }

        return Results.Ok(views);
    }

    private static async Task<IResult> GetShift(string shiftId, HttpContext http, LocationScope scope, ClubOsDbContext db,
        CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var shift = await db.CashShifts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == shiftId && x.TenantId == staff.TenantId, ct);
        if (shift is null || !await scope.CanAccessAsync(shift.LocationId, ct))
        {
            return Problems.NotFound("Смена");
        }

        var operations = await db.CashOperations.AsNoTracking().Where(x => x.ShiftId == shift.Id)
            .OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
        return Results.Ok(new
        {
            shift = await ShiftViewAsync(db, shift, operations, ct),
            operations = await OperationViewsAsync(db, operations, ct)
        });
    }

    // ---------------- Смены ----------------

    private static async Task<IResult> OpenShift(string locationId, OpenShiftRequest request, HttpContext http, LocationScope scope,
        ClubOsDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var location = await FindLocationAsync(db, scope, staff, locationId, ct);
        if (location is null)
        {
            return Problems.NotFound("Локация");
        }

        if (request.OpeningCashMinorUnits is < 0 or > CashMath.MaxAmountMinorUnits)
        {
            return Problems.Validation("invalid_amount", "Остаток наличных на начало смены — от 0 до 1 000 000.");
        }

        if (await db.CashShifts.AnyAsync(x => x.LocationId == locationId && x.ClosedAtUtc == null, ct))
        {
            return Problems.Conflict("shift_already_open", "В локации уже открыта смена. Закройте её, чтобы открыть новую.");
        }

        var shift = new CashShift
        {
            Id = Ids.New("shf"),
            TenantId = staff.TenantId,
            LocationId = locationId,
            Currency = location.Currency,
            OpenedBy = staff.Actor,
            OpenedAtUtc = time.GetUtcNow(),
            OpeningCashMinorUnits = request.OpeningCashMinorUnits
        };
        db.CashShifts.Add(shift);
        audit.Write(staff.TenantId, locationId, staff.Actor, "cash.shift_opened", $"shift:{shift.Id}", AuditResults.Success,
            details: new { openingCash = shift.OpeningCashMinorUnits, shift.Currency });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Две одновременные попытки: вторая упирается в уникальный индекс открытой смены.
            return Problems.Conflict("shift_already_open", "В локации уже открыта смена. Закройте её, чтобы открыть новую.");
        }

        return Results.Created($"/api/v1/cash/shifts/{shift.Id}", await ShiftViewAsync(db, shift, [], ct));
    }

    private static async Task<IResult> CloseShift(string shiftId, CloseShiftRequest request, HttpContext http, LocationScope scope,
        ClubOsDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        if (request.CountedCashMinorUnits is < 0 or > CashMath.MaxAmountMinorUnits)
        {
            return Problems.Validation("invalid_amount", "Пересчитанная сумма наличных — от 0 до 1 000 000.");
        }

        var note = request.Note?.Trim();
        if (note is { Length: > 500 })
        {
            return Problems.Validation("invalid_note", "Комментарий — не длиннее 500 символов.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var shift = (await db.CashShifts
            .FromSql($"""SELECT * FROM cash_shifts WHERE "Id" = {shiftId} AND "TenantId" = {staff.TenantId} FOR UPDATE""")
            .ToListAsync(ct)).SingleOrDefault();
        if (shift is null || !await scope.CanAccessAsync(shift.LocationId, ct))
        {
            return Problems.NotFound("Смена");
        }

        var operations = await db.CashOperations.Where(x => x.ShiftId == shift.Id).ToListAsync(ct);
        if (shift.ClosedAtUtc is not null)
        {
            return Problems.Conflict("shift_closed", "Смена уже закрыта.");
        }

        var totals = CashMath.Totals(shift.OpeningCashMinorUnits, operations);
        shift.ClosedAtUtc = time.GetUtcNow();
        shift.ClosedBy = staff.Actor;
        shift.ExpectedCashMinorUnits = totals.ExpectedCashMinorUnits;
        shift.CountedCashMinorUnits = request.CountedCashMinorUnits;
        shift.CloseNote = string.IsNullOrEmpty(note) ? null : note;
        var discrepancy = request.CountedCashMinorUnits - totals.ExpectedCashMinorUnits;
        audit.Write(staff.TenantId, shift.LocationId, staff.Actor, "cash.shift_closed", $"shift:{shift.Id}", AuditResults.Success,
            details: new
            {
                expectedCash = totals.ExpectedCashMinorUnits,
                countedCash = request.CountedCashMinorUnits,
                discrepancy,
                revenue = totals.RevenueMinorUnits,
                note = shift.CloseNote
            });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Results.Ok(await ShiftViewAsync(db, shift, operations, ct));
    }

    // ---------------- Операции ----------------

    private static async Task<IResult> AddMovement(string locationId, CashMovementRequest request, HttpContext http,
        LocationScope scope, ClubOsDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        if (request.Kind is not (CashOperationKinds.CashIn or CashOperationKinds.CashOut))
        {
            return Problems.Validation("invalid_kind", "Тип — CashIn (внесение) или CashOut (изъятие).");
        }

        if (ValidateAmount(request.AmountMinorUnits) is { } amountError)
        {
            return amountError;
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 3 or > 200)
        {
            return Problems.Validation("invalid_reason", "Укажите основание (3–200 символов), например «Инкассация».");
        }

        if (ValidateKey(request.IdempotencyKey) is { } keyError)
        {
            return keyError;
        }

        if (await FindLocationAsync(db, scope, staff, locationId, ct) is null)
        {
            return Problems.NotFound("Локация");
        }

        if (await ReplayAsync(db, staff.TenantId, request.IdempotencyKey, request.Kind, null, ct) is { } replay)
        {
            return replay;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var shift = await LockOpenShiftAsync(db, staff.TenantId, locationId, ct);
        if (shift is null)
        {
            return ShiftNotOpen();
        }

        var amount = request.Kind == CashOperationKinds.CashIn ? request.AmountMinorUnits : -request.AmountMinorUnits;
        if (amount < 0 && await CashOnHandAsync(db, shift, ct) + amount < 0)
        {
            return Problems.Conflict("insufficient_cash", "В кассе меньше наличных, чем нужно изъять.");
        }

        var operation = NewOperation(staff, shift, request.Kind, PaymentMethods.Cash, amount, time, request.IdempotencyKey);
        operation.Reason = reason;
        db.CashOperations.Add(operation);
        audit.Write(staff.TenantId, locationId, staff.Actor,
            request.Kind == CashOperationKinds.CashIn ? "cash.cash_in" : "cash.cash_out", $"shift:{shift.Id}", AuditResults.Success,
            details: new { operationId = operation.Id, amount = operation.AmountMinorUnits, reason });
        return await CommitAsync(db, tx, operation, staff.TenantId, request.IdempotencyKey, request.Kind, null, ct);
    }

    private static async Task<IResult> PaySession(string sessionId, SessionPaymentRequest request, HttpContext http,
        LocationScope scope, ClubOsDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        if (!PaymentMethods.IsValid(request.Method))
        {
            return Problems.Validation("invalid_method", "Способ оплаты — Cash, Card или Balance.");
        }

        if (ValidateAmount(request.AmountMinorUnits) is { } amountError)
        {
            return amountError;
        }

        if (ValidateKey(request.IdempotencyKey) is { } keyError)
        {
            return keyError;
        }

        var locationId = await db.Sessions.Where(x => x.Id == sessionId && x.TenantId == staff.TenantId)
            .Select(x => x.LocationId).SingleOrDefaultAsync(ct);
        if (locationId is null || !await scope.CanAccessAsync(locationId, ct))
        {
            return Problems.NotFound("Сессия");
        }

        if (await ReplayAsync(db, staff.TenantId, request.IdempotencyKey, CashOperationKinds.SessionPayment, sessionId, ct) is { } replay)
        {
            return replay;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var shift = await LockOpenShiftAsync(db, staff.TenantId, locationId, ct);
        if (shift is null)
        {
            return ShiftNotOpen();
        }

        var session = await LockSessionAsync(db, sessionId, ct);
        var charge = CashMath.Charge(session);
        if (charge is null)
        {
            return Problems.Conflict("session_not_payable",
                "Сумма ещё неизвестна: сессия без лимита оплачивается после завершения.");
        }

        var paid = CashMath.Paid(await db.CashOperations.Where(x => x.SessionId == sessionId).ToListAsync(ct));
        var due = charge.Value - paid;
        if (due <= 0)
        {
            return Problems.Conflict("session_paid", "Сессия уже оплачена.");
        }

        if (request.AmountMinorUnits > due)
        {
            return Problems.Validation("amount_exceeds_due",
                $"Сумма больше долга по сессии ({due / 100m:0.00} {session.Currency}). Сдачу считайте отдельно.");
        }

        Client? client = null;
        if (request.Method == PaymentMethods.Balance)
        {
            (client, var clientError) = await LockBalanceClientAsync(db, staff.TenantId, request.ClientId ?? session.ClientId,
                session.Currency, ct);
            if (clientError is not null)
            {
                return clientError;
            }

            if (client!.BalanceMinorUnits < request.AmountMinorUnits)
            {
                return Problems.Conflict("insufficient_balance",
                    $"На балансе {client.BalanceMinorUnits / 100m:0.00} {client.Currency} — меньше суммы оплаты.");
            }
        }

        var operation = NewOperation(staff, shift, CashOperationKinds.SessionPayment, request.Method!, request.AmountMinorUnits, time,
            request.IdempotencyKey);
        operation.SessionId = session.Id;
        operation.DeviceId = session.DeviceId;
        operation.ClientId = client?.Id;
        db.CashOperations.Add(operation);
        if (client is not null)
        {
            AppendLedger(db, client, ClientLedgerKinds.SessionPayment, -request.AmountMinorUnits, staff, time, locationId,
                session.Id, operation.Id, reason: null);
        }
        audit.Write(staff.TenantId, locationId, staff.Actor, "cash.payment", $"device:{session.DeviceId}", AuditResults.Success,
            session.CorrelationId,
            new { operationId = operation.Id, sessionId, amount = request.AmountMinorUnits, request.Method, charge, dueBefore = due });
        return await CommitAsync(db, tx, operation, staff.TenantId, request.IdempotencyKey, CashOperationKinds.SessionPayment, sessionId, ct);
    }

    /// <summary>
    /// Возврат по сессии. Переплату (клиент внёс больше, чем вышло по факту, — например, сессию завершили раньше)
    /// может вернуть любой кассир. Возврат сверх переплаты — только с правом <see cref="Permissions.CashRefund"/>.
    /// </summary>
    private static async Task<IResult> RefundSession(string sessionId, SessionRefundRequest request, HttpContext http,
        LocationScope scope, ClubOsDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        if (!PaymentMethods.IsValid(request.Method))
        {
            return Problems.Validation("invalid_method", "Способ возврата — Cash, Card или Balance.");
        }

        if (ValidateAmount(request.AmountMinorUnits) is { } amountError)
        {
            return amountError;
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 3 or > 200)
        {
            return Problems.Validation("invalid_reason", "Укажите причину возврата (3–200 символов).");
        }

        if (ValidateKey(request.IdempotencyKey) is { } keyError)
        {
            return keyError;
        }

        var locationId = await db.Sessions.Where(x => x.Id == sessionId && x.TenantId == staff.TenantId)
            .Select(x => x.LocationId).SingleOrDefaultAsync(ct);
        if (locationId is null || !await scope.CanAccessAsync(locationId, ct))
        {
            return Problems.NotFound("Сессия");
        }

        if (await ReplayAsync(db, staff.TenantId, request.IdempotencyKey, CashOperationKinds.Refund, sessionId, ct) is { } replay)
        {
            return replay;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var shift = await LockOpenShiftAsync(db, staff.TenantId, locationId, ct);
        if (shift is null)
        {
            return ShiftNotOpen();
        }

        var session = await LockSessionAsync(db, sessionId, ct);
        var paid = CashMath.Paid(await db.CashOperations.Where(x => x.SessionId == sessionId).ToListAsync(ct));
        if (request.AmountMinorUnits > paid)
        {
            return Problems.Validation("amount_exceeds_paid", $"Вернуть можно не больше оплаченного ({paid / 100m:0.00} {session.Currency}).");
        }

        // Переплата известна только при известном начислении; для идущей сессии без лимита её нет.
        var overpaid = CashMath.Charge(session) is { } charge ? Math.Max(paid - charge, 0) : 0;
        if (request.AmountMinorUnits > overpaid && !Permissions.Has(staff.Role, Permissions.CashRefund))
        {
            return Problems.Forbidden(overpaid > 0
                ? $"Без администратора можно вернуть только переплату ({overpaid / 100m:0.00} {session.Currency})."
                : "Возврат оплаченной сессии делает администратор.");
        }

        if (request.Method == PaymentMethods.Cash && await CashOnHandAsync(db, shift, ct) < request.AmountMinorUnits)
        {
            return Problems.Conflict("insufficient_cash", "В кассе меньше наличных, чем нужно вернуть.");
        }

        Client? client = null;
        if (request.Method == PaymentMethods.Balance)
        {
            // Чей баланс: указанный, клиент сессии или тот, с чьего баланса платили.
            var clientId = request.ClientId ?? session.ClientId ?? await db.CashOperations
                .Where(x => x.SessionId == sessionId && x.ClientId != null).Select(x => x.ClientId).FirstOrDefaultAsync(ct);
            (client, var clientError) = await LockBalanceClientAsync(db, staff.TenantId, clientId, session.Currency, ct);
            if (clientError is not null)
            {
                return clientError;
            }
        }

        var operation = NewOperation(staff, shift, CashOperationKinds.Refund, request.Method!, -request.AmountMinorUnits, time,
            request.IdempotencyKey);
        operation.SessionId = session.Id;
        operation.DeviceId = session.DeviceId;
        operation.Reason = reason;
        operation.ClientId = client?.Id;
        db.CashOperations.Add(operation);
        if (client is not null)
        {
            AppendLedger(db, client, ClientLedgerKinds.SessionRefund, request.AmountMinorUnits, staff, time, locationId,
                session.Id, operation.Id, reason);
        }
        audit.Write(staff.TenantId, locationId, staff.Actor, "cash.refund", $"device:{session.DeviceId}", AuditResults.Success,
            session.CorrelationId,
            new { operationId = operation.Id, sessionId, amount = request.AmountMinorUnits, request.Method, reason, paidBefore = paid, overpaid });
        return await CommitAsync(db, tx, operation, staff.TenantId, request.IdempotencyKey, CashOperationKinds.Refund, sessionId, ct);
    }

    // ---------------- Отчёт ----------------

    /// <summary>
    /// Выручка по дням в часовом поясе локации: начислено по завершённым сессиям, принято наличными и картой,
    /// возвраты, итог. «Не оплачено» — долг по сессиям, завершённым в периоде.
    /// </summary>
    private static async Task<IResult> Revenue(string? locationId, string? from, string? to, HttpContext http, LocationScope scope,
        ClubOsDbContext db, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var location = locationId is null ? null : await FindLocationAsync(db, scope, staff, locationId, ct);
        if (location is null)
        {
            return Problems.NotFound("Локация");
        }

        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDate) ||
            !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate) ||
            toDate < fromDate || toDate.DayNumber - fromDate.DayNumber >= MaxReportDays)
        {
            return Problems.Validation("invalid_period", $"Период: from и to в формате ГГГГ-ММ-ДД, не больше {MaxReportDays} дней.");
        }

        if (FindZone(location.Timezone) is not { } zone)
        {
            return Problems.Validation("invalid_timezone", $"Часовой пояс локации «{location.Timezone}» неизвестен серверу.");
        }

        var fromUtc = LocalMidnightUtc(fromDate, zone);
        var toUtc = LocalMidnightUtc(toDate.AddDays(1), zone);

        var operations = await db.CashOperations.AsNoTracking()
            .Where(x => x.TenantId == staff.TenantId && x.LocationId == location.Id &&
                        x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc < toUtc &&
                        (x.Kind == CashOperationKinds.SessionPayment || x.Kind == CashOperationKinds.Refund ||
                         x.Kind == CashOperationKinds.BalanceTopUp || x.Kind == CashOperationKinds.ProductSale ||
                         x.Kind == CashOperationKinds.ProductRefund))
            .ToListAsync(ct);
        var sessions = await db.Sessions.AsNoTracking()
            .Where(x => x.TenantId == staff.TenantId && x.LocationId == location.Id && x.State == SessionState.Ended &&
                        x.EndedAtUtc >= fromUtc && x.EndedAtUtc < toUtc)
            .Select(x => new { x.Id, x.EndedAtUtc, x.TotalMinorUnits })
            .ToListAsync(ct);
        var sessionIds = sessions.Select(x => x.Id).ToList();
        var paidBySession = (await db.CashOperations.AsNoTracking().Where(x => x.SessionId != null && sessionIds.Contains(x.SessionId))
                .ToListAsync(ct))
            .GroupBy(x => x.SessionId!).ToDictionary(g => g.Key, g => CashMath.Paid(g));

        DateOnly LocalDate(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);

        var days = new List<RevenueDayView>();
        for (var day = fromDate; day <= toDate; day = day.AddDays(1))
        {
            var dayOps = operations.Where(o => LocalDate(o.CreatedAtUtc) == day).ToList();
            var daySessions = sessions.Where(s => LocalDate(s.EndedAtUtc!.Value) == day).ToList();
            days.Add(Day(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), daySessions.Count,
                daySessions.Sum(s => s.TotalMinorUnits ?? 0), dayOps));
        }

        var totals = new RevenueDayView("total", days.Sum(d => d.SessionsEnded), days.Sum(d => d.ChargedMinorUnits),
            days.Sum(d => d.CashMinorUnits), days.Sum(d => d.CardMinorUnits), days.Sum(d => d.RefundsMinorUnits),
            days.Sum(d => d.NetMinorUnits), days.Sum(d => d.BalanceMinorUnits), days.Sum(d => d.TopUpsMinorUnits),
            days.Sum(d => d.ProductsMinorUnits));
        var unpaid = sessions.Sum(s => Math.Max((s.TotalMinorUnits ?? 0) - paidBySession.GetValueOrDefault(s.Id), 0));

        return Results.Ok(new RevenueReportView(location.Id, location.Currency, location.Timezone,
            fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            days, totals, unpaid));
    }

    public static RevenueDayView Day(string date, int sessionsEnded, long charged, IReadOnlyCollection<CashOperation> operations)
    {
        var payments = operations.Where(o => o.Kind == CashOperationKinds.SessionPayment).ToList();
        var cash = payments.Where(o => o.Method == PaymentMethods.Cash).Sum(o => o.AmountMinorUnits);
        var card = payments.Where(o => o.Method == PaymentMethods.Card).Sum(o => o.AmountMinorUnits);
        var balance = payments.Where(o => o.Method == PaymentMethods.Balance).Sum(o => o.AmountMinorUnits);
        var refunds = -operations.Where(o => o.Kind == CashOperationKinds.Refund).Sum(o => o.AmountMinorUnits);
        var topUps = operations.Where(o => o.Kind == CashOperationKinds.BalanceTopUp).Sum(o => o.AmountMinorUnits);
        // Бар: чеки минус возвраты чеков, любым способом оплаты.
        var products = operations.Where(o => o.Kind is CashOperationKinds.ProductSale or CashOperationKinds.ProductRefund)
            .Sum(o => o.AmountMinorUnits);
        return new RevenueDayView(date, sessionsEnded, charged, cash, card, refunds, cash + card + balance - refunds + products, balance,
            topUps, products);
    }

    /// <summary>
    /// Часовой пояс по IANA-имени (как хранится у локации). В Linux-контейнере .NET читает tzdata напрямую;
    /// на Windows IANA-имя переводится в Windows-идентификатор, если это позволяет среда (ICU). null — пояс неизвестен.
    /// </summary>
    public static TimeZoneInfo? FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId) &&
                   TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out var zone)
                ? zone
                : null;
        }
    }

    /// <summary>Начало местных суток в UTC (с учётом перехода на летнее время, если он есть в поясе).</summary>
    public static DateTimeOffset LocalMidnightUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30); // полночь попала в «пропущенный» час
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    // ---------------- Вспомогательное ----------------

    internal static async Task<Location?> FindLocationAsync(ClubOsDbContext db, LocationScope scope, StaffContext staff,
        string locationId, CancellationToken ct)
    {
        var location = await db.Locations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == locationId && x.OrganizationId == staff.TenantId, ct);
        return location is not null && await scope.CanAccessAsync(location.Id, ct) ? location : null;
    }

    internal static async Task<CashShift?> LockOpenShiftAsync(ClubOsDbContext db, string tenantId, string locationId,
        CancellationToken ct) =>
        (await db.CashShifts
            .FromSql($"""
                      SELECT * FROM cash_shifts
                      WHERE "TenantId" = {tenantId} AND "LocationId" = {locationId} AND "ClosedAtUtc" IS NULL
                      FOR UPDATE
                      """)
            .ToListAsync(ct)).SingleOrDefault();

    private static async Task<Session> LockSessionAsync(ClubOsDbContext db, string sessionId, CancellationToken ct) =>
        (await db.Sessions.FromSql($"""SELECT * FROM sessions WHERE "Id" = {sessionId} FOR UPDATE""").ToListAsync(ct)).Single();

    internal static async Task<long> CashOnHandAsync(ClubOsDbContext db, CashShift shift, CancellationToken ct) =>
        shift.OpeningCashMinorUnits + await db.CashOperations
            .Where(x => x.ShiftId == shift.Id && x.Method == PaymentMethods.Cash).SumAsync(x => x.AmountMinorUnits, ct);

    internal static CashOperation NewOperation(StaffContext staff, CashShift shift, string kind, string method, long amount,
        TimeProvider time, string? idempotencyKey) => new()
        {
            Id = Ids.New("cop"),
            TenantId = staff.TenantId,
            LocationId = shift.LocationId,
            ShiftId = shift.Id,
            Kind = kind,
            Method = method,
            AmountMinorUnits = amount,
            Currency = shift.Currency,
            CreatedBy = staff.Actor,
            CreatedAtUtc = time.GetUtcNow(),
            IdempotencyKey = idempotencyKey
        };

    /// <summary>Клиент для операции с балансом под блокировкой строки (после блокировок смены и сессии).</summary>
    public static async Task<(Client? Client, IResult? Error)> LockBalanceClientAsync(ClubOsDbContext db, string tenantId,
        string? clientId, string currency, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            return (null, Problems.Validation("client_required", "Выберите клиента, с чьим балансом операция."));
        }

        var client = (await db.Clients
            .FromSql($"""SELECT * FROM clients WHERE "Id" = {clientId} AND "TenantId" = {tenantId} FOR UPDATE""")
            .ToListAsync(ct)).SingleOrDefault();
        if (client is null)
        {
            return (null, Problems.NotFound("Клиент"));
        }

        if (client.IsBlocked)
        {
            return (null, Problems.Conflict("client_blocked", "Клиент заблокирован."));
        }

        return client.Currency != currency
            ? (null, Problems.Conflict("currency_mismatch", $"Баланс клиента в {client.Currency}, а сумма — в {currency}."))
            : (client, null);
    }

    /// <summary>Запись в журнал баланса и новый баланс клиента — в той же транзакции, что и операция.</summary>
    public static ClientLedgerEntry AppendLedger(ClubOsDbContext db, Client client, string kind, long amount, StaffContext staff,
        TimeProvider time, string? locationId, string? sessionId, string? cashOperationId, string? reason)
    {
        client.BalanceMinorUnits += amount;
        var entry = new ClientLedgerEntry
        {
            Id = Ids.New("cle"),
            TenantId = client.TenantId,
            ClientId = client.Id,
            Kind = kind,
            AmountMinorUnits = amount,
            BalanceAfterMinorUnits = client.BalanceMinorUnits,
            LocationId = locationId,
            SessionId = sessionId,
            CashOperationId = cashOperationId,
            Reason = reason,
            CreatedBy = staff.Actor,
            CreatedAtUtc = time.GetUtcNow()
        };
        db.ClientLedger.Add(entry);
        return entry;
    }

    internal static IResult? ValidateAmount(long amount) => amount is < 1 or > CashMath.MaxAmountMinorUnits
        ? Problems.Validation("invalid_amount", "Сумма должна быть больше нуля и не больше 1 000 000.")
        : null;

    internal static IResult? ValidateKey(string? key) => key is null || IdempotencyKeyPattern().IsMatch(key)
        ? null
        : Problems.Validation("invalid_idempotency_key", "idempotencyKey — 8–64 символа: латиница, цифры, «-», «_».");

    internal static IResult ShiftNotOpen() =>
        Problems.Conflict("shift_not_open", "Смена в кассе не открыта. Откройте смену, чтобы принимать деньги.");

    /// <summary>Повтор запроса с тем же ключом возвращает уже созданную операцию; ключ от другой операции — 409.</summary>
    internal static async Task<IResult?> ReplayAsync(ClubOsDbContext db, string tenantId, string? key, string kind, string? sessionId,
        CancellationToken ct)
    {
        if (key is null)
        {
            return null;
        }

        var existing = await db.CashOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == key, ct);
        if (existing is null)
        {
            return null;
        }

        return existing.Kind == kind && existing.SessionId == sessionId
            ? Results.Ok((await OperationViewsAsync(db, [existing], ct))[0])
            : Problems.Conflict("idempotency_key_reused", "Ключ идемпотентности уже использован для другой операции.");
    }

    internal static async Task<IResult> CommitAsync(ClubOsDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        CashOperation operation, string tenantId, string? key, string kind, string? sessionId, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex) && key is not null)
        {
            // Параллельный повтор с тем же ключом успел первым.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return await ReplayAsync(db, tenantId, key, kind, sessionId, ct)
                   ?? Problems.Conflict("idempotency_key_reused", "Ключ идемпотентности уже использован.");
        }

        return Results.Created($"/api/v1/cash/shifts/{operation.ShiftId}", (await OperationViewsAsync(db, [operation], ct))[0]);
    }

    internal static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    /// <summary>Сессии локации с незакрытым расчётом: долг или переплата. Свежие сверху.</summary>
    private static async Task<List<PayableSessionView>> PayableAsync(ClubOsDbContext db, string tenantId, string locationId,
        DateTimeOffset now, CancellationToken ct)
    {
        var since = now.AddDays(-PayableLookbackDays);
        var sessions = await db.Sessions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.LocationId == locationId &&
                        ((x.State == SessionState.Ended && x.EndedAtUtc >= since) ||
                         (x.State == SessionState.Active && x.PlannedEndAtUtc != null)))
            .ToListAsync(ct);
        var ids = sessions.Select(x => x.Id).ToList();
        var paid = (await db.CashOperations.AsNoTracking().Where(x => x.SessionId != null && ids.Contains(x.SessionId)).ToListAsync(ct))
            .GroupBy(x => x.SessionId!).ToDictionary(g => g.Key, g => CashMath.Paid(g));
        var deviceIds = sessions.Select(x => x.DeviceId).Distinct().ToList();
        var names = await db.Devices.AsNoTracking().Where(x => deviceIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        var clientIds = sessions.Select(x => x.ClientId).OfType<string>().Distinct().ToList();
        var clients = await db.Clients.AsNoTracking().Where(x => clientIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);

        return sessions
            .Select(s => (Session: s, Charge: CashMath.Charge(s) ?? 0, Paid: paid.GetValueOrDefault(s.Id)))
            .Where(x => x.Charge != x.Paid)
            .OrderByDescending(x => x.Session.EndedAtUtc ?? x.Session.StartedAtUtc)
            .Select(x => new PayableSessionView(x.Session.Id, x.Session.DeviceId, names.GetValueOrDefault(x.Session.DeviceId, x.Session.DeviceId),
                x.Session.State, x.Session.StartedAtUtc, x.Session.EndedAtUtc, x.Session.PlannedEndAtUtc, x.Session.Currency,
                x.Charge, x.Paid, x.Charge - x.Paid, x.Session.ClientId,
                x.Session.ClientId is { } cid && clients.TryGetValue(cid, out var c) ? c.DisplayName : null,
                x.Session.ClientId is { } cid2 && clients.TryGetValue(cid2, out var c2) ? c2.BalanceMinorUnits : null))
            .ToList();
    }

    private static async Task<Dictionary<string, string>> NamesAsync(ClubOsDbContext db, IEnumerable<string?> actors, CancellationToken ct)
    {
        var userIds = actors.OfType<string>().Where(a => a.StartsWith("user:", StringComparison.Ordinal))
            .Select(a => a["user:".Length..]).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        return users.ToDictionary(x => "user:" + x.Key, x => x.Value);
    }

    private static async Task<CashShiftView> ShiftViewAsync(ClubOsDbContext db, CashShift shift, IReadOnlyCollection<CashOperation> operations,
        CancellationToken ct)
    {
        var names = await NamesAsync(db, [shift.OpenedBy, shift.ClosedBy], ct);
        var totals = CashMath.Totals(shift.OpeningCashMinorUnits, operations);
        // У закрытой смены ожидаемая сумма — зафиксированная при закрытии.
        if (shift.ExpectedCashMinorUnits is { } fixedExpected)
        {
            totals = totals with { ExpectedCashMinorUnits = fixedExpected };
        }

        return new CashShiftView(shift.Id, shift.LocationId, shift.Currency, shift.OpenedBy,
            names.GetValueOrDefault(shift.OpenedBy, shift.OpenedBy), shift.OpenedAtUtc, shift.OpeningCashMinorUnits,
            shift.ClosedBy, shift.ClosedBy is null ? null : names.GetValueOrDefault(shift.ClosedBy, shift.ClosedBy), shift.ClosedAtUtc,
            shift.CountedCashMinorUnits,
            shift.CountedCashMinorUnits - shift.ExpectedCashMinorUnits,
            shift.CloseNote, totals);
    }

    internal static async Task<List<CashOperationView>> OperationViewsAsync(ClubOsDbContext db, IReadOnlyList<CashOperation> operations,
        CancellationToken ct)
    {
        var names = await NamesAsync(db, operations.Select(o => o.CreatedBy), ct);
        var deviceIds = operations.Select(o => o.DeviceId).OfType<string>().Distinct().ToList();
        var devices = await db.Devices.AsNoTracking().Where(x => deviceIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        var clientIds = operations.Select(o => o.ClientId).OfType<string>().Distinct().ToList();
        var clients = await db.Clients.AsNoTracking().Where(x => clientIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        return operations.Select(o => new CashOperationView(o.Id, o.ShiftId, o.Kind, o.Method, o.AmountMinorUnits, o.Currency,
            o.SessionId, o.DeviceId, o.DeviceId is null ? null : devices.GetValueOrDefault(o.DeviceId), o.Reason, o.CreatedBy,
            names.GetValueOrDefault(o.CreatedBy, o.CreatedBy), o.CreatedAtUtc, o.ClientId,
            o.ClientId is null ? null : clients.GetValueOrDefault(o.ClientId), o.SaleId)).ToList();
    }
}
