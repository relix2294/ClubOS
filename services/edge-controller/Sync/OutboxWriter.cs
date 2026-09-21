using System.Text.Json;
using ClubOS.EdgeController.Data;

namespace ClubOS.EdgeController.Sync;

/// <summary>
/// Постановка события домена в durable outbox (ТЗ §25.2.4). Вызывающий сохраняет изменения
/// (SaveChanges) — событие пишется в ту же транзакцию, что и доменное изменение.
/// Порядковый номер (Sequence) назначается при отправке из автоинкремента строки (см. CloudSyncService).
/// </summary>
public static class OutboxWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static OutboxEvent Enqueue(
        EdgeDbContext db,
        string eventType,
        string aggregateId,
        object payload,
        DateTimeOffset occurredAtUtc,
        string? correlationId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var evt = new OutboxEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = eventType,
            AggregateId = aggregateId,
            OccurredAtUtc = occurredAtUtc,
            RecordedAtUtc = now,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            CorrelationId = correlationId,
            Status = OutboxStatus.Pending,
            Attempts = 0,
            NextAttemptUtc = now,
        };

        db.Outbox.Add(evt);
        return evt;
    }
}
