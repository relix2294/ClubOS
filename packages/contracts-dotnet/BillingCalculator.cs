namespace ClubOS.Contracts;

/// <summary>
/// Детерминированный расчёт стоимости сессии (ТЗ §12.2, §12.4).
/// Общий код для Cloud и Edge — одинаковые входные данные всегда дают одинаковую сумму.
/// Расхождение с эталонными тестами должно быть 0 (ТЗ §3.1 цели пилота).
///
/// Эталон M0 (RoundingRule.CeilingPerMinute, 120 TJS/час = 12000 minor/час):
///   60 сек   = 2,00 TJS  (200 minor)
///   61 сек   = 4,00 TJS  (400 minor)
///   30 минут = 60,00 TJS (6000 minor)
/// </summary>
public static class BillingCalculator
{
    private const int SecondsPerMinute = 60;
    private const int MinutesPerHour = 60;

    /// <summary>
    /// Считает стоимость по снимку тарифа без учёта времени начала. Только для снимков без периодов
    /// (одна цена, пакет); для периодов — перегрузка с временем начала.
    /// </summary>
    /// <param name="snapshot">Снимок тарифа, зафиксированный на старте сессии.</param>
    /// <param name="elapsed">Длительность сессии.</param>
    /// <returns>Стоимость в минимальных единицах валюты (неотрицательная).</returns>
    /// <exception cref="ArgumentOutOfRangeException">Если длительность отрицательна.</exception>
    public static long CalculateMinorUnits(PriceSnapshot snapshot, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.IsTimeDependent)
        {
            throw new InvalidOperationException("Тариф с периодами считается от времени начала сессии.");
        }

        return CalculateMinorUnits(snapshot, DateTimeOffset.UnixEpoch, elapsed);
    }

    /// <summary>
    /// Стоимость сессии, начатой в <paramref name="startedAtUtc"/>. Время округляется вверх до минуты; каждая
    /// минута стоит цену часа периода, в который попадает её начало по местному времени, делённую на 60
    /// (делим один раз в конце — для одной цены совпадает с эталоном M0). Пакет: первые N минут — его цена.
    /// </summary>
    public static long CalculateMinorUnits(PriceSnapshot snapshot, DateTimeOffset startedAtUtc, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), "Длительность сессии не может быть отрицательной.");
        }

        if (snapshot.Rounding != RoundingRule.CeilingPerMinute)
        {
            throw new NotSupportedException($"Неизвестное правило округления: {snapshot.Rounding}.");
        }

        var minutes = BilledMinutes(elapsed);
        long total = 0;
        long from = 0;
        if (snapshot.HasPackage)
        {
            total = snapshot.PackagePriceMinorUnits!.Value;
            from = Math.Min(minutes, snapshot.PackageMinutes!.Value);
        }

        return checked(total + RateSum(snapshot, startedAtUtc, from, minutes) / MinutesPerHour);
    }

    /// <summary>Цена часа, действующая в момент <paramref name="atUtc"/> (для подсказки «сейчас N/час»).</summary>
    public static long PricePerHourAt(PriceSnapshot snapshot, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsTimeDependent)
        {
            return snapshot.PricePerHourMinorUnits;
        }

        var local = atUtc.UtcDateTime.AddMinutes(snapshot.UtcOffsetMinutes);
        return RateAt(snapshot, local.DayOfWeek, (int)local.TimeOfDay.TotalMinutes);
    }

    private static long BilledMinutes(TimeSpan elapsed)
    {
        // Округляем время ВВЕРХ до полной минуты.
        var totalSeconds = (long)Math.Ceiling(elapsed.TotalSeconds);
        return (totalSeconds + SecondsPerMinute - 1) / SecondsPerMinute;
    }

    /// <summary>Сумма цен часа по минутам [from, to). Без периодов — умножение (как эталон M0).</summary>
    private static long RateSum(PriceSnapshot snapshot, DateTimeOffset startedAtUtc, long from, long to)
    {
        if (to <= from)
        {
            return 0;
        }

        if (!snapshot.IsTimeDependent)
        {
            return checked((to - from) * snapshot.PricePerHourMinorUnits);
        }

        // Минуты по местному времени: начало сессии + смещение, сдвиг на from минут. Цикл по минутам прост и
        // детерминирован (сессия до 24 ч — до 1440 шагов); секунды начала не влияют на границы периодов —
        // граница минуты сессии отсчитывается от её старта.
        var local = startedAtUtc.UtcDateTime.AddMinutes(snapshot.UtcOffsetMinutes);
        long sum = 0;
        for (var i = from; i < to; i++)
        {
            var at = local.AddMinutes(i);
            sum = checked(sum + RateAt(snapshot, at.DayOfWeek, (int)at.TimeOfDay.TotalMinutes));
        }

        return sum;
    }

    private static long RateAt(PriceSnapshot snapshot, DayOfWeek day, int minuteOfDay)
    {
        foreach (var period in snapshot.Periods)
        {
            if (period.Contains(day, minuteOfDay))
            {
                return period.PricePerHourMinorUnits;
            }
        }

        return snapshot.PricePerHourMinorUnits;
    }
}
