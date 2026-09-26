using ClubOS.Contracts;
using ClubOS.EdgeController.Cloud;
using ClubOS.EdgeController.Storage;
using Microsoft.Extensions.Options;

namespace ClubOS.EdgeController.Workers;

/// <summary>
/// Каждые несколько секунд: помечает offline устройства без heartbeat, истекает просроченные команды
/// и отправляет в Cloud снимок статусов (не durable — следующий снимок заменит потерянный).
/// </summary>
public sealed class StatusWorker(
    EdgeIdentityStore identity,
    EdgeStore store,
    CloudClient cloud,
    IOptions<EdgeOptions> options,
    TimeProvider time,
    ILogger<StatusWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await identity.WhenEnrolled.WaitAsync(stoppingToken);
        var o = options.Value;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await store.MarkStaleDevicesOfflineAsync(TimeSpan.FromSeconds(o.HeartbeatTimeoutSeconds), stoppingToken);
                await store.ExpireCommandsAsync(stoppingToken);
                await cloud.ReportStatusAsync(new EdgeStatusReport
                {
                    EdgeClockUtc = time.GetUtcNow(),
                    PendingOutboxEvents = store.CountPendingEvents(),
                    Devices = store.BuildStatusEntries()
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug("Отчёт статуса в Cloud не отправлен: {Error}", ex.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(o.StatusReportSeconds), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
