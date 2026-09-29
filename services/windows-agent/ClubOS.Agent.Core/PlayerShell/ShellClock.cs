using System.Globalization;
using ClubOS.Contracts;

namespace ClubOS.Agent.Core.PlayerShell;

public enum ShellWarning
{
    None,

    /// <summary>До конца лимита ≤ 5 минут.</summary>
    Soon,

    /// <summary>До конца лимита ≤ 1 минуты.</summary>
    Critical
}

/// <summary>
/// Расчёты для индикатора сессии. Время — по часам Edge (локальные часы ПК + поправка), стоимость —
/// тем же <see cref="BillingCalculator"/>, что на Edge и в Cloud, поэтому цифра на экране клиента
/// совпадает с итогом.
/// </summary>
public static class ShellClock
{
    public static readonly TimeSpan SoonThreshold = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CriticalThreshold = TimeSpan.FromMinutes(1);

    public static DateTimeOffset EdgeNow(ShellState state, DateTimeOffset localUtcNow) =>
        localUtcNow.AddMilliseconds(state.ClockOffsetMs);

    public static TimeSpan Elapsed(ShellSession session, DateTimeOffset edgeNow)
    {
        var end = session.PlannedEndAtUtc is { } planned && planned < edgeNow ? planned : edgeNow;
        var elapsed = end - session.StartedAtUtc;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    /// <summary>Остаток лимита; null — открытая сессия.</summary>
    public static TimeSpan? Remaining(ShellSession session, DateTimeOffset edgeNow)
    {
        if (session.PlannedEndAtUtc is not { } planned)
        {
            return null;
        }

        var remaining = planned - edgeNow;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    public static long CurrentCost(ShellSession session, DateTimeOffset edgeNow) =>
        BillingCalculator.CalculateMinorUnits(session.PriceSnapshot, Elapsed(session, edgeNow));

    public static ShellWarning Warning(ShellSession session, DateTimeOffset edgeNow) =>
        Remaining(session, edgeNow) switch
        {
            null => ShellWarning.None,
            { } r when r <= CriticalThreshold => ShellWarning.Critical,
            { } r when r <= SoonThreshold => ShellWarning.Soon,
            _ => ShellWarning.None
        };

    /// <summary>"1:05:09" или "05:09" для часа и меньше.</summary>
    public static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var hours = (long)value.TotalHours;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{value.Minutes:D2}:{value.Seconds:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{value.Minutes:D2}:{value.Seconds:D2}");
    }

    public static string FormatMoney(long minorUnits, string currency) => new Money(minorUnits, currency).ToDisplayString();
}
