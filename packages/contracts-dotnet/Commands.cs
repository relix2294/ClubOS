using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClubOS.Contracts;

/// <summary>Типы команд, поддерживаемые в M0 (ТЗ §25.2.5). Только allow-list (§10.2 CMD-004).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommandType
{
    ShowMessage,
    LockTestMode
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
