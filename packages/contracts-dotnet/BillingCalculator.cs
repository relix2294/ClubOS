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
    /// Считает стоимость по снимку тарифа и длительности.
    /// </summary>
    /// <param name="snapshot">Снимок тарифа, зафиксированный на старте сессии.</param>
    /// <param name="elapsed">Длительность сессии.</param>
    /// <returns>Стоимость в минимальных единицах валюты (неотрицательная).</returns>
    /// <exception cref="ArgumentOutOfRangeException">Если длительность отрицательна.</exception>
    public static long CalculateMinorUnits(PriceSnapshot snapshot, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), "Длительность сессии не может быть отрицательной.");
        }

        return snapshot.Rounding switch
        {
            RoundingRule.CeilingPerMinute => CeilingPerMinute(snapshot.PricePerHourMinorUnits, elapsed),
            _ => throw new NotSupportedException($"Неизвестное правило округления: {snapshot.Rounding}.")
        };
    }

    private static long CeilingPerMinute(long pricePerHourMinorUnits, TimeSpan elapsed)
    {
        // Округляем время ВВЕРХ до полной минуты.
        var totalSeconds = (long)Math.Ceiling(elapsed.TotalSeconds);
        var minutes = (totalSeconds + SecondsPerMinute - 1) / SecondsPerMinute;

        // Стоимость минуты = цена/час / 60. Для эталонных тарифов (делимых на 60) — точно.
        // (minutes * price) / 60 избегает потери точности при промежуточном делении.
        return checked(minutes * pricePerHourMinorUnits) / MinutesPerHour;
    }
}
