using ClubOS.Contracts;

namespace ClubOS.EdgeController.Data;

/// <summary>Статус доставки события в Cloud (durable outbox — ТЗ §25.2.4).</summary>
public enum OutboxStatus
{
    Pending,
    Sent,
    Failed,
}

/// <summary>
/// Событие домена, ожидающее отправки в Cloud. Долговечный outbox с retry/backoff:
/// событие не теряется при перезапуске и не отправляется дважды (EventId идемпотентен в Cloud).
/// </summary>
public sealed class OutboxEvent
{
    public long Id { get; set; } // локальный автоинкремент, порядок вставки
    public required string EventId { get; set; } // ключ идемпотентности (UNIQUE)
    public required string EventType { get; set; }
    public required string AggregateId { get; set; }
    public long Sequence { get; set; } // монотонно в рамках локации (ТЗ §4)
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public required string PayloadJson { get; set; }
    public string? CorrelationId { get; set; }

    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptUtc { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Идемпотентный inbox для команд/сообщений, полученных от Cloud (ТЗ §4).
/// Наличие Id означает «уже обработано» — повторная доставка игнорируется.
/// </summary>
public sealed class InboxReceipt
{
    public required string Id { get; set; } // = commandId / messageId
    public required string Kind { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
}

/// <summary>Локальная сессия (ТЗ §12). Переживает перезапуск — активная сессия не теряется.</summary>
public sealed class EdgeSession
{
    public required string Id { get; set; }
    public required string DeviceId { get; set; }
    public SessionState State { get; set; } = SessionState.Created;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }

    // Снимок тарифа на старте (ТЗ §12.2).
    public long PricePerHourMinorUnits { get; set; }
    public required string Currency { get; set; }
    public RoundingRule Rounding { get; set; }
    public int RuleVersion { get; set; }

    public long? TotalMinorUnits { get; set; }

    public required string Actor { get; set; }
    public required string CorrelationId { get; set; }
}

/// <summary>Команда для ретрансляции агенту (ТЗ §10.2). Жизненный цикл — вперёд.</summary>
public sealed class EdgeCommand
{
    public required string Id { get; set; } // = commandId
    public required string DeviceId { get; set; }
    public CommandType CommandType { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public CommandState State { get; set; } = CommandState.Queued;
    public string? Error { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Последнее известное состояние устройства по heartbeat (ТЗ §10.1).</summary>
public sealed class EdgeDeviceState
{
    public required string DeviceId { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.Offline;
    public DateTimeOffset? LastHeartbeatUtc { get; set; }
    public string? Hostname { get; set; }
    public string? WindowsVersion { get; set; }
    public string? Cpu { get; set; }
    public int? RamMegabytes { get; set; }
    public string? Ipv4 { get; set; }
    public string? AgentVersion { get; set; }
}

/// <summary>Кэш конфигурации, полученной от Cloud (ТЗ §25.2.4): работа при недоступном Cloud.</summary>
public sealed class CachedConfig
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
