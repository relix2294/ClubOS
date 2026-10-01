using ClubOS.EdgeController.Cloud;
using ClubOS.EdgeController.Storage;
using Microsoft.Extensions.Options;

namespace ClubOS.EdgeController.Workers;

/// <summary>
/// Long-poll очереди Cloud → Edge (исходящее соединение). Элемент сначала durable сохраняется
/// в SQLite (inbox + эффект), и только потом подтверждается Cloud — потерять команду нельзя,
/// повторная доставка отбрасывается inbox'ом.
/// </summary>
public sealed class CommandPuller(
    EdgeIdentityStore identity,
    EdgeStore store,
    CloudClient cloud,
    IOptions<EdgeOptions> options,
    TimeProvider time,
    ILogger<CommandPuller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await identity.WhenEnrolled.WaitAsync(stoppingToken);
        var backoff = new Backoff(TimeSpan.FromSeconds(options.Value.MaxBackoffSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await cloud.PullCommandsAsync(options.Value.CommandLongPollSeconds, stoppingToken);
                foreach (var id in response.Unsupported)
                {
                    // Cloud новее этого Edge: элемент не подтверждаем, он истечёт в очереди Cloud. Обновите Edge.
                    logger.LogWarning("Команда Cloud {Id} неизвестного вида пропущена — нужна более новая версия Edge", id);
                }

                foreach (var command in response.Commands)
                {
                    var applied = await store.ApplyCloudCommandAsync(command, stoppingToken);
                    logger.LogInformation("Команда Cloud {Id} ({Kind}) {Result}", command.Id, command.Kind,
                        applied ? "применена" : "уже была применена (дубль)");
                }

                if (response.Commands.Count > 0)
                {
                    await cloud.AckCommandsAsync(response.Commands.Select(x => x.Id).ToList(), stoppingToken);
                }
                else if (response.Unsupported.Count > 0)
                {
                    // Cloud сразу вернёт тот же неподтверждённый элемент — не крутим long-poll вхолостую.
                    await Task.Delay(TimeSpan.FromSeconds(10), time, stoppingToken);
                }

                backoff.Reset();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var delay = backoff.Next();
                logger.LogWarning("Получение команд из Cloud не удалось, повтор через {Delay:F0} с: {Error}",
                    delay.TotalSeconds, ex.Message);
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
}
