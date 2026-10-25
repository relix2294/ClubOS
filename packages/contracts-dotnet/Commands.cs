using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClubOS.Contracts;

/// <summary>Типы команд, поддерживаемые в M0 (ТЗ §25.2.5). Только allow-list (§10.2 CMD-004).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommandType
{
    ShowMessage,
    LockTestMode,

    /// <summary>Снимок экрана ПК (удалённый доступ, D-022). Результат — <see cref="ScreenshotOutput"/>.</summary>
    Screenshot,

    /// <summary>Процессы пользователя (не системные). Результат — <see cref="ProcessListOutput"/>.</summary>
    ListProcesses,

    /// <summary>Завершить процесс пользователя (<see cref="KillProcessPayload"/>). Системные и ClubOS — отказ.</summary>
    KillProcess,

    /// <summary>Перезагрузка ПК (<see cref="PowerPayload"/>).</summary>
    Reboot,

    /// <summary>Выключение ПК (<see cref="PowerPayload"/>).</summary>
    Shutdown
}

/// <summary>Ограничения удалённого доступа (D-022).</summary>
public static class RemoteLimits
{
    /// <summary>Максимальный размер результата команды (JSON) — снимок экрана JPEG в base64.</summary>
    public const int MaxOutputChars = 768 * 1024;

    /// <summary>Снимок уменьшается до этой ширины (пропорционально).</summary>
    public const int ScreenshotMaxWidth = 1280;

    public const int MaxPowerDelaySeconds = 600;

    public const int MaxProcesses = 200;
}

/// <summary>
/// Жизненный цикл команды (ТЗ §10.2 CMD-003). Переходы строго вперёд;
/// повторная доставка одной команды не выполняет действие дважды (CMD-002).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommandState
{
    Queued,
    Delivered,
    Acknowledged,
    Succeeded,
    Failed,
    Expired,
    Cancelled
}

/// <summary>
/// Конверт команды (ТЗ §24.3). Cloud → Edge → Agent.
/// Обязательные поля §4: commandId, commandType, issuedAtUtc, expiresAtUtc, actor, correlationId.
/// </summary>
public sealed record CommandEnvelope
{
    /// <summary>Уникальный ID команды (ключ идемпотентности исполнения).</summary>
    public required string CommandId { get; init; }

    public required CommandType CommandType { get; init; }

    public int SchemaVersion { get; init; } = SchemaVersions.Command;

    public required IReadOnlyList<string> TargetDeviceIds { get; init; }

    /// <summary>Actor, инициировавший команду (user-id). ТЗ §4.</summary>
    public required string IssuedBy { get; init; }

    public required DateTimeOffset IssuedAtUtc { get; init; }

    /// <summary>После этого момента команда не исполняется (ТЗ §10.2 CMD-006).</summary>
    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public required string CorrelationId { get; init; }

    /// <summary>Полезная нагрузка; форма зависит от <see cref="CommandType"/>.</summary>
    public JsonElement Payload { get; init; }
}

/// <summary>Payload для <see cref="CommandType.ShowMessage"/> (ТЗ §24.3 пример).</summary>
public sealed record ShowMessagePayload
{
    public required string Title { get; init; }
    public required string Message { get; init; }
}

/// <summary>Payload для <see cref="CommandType.LockTestMode"/> — управляемый overlay (ТЗ §25.2.5).</summary>
public sealed record LockTestModePayload
{
    /// <summary>true — включить overlay, false — снять.</summary>
    public required bool Lock { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Защита от завершения системных процессов и самого ClubOS.</summary>
public static class ProtectedProcesses
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost", "fontdrvhost",
        "dwm", "explorer", "sihost", "ctfmon", "conhost", "registry", "memory compression", "spoolsv", "taskhostw",
        "runtimebroker", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "textinputhost", "securityhealthsystray",
        "msmpeng", "nissrv", "audiodg", "dllhost", "userinit", "logonui", "lockapp"
    };

    public static bool IsProtected(string name) =>
        Names.Contains(name) || name.StartsWith("ClubOS", StringComparison.OrdinalIgnoreCase);
}

public sealed record KillProcessPayload
{
    public required int ProcessId { get; init; }

    /// <summary>Имя процесса из списка: защита от повторного использования PID другим процессом.</summary>
    public required string Name { get; init; }
}

public sealed record PowerPayload
{
    /// <summary>Задержка перед перезагрузкой/выключением (0–600 с), пользователь видит предупреждение Windows.</summary>
    public int DelaySeconds { get; init; }
    public string? Message { get; init; }
}

public sealed record ScreenshotOutput
{
    /// <summary>image/jpeg (Windows) или image/svg+xml (симулятор).</summary>
    public required string Mime { get; init; }
    public required string DataBase64 { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
}

public sealed record ProcessInfo
{
    public required int ProcessId { get; init; }
    public required string Name { get; init; }
    public long MemoryMb { get; init; }
    public string? WindowTitle { get; init; }
}

public sealed record ProcessListOutput
{
    public required IReadOnlyList<ProcessInfo> Processes { get; init; }
}

/// <summary>Результат исполнения команды агентом (ТЗ §10.2 CMD-007: попадает в audit).</summary>
public sealed record CommandResult
{
    public required string CommandId { get; init; }
    public required string DeviceId { get; init; }

    /// <summary>Одно из терминальных/промежуточных состояний, сообщаемых агентом.</summary>
    public required CommandState State { get; init; }

    public required DateTimeOffset ReportedAtUtc { get; init; }

    /// <summary>Текст ошибки при State=Failed; иначе null. Без секретов (ТЗ §27.3).</summary>
    public string? Error { get; init; }
}
