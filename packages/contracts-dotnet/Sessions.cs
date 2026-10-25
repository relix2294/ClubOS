using System.Text.Json.Serialization;

namespace ClubOS.Contracts;

/// <summary>
/// Состояния сессии (ТЗ §12.2.1). На M0 используется базовый путь
/// created → active → ending → ended; ended нельзя вернуть в active.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionState
{
    Created,
    Reserved,
    Ready,
    Active,
    Paused,
    Ending,
    Ended,
    Reconciled,
    Cancelled,
    Failed
}

/// <summary>
/// Снимок тарифа на момент старта сессии (ТЗ §12.2: price snapshot).
/// Меняющийся позже тариф не влияет на уже начатую сессию.
/// </summary>
public sealed record PriceSnapshot
{
    /// <summary>Цена за час в минимальных единицах валюты (M0: 12000 = 120,00 TJS/час).</summary>
    public required long PricePerHourMinorUnits { get; init; }
    public required string Currency { get; init; }

    /// <summary>Правило округления расчёта (конфигурируемо и версионируемо, ТЗ §12.4).</summary>
    public required RoundingRule Rounding { get; init; }

    /// <summary>Версия правила тарификации (ТЗ §12.4: правило версионируемо).</summary>
    public required int RuleVersion { get; init; }

    /// <summary>
    /// Цена по времени суток и дням недели: первая подходящая по местному времени начала минуты. Минута вне
    /// всех периодов — по <see cref="PricePerHourMinorUnits"/>. Пусто — одна цена всегда.
    /// </summary>
    public IReadOnlyList<PricePeriod> Periods { get; init; } = [];

    /// <summary>
    /// Смещение местного времени локации от UTC на старте сессии (минуты). Фиксируется в снимке: Edge и агент
    /// считают одинаково без базы часовых поясов. Сессия через переход на летнее время считается по старому смещению.
    /// </summary>
    public int UtcOffsetMinutes { get; init; }

    /// <summary>Пакет: первые <see cref="PackageMinutes"/> минут — за фиксированную цену; сверх пакета — по тарифу.</summary>
    public string? PackageId { get; init; }

    public string? PackageName { get; init; }

    public int? PackageMinutes { get; init; }

    public long? PackagePriceMinorUnits { get; init; }

    /// <summary>Стоимость зависит от времени начала (есть периоды).</summary>
    [JsonIgnore]
    public bool IsTimeDependent => Periods.Count > 0;

    [JsonIgnore]
    public bool HasPackage => PackageMinutes is > 0 && PackagePriceMinorUnits is >= 0;
}

/// <summary>
/// Период тарифа: дни недели (маска: пн = 1, вт = 2, … вс = 64; 127 — все дни) и местное время
/// [<see cref="StartMinute"/>, <see cref="EndMinute"/>) в минутах от полуночи. Если начало позже конца —
/// период через полночь (22:00–08:00): утренняя часть относится к дню, когда период начался.
/// </summary>
public sealed record PricePeriod
{
    public const int AllDays = 127;
    public const int MinutesPerDay = 24 * 60;

    public required int Days { get; init; }
    public required int StartMinute { get; init; }
    public required int EndMinute { get; init; }
    public required long PricePerHourMinorUnits { get; init; }

    public static int DayBit(DayOfWeek day) => 1 << (((int)day + 6) % 7);

    /// <summary>Попадает ли минута местного времени в период.</summary>
    public bool Contains(DayOfWeek day, int minuteOfDay)
    {
        if (StartMinute < EndMinute)
        {
            return (Days & DayBit(day)) != 0 && minuteOfDay >= StartMinute && minuteOfDay < EndMinute;
        }

        var previous = (DayOfWeek)(((int)day + 6) % 7);
        return ((Days & DayBit(day)) != 0 && minuteOfDay >= StartMinute)
            || ((Days & DayBit(previous)) != 0 && minuteOfDay < EndMinute);
    }

    /// <summary>Ошибка в периоде или null.</summary>
    public string? Validate()
    {
        if (Days is < 1 or > AllDays)
        {
            return "Выберите хотя бы один день недели.";
        }

        if (StartMinute is < 0 or >= MinutesPerDay || EndMinute is < 1 or > MinutesPerDay)
        {
            return "Время периода — от 00:00 до 24:00.";
        }

        if (StartMinute == EndMinute || (StartMinute == 0 && EndMinute == MinutesPerDay))
        {
            return StartMinute == EndMinute
                ? "Начало и конец периода совпадают."
                : "Период на весь день — это просто цена зоны.";
        }

        return null;
    }
}

/// <summary>
/// Правило округления времени при поминутной тарификации (ТЗ §12.4).
/// M0-эталон: округление вверх до минуты → 60 сек = 2,00 TJS, 61 сек = 4,00 TJS.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RoundingRule
{
    /// <summary>Округление вверх до полной минуты (M0 эталон).</summary>
    CeilingPerMinute
}

/// <summary>Запрос старта тестовой сессии (Admin Web / edge-cli → Cloud/Edge).</summary>
public sealed record StartSessionRequest
{
    public required string DeviceId { get; init; }
    public required string Actor { get; init; }
    public required string CorrelationId { get; init; }
}

/// <summary>Ограничения лимита времени сессии (минуты).</summary>
public static class SessionLimits
{
    public const int MinDurationMinutes = 1;

    /// <summary>Максимальный лимит одной сессии, включая продления (24 часа).</summary>
    public const int MaxDurationMinutes = 24 * 60;

    public const int MinExtendMinutes = 1;
    public const int MaxExtendMinutes = 12 * 60;
}

/// <summary>Итог завершённой сессии.</summary>
public sealed record SessionSummary
{
    public required string SessionId { get; init; }
    public required string DeviceId { get; init; }
    public required SessionState State { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public required PriceSnapshot PriceSnapshot { get; init; }

    /// <summary>Итоговая стоимость в минимальных единицах (заполнена после завершения).</summary>
    public long? TotalMinorUnits { get; init; }
}
