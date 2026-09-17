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

    [Fact]
    public void Money_ToDisplayString_FormatsTjs()
    {
        Assert.Equal("2,00 TJS", Money.Tjs(200).ToDisplayString());
        Assert.Equal("60,00 TJS", Money.Tjs(6000).ToDisplayString());
    }
}
