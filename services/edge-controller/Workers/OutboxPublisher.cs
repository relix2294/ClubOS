using System.Text.Json;
using ClubOS.Contracts;
using ClubOS.EdgeController.Cloud;
using ClubOS.EdgeController.Storage;
using Microsoft.Extensions.Options;

namespace ClubOS.EdgeController.Workers;

/// <summary>
/// Durable outbox → Cloud (at-least-once). События отправляются пакетами по порядку sequence;
/// при недоступности Cloud — экспоненциальный backoff. Ничего не теряется: события лежат в SQLite
/// до подтверждения Cloud (accepted или duplicate). После восстановления связи — автосинк.
/// </summary>
public sealed class OutboxPublisher(
    EdgeIdentityStore identity,
    EdgeStore store,
    EdgeSignals signals,
    CloudClient cloud,
    IOptions<EdgeOptions> options,
    TimeProvider time,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    public const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await identity.WhenEnrolled.WaitAsync(stoppingToken);
        var backoff = new Backoff(TimeSpan.FromSeconds(options.Value.MaxBackoffSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            var pending = store.GetPendingEvents(BatchSize);
            if (pending.Count == 0)
            {
                await signals.WaitOutboxAsync(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            try
            {
                var response = await PublishBatchAsync(pending, stoppingToken);
                backoff.Reset();
                if (response.Rejected.Count > 0)
                {
                    logger.LogWarning("Cloud отклонил {Count} событий: {Reasons}", response.Rejected.Count,
                        string.Join("; ", response.Rejected.Select(r => $"{r.EventId}: {r.Reason}")));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var delay = backoff.Next();
                logger.LogWarning("Sync в Cloud не удался ({Pending} событий ждут), повтор через {Delay:F0} с: {Error}",
                    pending.Count, delay.TotalSeconds, ex.Message);
                await store.RecordSendFailureAsync(pending.Select(x => x.EventId), ex.Message, stoppingToken);
                try
                {
                    await Task.Delay(delay, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    internal async Task<SyncBatchResponse> PublishBatchAsync(IReadOnlyList<OutboxEvent> pending, CancellationToken ct)
    {
        var id = identity.Current!;
        var batch = new SyncBatchRequest
        {
            Events = pending.Select(e => new EventEnvelope
            {
                EventId = e.EventId,
                EventType = e.EventType,
                TenantId = id.TenantId,
                LocationId = id.LocationId,
                AggregateId = e.AggregateId,
                Sequence = e.Sequence,
                OccurredAtUtc = e.OccurredAtUtc,
                RecordedAtUtc = e.RecordedAtUtc,
                CorrelationId = e.CorrelationId,
                Payload = JsonDocument.Parse(e.PayloadJson).RootElement.Clone()
            }).ToList()
        };

        var response = await cloud.SyncAsync(batch, ct);
        await store.MarkEventsDeliveredAsync(response.Accepted.Concat(response.Duplicates), response.Rejected, ct);
        return response;
    }
}
