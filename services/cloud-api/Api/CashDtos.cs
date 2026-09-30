using ClubOS.Contracts;

namespace ClubOS.CloudApi.Api;

public sealed record ShiftTotals(
    long CashPaymentsMinorUnits,
    long CardPaymentsMinorUnits,
    long CashRefundsMinorUnits,
    long CardRefundsMinorUnits,
    long CashInMinorUnits,
    long CashOutMinorUnits,
    long ExpectedCashMinorUnits,
    long RevenueMinorUnits,
    int PaymentCount);

public sealed record CashShiftView(
    string ShiftId,
    string LocationId,
    string Currency,
    string OpenedBy,
    string OpenedByName,
    DateTimeOffset OpenedAtUtc,
    long OpeningCashMinorUnits,
    string? ClosedBy,
    string? ClosedByName,
    DateTimeOffset? ClosedAtUtc,
    long? CountedCashMinorUnits,
    long? DiscrepancyMinorUnits,
    string? CloseNote,
    ShiftTotals Totals);

public sealed record CashOperationView(
    string OperationId,
    string ShiftId,
    string Kind,
    string Method,
    long AmountMinorUnits,
    string Currency,
    string? SessionId,
    string? DeviceId,
    string? DeviceName,
    string? Reason,
    string CreatedBy,
    string CreatedByName,
    DateTimeOffset CreatedAtUtc);

/// <summary>Сессия, по которой есть расчёт с клиентом: долг (Due &gt; 0) или переплата (Due &lt; 0).</summary>
public sealed record PayableSessionView(
    string SessionId,
    string DeviceId,
    string DeviceName,
    SessionState State,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    DateTimeOffset? PlannedEndAtUtc,
    string Currency,
    long ChargeMinorUnits,
    long PaidMinorUnits,
    long DueMinorUnits);

public sealed record CashDeskView(
    string LocationId,
    string Currency,
    CashShiftView? Shift,
    IReadOnlyList<PayableSessionView> Payable,
    IReadOnlyList<CashOperationView> Operations);

public sealed record OpenShiftRequest(long OpeningCashMinorUnits);

public sealed record CloseShiftRequest(long CountedCashMinorUnits, string? Note);

public sealed record SessionPaymentRequest(long AmountMinorUnits, string? Method, string? IdempotencyKey);

public sealed record SessionRefundRequest(long AmountMinorUnits, string? Method, string? Reason, string? IdempotencyKey);

public sealed record CashMovementRequest(string? Kind, long AmountMinorUnits, string? Reason, string? IdempotencyKey);

public sealed record RevenueDayView(
    string Date,
    int SessionsEnded,
    long ChargedMinorUnits,
    long CashMinorUnits,
    long CardMinorUnits,
    long RefundsMinorUnits,
    long NetMinorUnits);

public sealed record RevenueReportView(
    string LocationId,
    string Currency,
    string Timezone,
    string From,
    string To,
    IReadOnlyList<RevenueDayView> Days,
    RevenueDayView Totals,
    long UnpaidMinorUnits);
