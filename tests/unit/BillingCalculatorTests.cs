using ClubOS.Contracts;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>
/// Тесты эталона тарификации (ТЗ §5.1 п.1, §12.4).
/// Тариф 120 TJS/час = 12000 minor/час, правило CeilingPerMinute.
/// </summary>
public class BillingCalculatorTests
{
    private static PriceSnapshot Snapshot() => new()
    {
        PricePerHourMinorUnits = 12_000, // 120,00 TJS/час
        Currency = "TJS",
        Rounding = RoundingRule.CeilingPerMinute,
        RuleVersion = 1
    };

    [Theory]
    [InlineData(60, 200)]     // 60 сек  = 2,00 TJS  (эталон ТЗ §12.4)
    [InlineData(61, 400)]     // 61 сек  = 4,00 TJS  (эталон ТЗ §12.4)
    [InlineData(1800, 6000)]  // 30 мин  = 60,00 TJS (эталон ТЗ §12.4)
    [InlineData(1, 200)]      // 1 сек   -> округление вверх до минуты = 2,00 TJS
    [InlineData(120, 400)]    // 2 мин   = 4,00 TJS
    [InlineData(0, 0)]        // 0 сек   = 0
    public void CalculateMinorUnits_MatchesReferenceTable(int seconds, long expectedMinor)
    {
        var result = BillingCalculator.CalculateMinorUnits(Snapshot(), TimeSpan.FromSeconds(seconds));
        Assert.Equal(expectedMinor, result);
    }

    [Fact]
    public void CalculateMinorUnits_IsDeterministic_ForSameInput()
    {
        var snapshot = Snapshot();
        var elapsed = TimeSpan.FromSeconds(137);

        var first = BillingCalculator.CalculateMinorUnits(snapshot, elapsed);
        var second = BillingCalculator.CalculateMinorUnits(snapshot, elapsed);

        Assert.Equal(first, second);
    }

    [Fact]
    public void CalculateMinorUnits_NegativeDuration_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BillingCalculator.CalculateMinorUnits(Snapshot(), TimeSpan.FromSeconds(-1)));
    }

    // ---- Периоды и пакеты ----

    /// <summary>Душанбе UTC+5: 120/час днём, 60/час ночью (22:00–08:00 каждый день).</summary>
    private static PriceSnapshot Night(int days = PricePeriod.AllDays) => Snapshot() with
    {
        UtcOffsetMinutes = 300,
        Periods = [new PricePeriod { Days = days, StartMinute = 22 * 60, EndMinute = 8 * 60, PricePerHourMinorUnits = 6_000 }]
    };

    /// <summary>Местное время Душанбе → UTC.</summary>
    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute, int second = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.FromHours(5)).ToUniversalTime();

    [Theory]
    [InlineData(60, 200)]
    [InlineData(61, 400)]
    [InlineData(1800, 6000)]
    public void Flat_tariff_with_start_time_matches_reference(int seconds, long expected) =>
        Assert.Equal(expected, BillingCalculator.CalculateMinorUnits(Snapshot(), Local(2026, 10, 1, 3, 0), TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Session_across_night_boundary_is_split_by_minutes()
    {
        // 21:50 → 22:10: 10 мин × 120/час + 10 мин × 60/час = 20,00 + 10,00.
        var total = BillingCalculator.CalculateMinorUnits(Night(), Local(2026, 10, 1, 21, 50), TimeSpan.FromMinutes(20));
        Assert.Equal(3_000, total);

        // 07:30 → 08:30: 30 мин ночью (30,00) + 30 мин днём (60,00).
        Assert.Equal(9_000, BillingCalculator.CalculateMinorUnits(Night(), Local(2026, 10, 2, 7, 30), TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Minute_is_priced_by_its_start_even_with_seconds_offset()
    {
        // Старт 21:59:30: первая минута начинается до 22:00 — днём; вторая (22:00:30) — ночью.
        var total = BillingCalculator.CalculateMinorUnits(Night(), Local(2026, 10, 1, 21, 59, 30), TimeSpan.FromSeconds(61));
        Assert.Equal((12_000 + 6_000) / 60, total);
    }

    [Fact]
    public void Overnight_period_morning_belongs_to_the_day_it_started()
    {
        var fridayNight = Night(PricePeriod.DayBit(DayOfWeek.Friday));
        // 2026-10-03 — суббота: 03:00 — продолжение ночи пятницы.
        Assert.Equal(6_000, BillingCalculator.PricePerHourAt(fridayNight, Local(2026, 10, 3, 3, 0)));
        // Пятница 03:00 — продолжение ночи четверга, не входит.
        Assert.Equal(12_000, BillingCalculator.PricePerHourAt(fridayNight, Local(2026, 10, 2, 3, 0)));
        // Пятница 23:00 — входит; суббота 23:00 — нет.
        Assert.Equal(6_000, BillingCalculator.PricePerHourAt(fridayNight, Local(2026, 10, 2, 23, 0)));
        Assert.Equal(12_000, BillingCalculator.PricePerHourAt(fridayNight, Local(2026, 10, 3, 23, 0)));
    }

    [Fact]
    public void First_matching_period_wins()
    {
        var snapshot = Night() with
        {
            Periods =
            [
                new PricePeriod { Days = PricePeriod.DayBit(DayOfWeek.Saturday) | PricePeriod.DayBit(DayOfWeek.Sunday), StartMinute = 0, EndMinute = 24 * 60 - 1, PricePerHourMinorUnits = 15_000 },
                new PricePeriod { Days = PricePeriod.AllDays, StartMinute = 22 * 60, EndMinute = 8 * 60, PricePerHourMinorUnits = 6_000 }
            ]
        };
        Assert.Equal(15_000, BillingCalculator.PricePerHourAt(snapshot, Local(2026, 10, 3, 23, 0))); // суббота
        Assert.Equal(6_000, BillingCalculator.PricePerHourAt(snapshot, Local(2026, 10, 2, 23, 0))); // пятница
        Assert.Equal(12_000, BillingCalculator.PricePerHourAt(snapshot, Local(2026, 10, 2, 12, 0)));
    }

    [Fact]
    public void Package_price_covers_its_minutes_and_extra_time_is_billed_by_tariff()
    {
        var package = Snapshot() with { PackageId = "pkg_1", PackageName = "3 часа", PackageMinutes = 180, PackagePriceMinorUnits = 25_000 };
        Assert.Equal(25_000, BillingCalculator.CalculateMinorUnits(package, TimeSpan.Zero));
        Assert.Equal(25_000, BillingCalculator.CalculateMinorUnits(package, TimeSpan.FromMinutes(60))); // ранний конец — цена пакета
        Assert.Equal(25_000, BillingCalculator.CalculateMinorUnits(package, TimeSpan.FromMinutes(180)));
        // Продление на 20 мин сверх пакета: 20 × 120/час = 40,00.
        Assert.Equal(29_000, BillingCalculator.CalculateMinorUnits(package, TimeSpan.FromMinutes(200)));
        Assert.Equal(25_000 + 200, BillingCalculator.CalculateMinorUnits(package, TimeSpan.FromSeconds(180 * 60 + 1)));
    }

    [Fact]
    public void Package_extension_uses_the_period_price_of_those_minutes()
    {
        // Пакет 60 мин с 21:00; продление 22:00–22:30 — ночная цена.
        var package = Night() with { PackageMinutes = 60, PackagePriceMinorUnits = 10_000 };
        Assert.Equal(10_000 + 3_000, BillingCalculator.CalculateMinorUnits(package, Local(2026, 10, 1, 21, 0), TimeSpan.FromMinutes(90)));
    }

    [Fact]
    public void Time_dependent_tariff_requires_start_time() =>
        Assert.Throws<InvalidOperationException>(() => BillingCalculator.CalculateMinorUnits(Night(), TimeSpan.FromMinutes(1)));

    [Fact]
    public void Day_long_session_sums_every_minute()
    {
        // 24 часа: 14 ч днём × 120 + 10 ч ночью × 60 = 1680 + 600.
        Assert.Equal(228_000, BillingCalculator.CalculateMinorUnits(Night(), Local(2026, 10, 1, 8, 0), TimeSpan.FromHours(24)));
    }

    [Theory]
    [InlineData(0, 0, 60, "день")]
    [InlineData(128, 0, 60, "день")]
    [InlineData(127, 600, 600, "совпадают")]
    [InlineData(127, -1, 60, "00:00")]
    [InlineData(127, 0, 1441, "00:00")]
    [InlineData(127, 0, 1440, "весь день")]
    public void Invalid_periods_are_rejected(int days, int start, int end, string message)
    {
        var error = new PricePeriod { Days = days, StartMinute = start, EndMinute = end, PricePerHourMinorUnits = 1 }.Validate();
        Assert.NotNull(error);
        Assert.Contains(message, error);
    }

    [Fact]
    public void Snapshot_with_periods_round_trips_through_json()
    {
        var snapshot = Night() with { PackageId = "pkg", PackageName = "Ночь", PackageMinutes = 600, PackagePriceMinorUnits = 30_000 };
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot, ContractJson.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<PriceSnapshot>(json, ContractJson.Options)!;
        Assert.Equal(snapshot.Periods, back.Periods);
        Assert.Equal(300, back.UtcOffsetMinutes);
        Assert.Equal("Ночь", back.PackageName);
        Assert.DoesNotContain("isTimeDependent", json);

        // Старый снимок без новых полей — одна цена, без пакета.
        var legacy = System.Text.Json.JsonSerializer.Deserialize<PriceSnapshot>(
            """{"pricePerHourMinorUnits":12000,"currency":"TJS","rounding":"CeilingPerMinute","ruleVersion":1}""", ContractJson.Options)!;
        Assert.Empty(legacy.Periods);
        Assert.False(legacy.HasPackage);
    }

    [Fact]
    public void Money_ToDisplayString_FormatsTjs()
    {
        Assert.Equal("2,00 TJS", Money.Tjs(200).ToDisplayString());
        Assert.Equal("60,00 TJS", Money.Tjs(6000).ToDisplayString());
    }
}
