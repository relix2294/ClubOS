using System.Text.Json;
using ClubOS.Contracts;
using ClubOS.EdgeController.Config;
using ClubOS.EdgeController.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClubOS.EdgeController.Sync;

/// <summary>
/// Фоновая отправка outbox в Cloud (ТЗ §25.2.4). Порядок — по возрастанию Id (порядок вставки);
/// Sequence события = Id строки (монотонно в рамках локации). При неудаче — экспоненциальный
/// backoff, событие остаётся в outbox (durable, не теряется при перезапуске).
/// </summary>
public sealed class OutboxSyncService(
    IServiceProvider services,
    IOptions<EdgeOptions> options,
    ILogger<OutboxSyncService> logger) : BackgroundService
{
    private const int MaxBackoffSeconds = 300;
    private readonly EdgeOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.SyncIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PumpAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка цикла outbox-sync");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
        var client = scope.ServiceProvider.GetRequiredService<CloudSyncClient>();

        var now = DateTimeOffset.UtcNow;
        var batch = await db.Outbox
            .Where(o => o.Status == OutboxStatus.Pending && o.NextAttemptUtc <= now)
            .OrderBy(o => o.Id)
            .Take(Math.Max(1, _options.SyncBatchSize))
            .ToListAsync(ct);

        if (batch.Count == 0)
        {
            return;
        }

        var envelopes = batch.Select(ToEnvelope).ToList();
        var delivered = await client.SendAsync(envelopes, ct);
        var sentAt = DateTimeOffset.UtcNow;

        foreach (var row in batch)
        {
            if (delivered)
            {
                row.Status = OutboxStatus.Sent;
                row.SentAtUtc = sentAt;
                row.LastError = null;
            }
            else
            {
                row.Attempts++;
                row.NextAttemptUtc = sentAt.AddSeconds(BackoffSeconds(row.Attempts));
                row.LastError = "Доставка в Cloud не удалась (нет 2xx или сеть недоступна).";
            }
        }

        await db.SaveChangesAsync(ct);

        if (!delivered)
        {
            logger.LogWarning("Outbox: батч из {Count} событий не доставлен, повтор по backoff.", batch.Count);
        }
    }

    private EventEnvelope ToEnvelope(OutboxEvent row)
    {
        using var doc = JsonDocument.Parse(row.PayloadJson);
        return new EventEnvelope
        {
            EventId = row.EventId,
            EventType = row.EventType,
            TenantId = _options.TenantId,
            LocationId = _options.LocationId,
            AggregateId = row.AggregateId,
            Sequence = row.Id,
            OccurredAtUtc = row.OccurredAtUtc,
            RecordedAtUtc = row.RecordedAtUtc,
            CorrelationId = row.CorrelationId,
            Payload = doc.RootElement.Clone(),
        };
    }

    private static int BackoffSeconds(int attempts)
    {
        // 2,4,8,... с потолком MaxBackoffSeconds.
        var capped = Math.Min(attempts, 30);
        var seconds = 1L << capped;
        return (int)Math.Min(seconds, MaxBackoffSeconds);
    }
}
