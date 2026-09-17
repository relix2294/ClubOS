namespace ClubOS.Contracts;

/// <summary>
/// Денежная сумма в минимальных единицах валюты целым числом (ТЗ §12.2, §23.2).
/// Для TJS minor unit = дирам (1 TJS = 100 дирам). НИКОГДА не хранить деньги в double.
/// </summary>
/// <param name="MinorUnits">Сумма в минимальных единицах (например, 200 = 2,00 TJS).</param>
/// <param name="Currency">ISO 4217 код валюты, напр. "TJS".</param>
public readonly record struct Money(long MinorUnits, string Currency)
{
    public static Money Tjs(long minorUnits) => new(minorUnits, "TJS");

    /// <summary>Человекочитаемое представление, напр. "2,00 TJS". Только для отображения/логов.</summary>
    public string ToDisplayString()
    {
        var whole = MinorUnits / 100;
        var frac = Math.Abs(MinorUnits % 100);
        return $"{whole},{frac:D2} {Currency}";
    }
}
