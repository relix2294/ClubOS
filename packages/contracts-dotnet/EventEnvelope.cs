using System.Text.Json;

namespace ClubOS.Contracts;

/// <summary>
/// Конверт события домена (ТЗ §24.2). Передаётся Edge → Cloud в sync-batch.
/// Идемпотентность в Cloud обеспечивается уникальностью <see cref="EventId"/>
/// (UNIQUE-ограничение в БД, не только проверка в памяти — ТЗ §4).
/// </summary>
public sealed record EventEnvelope
{
    /// <summary>Глобально уникальный ID события (ULID/UUID). Ключ идемпотентности.</summary>
    public required string EventId { get; init; }

    /// <summary>Тип события, напр. "SessionStarted", "DeviceHeartbeat" (ТЗ §24.4).</summary>
    public required string EventType { get; init; }

    public int SchemaVersion { get; init; } = SchemaVersions.Event;

    public required string TenantId { get; init; }

    public required string LocationId { get; init; }

    /// <summary>ID агрегата, к которому относится событие (device/session/...).</summary>
    public required string AggregateId { get; init; }

    /// <summary>Монотонная последовательность событий в рамках локации (ТЗ §4, §24.2).</summary>
    public required long Sequence { get; init; }

    /// <summary>Когда событие произошло по часам источника (UTC).</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>Когда событие записано в локальный журнал (UTC).</summary>
    public required DateTimeOffset RecordedAtUtc { get; init; }

    public string? CorrelationId { get; init; }

    public string? CausationId { get; init; }

    /// <summary>Полезная нагрузка события; форма зависит от <see cref="EventType"/>.</summary>
    public JsonElement Payload { get; init; }
}
