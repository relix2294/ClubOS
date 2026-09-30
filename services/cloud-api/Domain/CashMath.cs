using ClubOS.CloudApi.Api;
using ClubOS.Contracts;

namespace ClubOS.CloudApi.Domain;

/// <summary>Расчёты кассы без БД: начисление по сессии, остаток к оплате, итоги смены.</summary>
public static class CashMath
{
    /// <summary>Максимальная сумма одной операции и остатка в кассе (1 000 000,00 в валюте локации).</summary>
    public const long MaxAmountMinorUnits = 100_000_000;

    /// <summary>
    /// Сколько клиент должен за сессию. Завершённая — итог Edge. Идущая с лимитом — стоимость до планового
    /// окончания (предоплата; продление увеличивает начисление, ранний конец уменьшает). Идущая без лимита
    /// и ещё не начатая — null: сумма неизвестна, принять оплату нельзя. Прочие состояния — 0.
    /// </summary>
    public static long? Charge(Session s) => s.State switch
    {
        SessionState.Ended => s.TotalMinorUnits ?? 0,
        SessionState.Active when s is { StartedAtUtc: { } started, PlannedEndAtUtc: { } planned } && planned >= started =>
            BillingCalculator.CalculateMinorUnits(s.Snapshot(), planned - started),
        SessionState.Created or SessionState.Active => null,
        _ => 0
    };

    /// <summary>Оплачено по сессии: оплаты минус возвраты (сумма операций со знаком).</summary>
    public static long Paid(IEnumerable<CashOperation> operations) =>
        operations.Where(o => o.Kind is CashOperationKinds.SessionPayment or CashOperationKinds.Refund)
            .Sum(o => o.AmountMinorUnits);

    public static ShiftTotals Totals(long openingCash, IReadOnlyCollection<CashOperation> operations)
    {
        long Sum(string kind, string method) =>
            operations.Where(o => o.Kind == kind && o.Method == method).Sum(o => o.AmountMinorUnits);

        var cashPayments = Sum(CashOperationKinds.SessionPayment, PaymentMethods.Cash);
        var cardPayments = Sum(CashOperationKinds.SessionPayment, PaymentMethods.Card);
        var cashRefunds = -Sum(CashOperationKinds.Refund, PaymentMethods.Cash);
        var cardRefunds = -Sum(CashOperationKinds.Refund, PaymentMethods.Card);
        var cashIn = Sum(CashOperationKinds.CashIn, PaymentMethods.Cash);
        var cashOut = -Sum(CashOperationKinds.CashOut, PaymentMethods.Cash);
        return new ShiftTotals(
            cashPayments, cardPayments, cashRefunds, cardRefunds, cashIn, cashOut,
            ExpectedCashMinorUnits: openingCash + operations.Where(o => o.Method == PaymentMethods.Cash).Sum(o => o.AmountMinorUnits),
            RevenueMinorUnits: cashPayments + cardPayments - cashRefunds - cardRefunds,
            PaymentCount: operations.Count(o => o.Kind == CashOperationKinds.SessionPayment));
    }
}
