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
