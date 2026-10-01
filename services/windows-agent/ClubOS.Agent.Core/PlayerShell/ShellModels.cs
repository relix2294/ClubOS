using System.Text.Json.Serialization;
using ClubOS.Contracts;

namespace ClubOS.Agent.Core.PlayerShell;

/// <summary>Режим Player Shell на ПК (ТЗ §26, DEVIATIONS D-003/D-016).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShellMode
{
    /// <summary>Поведение M0: без экрана клуба, только ShowMessage и LockTestMode.</summary>
    Off,

    /// <summary>Только индикатор сессии (время и стоимость); свободный ПК не блокируется. Для пилота.</summary>
    Hud,

    /// <summary>Свободный ПК закрыт экраном клуба; рабочий стол доступен только во время сессии.</summary>
    Enforced
}

public sealed class ShellOptions
{
    public ShellMode Mode { get; set; } = ShellMode.Off;

    /// <summary>Хэш PIN техника (<see cref="TechnicianPin"/>); без него режим обслуживания недоступен.</summary>
    public string? TechnicianPinHash { get; set; }

    /// <summary>Сколько длится режим обслуживания после ввода PIN.</summary>
    public int MaintenanceMinutes { get; set; } = 15;

    /// <summary>Сколько секунд без ответа Edge считать «нет связи» (для подписи на экране).</summary>
    public int EdgeOfflineAfterSeconds { get; set; } = 30;
}

/// <summary>Что показывает Player Shell.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShellView
{
    /// <summary>Ничего (режим Off или Hud без сессии).</summary>
    Hidden,

    /// <summary>Экран клуба: ПК свободен, обратитесь к администратору.</summary>
    Free,

    /// <summary>Идёт сессия: рабочий стол доступен, индикатор времени и стоимости.</summary>
    Session,

    /// <summary>Сессия завершена: итог; в режиме Enforced ПК закрыт.</summary>
    Ended,

    /// <summary>Техник ввёл PIN: экран клуба снят на ограниченное время.</summary>
    Maintenance
}

/// <summary>
/// Снимок для отрисовки в AgentSessionHost. Содержит только «медленные» данные — таймер и стоимость
/// SessionHost считает сам каждую секунду через <see cref="ShellClock"/>, поэтому снимок не меняется
/// каждую секунду и передаётся по pipe только при реальных изменениях.
/// </summary>
public sealed record ShellState
{
    public required ShellMode Mode { get; init; }
    public required ShellView View { get; init; }
    public required string DeviceName { get; init; }
    public string? ClubName { get; init; }

    /// <summary>Поправка к часам ПК: время Edge = локальное UTC + offset.</summary>
    public long ClockOffsetMs { get; init; }

    public bool EdgeOnline { get; init; } = true;

    public ShellSession? Session { get; init; }
    public ShellEnded? Ended { get; init; }
    public DateTimeOffset? MaintenanceUntilUtc { get; init; }

    /// <summary>Служебная подсказка на экране клуба (например, «ПК ожидает подтверждения, MAC …»).</summary>
    public string? Notice { get; init; }
}

public sealed record ShellSession
{
    public required string SessionId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? PlannedEndAtUtc { get; init; }
    public required PriceSnapshot PriceSnapshot { get; init; }
}

public sealed record ShellEnded
{
    public required string SessionId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset EndedAtUtc { get; init; }
    public required long TotalMinorUnits { get; init; }
    public required string Currency { get; init; }
    public string? Reason { get; init; }

    /// <summary>true — итог посчитан агентом локально (Edge недоступен), окончательный придёт от Edge.</summary>
    public bool Estimated { get; init; }
}

/// <summary>Вход от пользователя/техника из SessionHost в службу.</summary>
public interface IShellInput
{
    Task<PresentResult> RequestMaintenanceAsync(string pin, CancellationToken ct);

    void EndMaintenance();
}
