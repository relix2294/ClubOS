using ClubOS.Contracts;
using Microsoft.Data.Sqlite;

namespace ClubOS.EdgeController.Storage;

public sealed record OfflineStaffMember(string UserId, string DisplayName, string PinHash);

/// <summary>Сессия к расчёту в кассе Edge: начислено по данным Edge, оплачено в Cloud (последнее известное) и на Edge.</summary>
public sealed record OfflinePayable(
    string SessionId,
    string DeviceId,
    string DeviceName,
    SessionState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    DateTimeOffset? PlannedEndAtUtc,
    string Currency,
    long ChargeMinorUnits,
    long CloudPaidMinorUnits,
    long OfflinePaidMinorUnits,
    long DueMinorUnits);

public sealed record OfflinePayment(
    string PaymentId,
    string SessionId,
    string DeviceId,
    string DeviceName,
    long AmountMinorUnits,
    string Method,
    string UserName,
    DateTimeOffset RecordedAtUtc,
    bool Sent);

public enum OfflinePaymentOutcome
{
    Recorded,
    Replayed,
    UnknownSession,
    NotPayable,
    ExceedsDue
}

/// <summary>Касса Edge без интернета (D-023): кассиры, долги по сессиям, оплаты в outbox.</summary>
public sealed partial class EdgeStore
{
    /// <summary>Сколько назад показывать завершённые сессии к расчёту.</summary>
    public static readonly TimeSpan OfflinePayableWindow = TimeSpan.FromHours(48);

    private static void ReplaceOfflineStaff(SqliteConnection c, SqliteTransaction tx, IReadOnlyList<EdgeOfflineStaff> staff)
    {
        c.Exec(tx, "DELETE FROM offline_staff");
        foreach (var s in staff)
        {
            c.Exec(tx, "INSERT INTO offline_staff (user_id, display_name, pin_hash) VALUES ($id, $name, $hash)",
                ("$id", s.UserId), ("$name", s.DisplayName), ("$hash", s.PinHash));
        }
    }

    private static void ApplyCashSync(SqliteConnection c, SqliteTransaction tx, CashSyncCommand sync, DateTimeOffset now) =>
        c.Exec(tx, """
            INSERT INTO session_cloud_paid (session_id, paid_minor_units, updated_at_utc) VALUES ($s, $p, $now)
            ON CONFLICT(session_id) DO UPDATE SET paid_minor_units = excluded.paid_minor_units, updated_at_utc = excluded.updated_at_utc
            """, ("$s", sync.SessionId), ("$p", sync.PaidMinorUnits), ("$now", now));

    public IReadOnlyList<OfflineStaffMember> ListOfflineStaff() =>
        database.Read(c => c.Query(null, "SELECT user_id, display_name, pin_hash FROM offline_staff ORDER BY display_name",
            r => new OfflineStaffMember(r.S("user_id"), r.S("display_name"), r.S("pin_hash"))));

    public OfflineStaffMember? GetOfflineStaff(string userId) =>
        database.Read(c => c.Query(null, "SELECT user_id, display_name, pin_hash FROM offline_staff WHERE user_id = $id",
            r => new OfflineStaffMember(r.S("user_id"), r.S("display_name"), r.S("pin_hash")), ("$id", userId)).FirstOrDefault());

    /// <summary>
    /// Долг по сессии = начислено − оплачено в Cloud (по CashSync) − принято на Edge. Начислено: у завершённой — итог,
    /// у идущей с лимитом — до планового окончания (предоплата), открытая идущая — ещё не к оплате.
    /// </summary>
    public IReadOnlyList<OfflinePayable> GetOfflinePayable() => database.Read(c => PayableCore(c, null));

    private List<OfflinePayable> PayableCore(SqliteConnection c, SqliteTransaction? tx, string? onlySessionId = null)
    {
        var since = time.GetUtcNow() - OfflinePayableWindow;
        var sessions = c.Query(tx, """
            SELECT * FROM sessions
            WHERE ($only IS NULL OR session_id = $only)
              AND ((state = 'Ended' AND ended_at_utc >= $since) OR (state = 'Active' AND planned_end_at_utc IS NOT NULL))
            ORDER BY started_at_utc DESC
            """, MapSession, ("$since", since), ("$only", onlySessionId));
        var cloudPaid = c.Query(tx, "SELECT session_id, paid_minor_units FROM session_cloud_paid", r => (r.S("session_id"), r.L("paid_minor_units")))
            .ToDictionary(x => x.Item1, x => x.Item2);
        var offlinePaid = c.Query(tx, "SELECT session_id, SUM(amount_minor_units) AS paid FROM offline_payments GROUP BY session_id",
            r => (r.S("session_id"), r.L("paid"))).ToDictionary(x => x.Item1, x => x.Item2);
        var names = c.Query(tx, "SELECT device_id, display_name FROM devices", r => (r.S("device_id"), r.S("display_name")))
            .ToDictionary(x => x.Item1, x => x.Item2);

        var result = new List<OfflinePayable>();
        foreach (var s in sessions)
        {
            var charge = s.State == SessionState.Ended
                ? s.TotalMinorUnits ?? 0
                : BillingCalculator.CalculateMinorUnits(s.PriceSnapshot, s.StartedAtUtc, s.PlannedEndAtUtc!.Value - s.StartedAtUtc);
            var cloud = cloudPaid.GetValueOrDefault(s.SessionId);
            var offline = offlinePaid.GetValueOrDefault(s.SessionId);
            result.Add(new OfflinePayable(s.SessionId, s.DeviceId, names.GetValueOrDefault(s.DeviceId, s.DeviceId), s.State, s.StartedAtUtc,
                s.EndedAtUtc, s.PlannedEndAtUtc, s.PriceSnapshot.Currency, charge, cloud, offline, charge - cloud - offline));
        }

        return result;
    }

    /// <summary>
    /// Принять оплату в кассе Edge. Не больше известного долга; повтор с тем же ключом — та же оплата.
    /// Событие OfflinePaymentRecorded уходит в Cloud через outbox, когда появится связь.
    /// </summary>
    public async Task<(OfflinePaymentOutcome Outcome, OfflinePayment? Payment, long Due)> RecordOfflinePaymentAsync(string sessionId,
        long amount, string method, OfflineStaffMember cashier, string? idempotencyKey, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var result = await database.WriteAsync((c, tx) =>
        {
            if (idempotencyKey is not null &&
                c.Query(tx, "SELECT payment_id FROM offline_payments WHERE idempotency_key = $k", r => r.S("payment_id"),
                    ("$k", idempotencyKey)).FirstOrDefault() is { } existing)
            {
                return (OfflinePaymentOutcome.Replayed, (string?)existing, 0L);
            }

            if (c.Scalar(tx, "SELECT 1 FROM sessions WHERE session_id = $s", ("$s", sessionId)) is null)
            {
                return (OfflinePaymentOutcome.UnknownSession, null, 0L);
            }

            var row = PayableCore(c, tx, sessionId).FirstOrDefault();
            if (row is null || row.DueMinorUnits <= 0)
            {
                return (OfflinePaymentOutcome.NotPayable, null, row?.DueMinorUnits ?? 0);
            }

            if (amount > row.DueMinorUnits)
            {
                return (OfflinePaymentOutcome.ExceedsDue, null, row.DueMinorUnits);
            }

            var paymentId = $"opay_{Guid.CreateVersion7():N}";
            c.Exec(tx, """
                INSERT INTO offline_payments (payment_id, session_id, device_id, amount_minor_units, method, user_id, user_name,
                    recorded_at_utc, idempotency_key)
                VALUES ($id, $s, $d, $a, $m, $u, $n, $now, $k)
                """, ("$id", paymentId), ("$s", sessionId), ("$d", row.DeviceId), ("$a", amount), ("$m", method),
                ("$u", cashier.UserId), ("$n", cashier.DisplayName), ("$now", now), ("$k", idempotencyKey));
            AppendEvent(c, tx, EventTypes.OfflinePaymentRecorded, sessionId, new OfflinePaymentRecordedPayload
            {
                PaymentId = paymentId,
                SessionId = sessionId,
                DeviceId = row.DeviceId,
                AmountMinorUnits = amount,
                Method = method,
                UserId = cashier.UserId,
                RecordedAtUtc = now
            }, correlationId: null);
            return (OfflinePaymentOutcome.Recorded, paymentId, row.DueMinorUnits - amount);
        }, ct);

        if (result.Item1 == OfflinePaymentOutcome.Recorded)
        {
            signals.NotifyOutbox();
        }

        var payment = result.Item2 is { } id ? ListOfflinePayments(500).FirstOrDefault(p => p.PaymentId == id) : null;
        return (result.Item1, payment, result.Item3);
    }

    /// <summary>Последние оплаты кассы Edge; Sent — событие уже принято Cloud.</summary>
    public IReadOnlyList<OfflinePayment> ListOfflinePayments(int limit) => database.Read(c => c.Query(null, """
        SELECT p.*, COALESCE(d.display_name, p.device_id) AS device_name,
               (SELECT o.sent_at_utc IS NOT NULL FROM outbox o
                WHERE o.event_type = 'OfflinePaymentRecorded' AND o.payload_json LIKE '%' || p.payment_id || '%' LIMIT 1) AS sent
        FROM offline_payments p LEFT JOIN devices d ON d.device_id = p.device_id
        ORDER BY p.recorded_at_utc DESC LIMIT $n
        """, r => new OfflinePayment(r.S("payment_id"), r.S("session_id"), r.S("device_id"), r.S("device_name"),
        r.L("amount_minor_units"), r.S("method"), r.S("user_name"), r.T("recorded_at_utc"), r.LN("sent") == 1), ("$n", limit)));
}
