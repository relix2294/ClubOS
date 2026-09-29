using ClubOS.EdgeController.Storage;

namespace ClubOS.EdgeController.Workers;

/// <summary>
/// Таймер лимита сессий (Player Shell, M1). Работает на Edge, а не в Cloud: клуб без интернета
/// продолжает завершать оплаченные пакеты вовремя (ТЗ §7.2, §23.3). Проверка раз в секунду —
/// дешёвый индексный запрос; время окончания в любом случае = плановое окончание.
/// </summary>
public sealed class SessionTimerWorker(EdgeStore store, TimeProvider time, ILogger<SessionTimerWorker> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var session in await store.EndExpiredSessionsAsync(stoppingToken))
                {
                    logger.LogInformation("Сессия {SessionId} на {DeviceId} завершена по лимиту времени, итог {Total}",
                        session.SessionId, session.DeviceId, session.TotalMinorUnits);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Ошибка таймера сессий");
            }

            try
            {
                await Task.Delay(Interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
