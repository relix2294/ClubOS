using ClubOS.CloudApi.Api;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Live;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>Касса: начисление по сессии, оплачено, итоги смены, отчёт по дням, границы местных суток.</summary>
public class CashMathTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    private static Session NewSession(SessionState state, long? total = null, int? plannedMinutes = null) => new()
    {
        Id = "ses_1",
        TenantId = "org",
        DeviceId = "dev_1",
        LocationId = "loc_1",
        State = state,
        Origin = "cloud",
        RequestedAtUtc = T0,
        StartedAtUtc = state == SessionState.Created ? null : T0,
        PlannedEndAtUtc = plannedMinutes is { } m ? T0.AddMinutes(m) : null,
        DurationMinutes = plannedMinutes,
        PricePerHourMinorUnits = 12_000,
        Currency = "TJS",
        Rounding = RoundingRule.CeilingPerMinute,
        RuleVersion = 1,
        TotalMinorUnits = total,
        StartedBy = "user:u",
        CorrelationId = "cor"
    };

    private static CashOperation Op(string kind, string method, long amount, string? session = "ses_1") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        TenantId = "org",
        LocationId = "loc_1",
        ShiftId = "shf_1",
        Kind = kind,
        Method = method,
        AmountMinorUnits = amount,
        Currency = "TJS",
        SessionId = session,
        CreatedBy = "user:u",
        CreatedAtUtc = T0
    };

    [Fact]
    public void Ended_session_charges_its_total()
    {
        Assert.Equal(400, CashMath.Charge(NewSession(SessionState.Ended, total: 400)));
        Assert.Equal(0, CashMath.Charge(NewSession(SessionState.Ended, total: null)));
    }

    [Fact]
    public void Running_limited_session_charges_until_planned_end_for_prepayment()
    {
        // 120 TJS/час × 45 мин = 90,00: предоплата покрывает лимит с продлением.
        Assert.Equal(9_000, CashMath.Charge(NewSession(SessionState.Active, plannedMinutes: 45)));
    }

    [Theory]
    [InlineData(SessionState.Created)]
    [InlineData(SessionState.Active)] // без лимита
    public void Unknown_amount_cannot_be_charged_yet(SessionState state) =>
        Assert.Null(CashMath.Charge(NewSession(state)));

    [Theory]
    [InlineData(SessionState.Failed)]
    [InlineData(SessionState.Cancelled)]
    public void Failed_or_cancelled_session_costs_nothing(SessionState state) =>
        Assert.Equal(0, CashMath.Charge(NewSession(state)));

    [Fact]
    public void Paid_is_payments_minus_refunds_and_ignores_cash_movements()
    {
        var ops = new[]
        {
            Op(CashOperationKinds.SessionPayment, PaymentMethods.Cash, 500),
            Op(CashOperationKinds.SessionPayment, PaymentMethods.Card, 300),
            Op(CashOperationKinds.Refund, PaymentMethods.Cash, -200),
            Op(CashOperationKinds.CashIn, PaymentMethods.Cash, 10_000, session: null)
        };
        Assert.Equal(600, CashMath.Paid(ops));
    }

    [Fact]
    public void Shift_totals_expected_cash_counts_only_cash()
    {
        var ops = new List<CashOperation>
        {
            Op(CashOperationKinds.SessionPayment, PaymentMethods.Cash, 1_000),
            Op(CashOperationKinds.SessionPayment, PaymentMethods.Card, 700),
            Op(CashOperationKinds.Refund, PaymentMethods.Cash, -100),
            Op(CashOperationKinds.Refund, PaymentMethods.Card, -50),
            Op(CashOperationKinds.CashIn, PaymentMethods.Cash, 2_000, session: null),
            Op(CashOperationKinds.CashOut, PaymentMethods.Cash, -1_500, session: null)
        };
        var totals = CashMath.Totals(openingCash: 5_000, ops);
        Assert.Equal(1_000, totals.CashPaymentsMinorUnits);
        Assert.Equal(700, totals.CardPaymentsMinorUnits);
        Assert.Equal(100, totals.CashRefundsMinorUnits);
        Assert.Equal(50, totals.CardRefundsMinorUnits);
        Assert.Equal(2_000, totals.CashInMinorUnits);
        Assert.Equal(1_500, totals.CashOutMinorUnits);
        Assert.Equal(5_000 + 1_000 - 100 + 2_000 - 1_500, totals.ExpectedCashMinorUnits);
        Assert.Equal(1_000 + 700 - 100 - 50, totals.RevenueMinorUnits); // внесения и изъятия — не выручка
        Assert.Equal(2, totals.PaymentCount);
    }

    [Fact]
    public void Revenue_day_splits_methods_and_nets_refunds()
    {
        var day = CashEndpoints.Day("2026-09-30", 3, 1_200, new[]
        {
            Op(CashOperationKinds.SessionPayment, PaymentMethods.Cash, 800),
            Op(CashOperationKinds.SessionPayment, PaymentMethods.Card, 400),
            Op(CashOperationKinds.Refund, PaymentMethods.Card, -150)
        });
        Assert.Equal(800, day.CashMinorUnits);
        Assert.Equal(400, day.CardMinorUnits);
        Assert.Equal(150, day.RefundsMinorUnits);
        Assert.Equal(1_050, day.NetMinorUnits);
        Assert.Equal(1_200, day.ChargedMinorUnits);
    }

    [Fact]
    public void Balance_payments_are_revenue_but_not_cash_and_top_ups_are_deposits()
    {
        var ops = new List<CashOperation>
        {
            Op(CashOperationKinds.BalanceTopUp, PaymentMethods.Cash, 2_000, session: null),
            Op(CashOperationKinds.BalanceTopUp, PaymentMethods.Card, 1_000, session: null),
            Op(CashOperationKinds.SessionPayment, PaymentMethods.Balance, 600),
            Op(CashOperationKinds.Refund, PaymentMethods.Balance, -100),
            Op(CashOperationKinds.SessionPayment, PaymentMethods.Cash, 300)
        };
        var totals = CashMath.Totals(openingCash: 1_000, ops);
        Assert.Equal(600, totals.BalancePaymentsMinorUnits);
        Assert.Equal(100, totals.BalanceRefundsMinorUnits);
        Assert.Equal(2_000, totals.TopUpCashMinorUnits);
        Assert.Equal(1_000, totals.TopUpCardMinorUnits);
        // В кассе: начало + пополнение наличными + оплата наличными; списание с баланса кассу не трогает.
        Assert.Equal(1_000 + 2_000 + 300, totals.ExpectedCashMinorUnits);
        // Выручка: оплаты сессий (наличные + баланс) минус возврат на баланс; пополнения — не выручка.
        Assert.Equal(300 + 600 - 100, totals.RevenueMinorUnits);
        Assert.Equal(2, totals.PaymentCount);
        Assert.Equal(800, CashMath.Paid(ops));
    }

    [Fact]
    public void Revenue_day_counts_balance_payments_and_top_ups_separately()
    {
        var day = CashEndpoints.Day("2026-09-30", 1, 500, new[]
        {
            Op(CashOperationKinds.SessionPayment, PaymentMethods.Balance, 500),
            Op(CashOperationKinds.Refund, PaymentMethods.Balance, -100),
            Op(CashOperationKinds.BalanceTopUp, PaymentMethods.Cash, 1_500, session: null)
        });
        Assert.Equal(500, day.BalanceMinorUnits);
        Assert.Equal(100, day.RefundsMinorUnits);
        Assert.Equal(1_500, day.TopUpsMinorUnits);
        Assert.Equal(0, day.CashMinorUnits);
        Assert.Equal(400, day.NetMinorUnits);
    }

    [Fact]
    public void Bar_sales_are_revenue_and_cash_by_method_refunds_net_out()
    {
        var ops = new List<CashOperation>
        {
            Op(CashOperationKinds.ProductSale, PaymentMethods.Cash, 2_500, session: null),
            Op(CashOperationKinds.ProductSale, PaymentMethods.Balance, 1_000, session: null),
            Op(CashOperationKinds.ProductRefund, PaymentMethods.Cash, -2_500, session: null),
            Op(CashOperationKinds.SessionPayment, PaymentMethods.Card, 600)
        };
        var totals = CashMath.Totals(openingCash: 0, ops);
        Assert.Equal(3_500, totals.ProductSalesMinorUnits);
        Assert.Equal(2_500, totals.ProductRefundsMinorUnits);
        Assert.Equal(600 + 3_500 - 2_500, totals.RevenueMinorUnits);
        Assert.Equal(0, totals.ExpectedCashMinorUnits); // наличный чек и его возврат
        Assert.Equal(1, totals.PaymentCount); // считаются оплаты сессий
        Assert.Equal(600, CashMath.Paid(ops)); // чеки бара не оплачивают сессии

        var day = CashEndpoints.Day("2026-09-30", 0, 0, ops);
        Assert.Equal(1_000, day.ProductsMinorUnits);
        Assert.Equal(600 + 1_000, day.NetMinorUnits);
    }

    [Theory]
    [InlineData("+992 90 123 45 67", "992901234567")]
    [InlineData("(992) 90-123-45-67", "992901234567")]
    [InlineData("9012345", "9012345")]
    [InlineData("123456", null)] // короче 7 цифр
    [InlineData("1234567890123456", null)] // длиннее 15
    [InlineData("+992 90 abc", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Phone_is_normalized_to_digits(string? input, string? expected) =>
        Assert.Equal(expected, ClientEndpoints.NormalizePhone(input));

    [Fact]
    public void Phone_is_masked_in_audit_except_last_four_digits()
    {
        Assert.Equal("••••••••4567", ClientEndpoints.MaskPhone("992901234567"));
        Assert.Equal("4567", ClientEndpoints.MaskPhone("4567"));
    }

    /// <summary>
    /// Пояс через тот же поиск, что в Cloud. Windows CI без ICU (InvariantGlobalization) IANA-имён не знает —
    /// там берём Windows-идентификатор того же пояса; Cloud в проде работает в Linux-контейнере.
    /// </summary>
    private static TimeZoneInfo Zone(string iana, string windows) =>
        CashEndpoints.FindZone(iana) ?? TimeZoneInfo.FindSystemTimeZoneById(windows);

    [Fact]
    public void Unknown_zone_is_not_found() => Assert.Null(CashEndpoints.FindZone("Mars/Olympus_Mons"));

    [Fact]
    public void Local_day_starts_at_local_midnight()
    {
        // Душанбе UTC+5 без перехода на летнее время: 30.09 начинается в 19:00 UTC 29.09.
        var dushanbe = Zone("Asia/Dushanbe", "West Asia Standard Time");
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 19, 0, 0, TimeSpan.Zero),
            CashEndpoints.LocalMidnightUtc(new DateOnly(2026, 9, 30), dushanbe));
    }

    [Fact]
    public void Local_midnight_survives_dst_gap()
    {
        // В Сантьяго переход на летнее время происходит в полночь: 00:00 не существует, сутки начинаются в 01:00.
        var santiago = Zone("America/Santiago", "Pacific SA Standard Time");
        var start = CashEndpoints.LocalMidnightUtc(new DateOnly(2026, 9, 6), santiago);
        Assert.False(santiago.IsInvalidTime(TimeZoneInfo.ConvertTime(start, santiago).DateTime));
        Assert.True(start < CashEndpoints.LocalMidnightUtc(new DateOnly(2026, 9, 7), santiago));
    }

    [Fact]
    public void Cash_changes_publish_live_hints()
    {
        var shift = new CashShift
        {
            Id = "shf_1",
            TenantId = "org",
            LocationId = "loc_1",
            Currency = "TJS",
            OpenedBy = "user:u",
            OpenedAtUtc = T0
        };
        var shiftEvent = Assert.Single(LiveChangeInterceptor.EventsFor(shift, EntityState.Added, []));
        Assert.Equal(LiveTopics.Cash, shiftEvent.Topic);
        Assert.Equal("loc_1", shiftEvent.LocationId);

        var payment = Op(CashOperationKinds.SessionPayment, PaymentMethods.Cash, 100);
        payment.DeviceId = "dev_1";
        var opEvent = Assert.Single(LiveChangeInterceptor.EventsFor(payment, EntityState.Added, []));
        Assert.Equal(LiveTopics.Cash, opEvent.Topic);
        Assert.Equal("dev_1", opEvent.DeviceId);
    }
}
