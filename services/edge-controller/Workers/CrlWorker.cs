using ClubOS.EdgeController.Cloud;
using ClubOS.EdgeController.Storage;
using ClubOS.Security;
using Microsoft.Extensions.Options;

namespace ClubOS.EdgeController.Workers;

/// <summary>
/// Обновляет список отзыва CA (D-002) каждые <see cref="EdgeOptions.CrlRefreshSeconds"/>. Без связи с Cloud
/// действует последний сохранённый список — даже просроченный (клуб без интернета не теряет проверку отзыва),
/// о просрочке пишется предупреждение.
/// </summary>
public sealed class CrlWorker(
    EdgeIdentityStore identity,
    EdgeCrlStore store,
    CloudClient cloud,
    IOptions<EdgeOptions> options,
    TimeProvider time,
    ILogger<CrlWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await identity.WhenEnrolled.WaitAsync(stoppingToken);
        if (store.TryLoad(identity.Current!.CaCertificatePem))
        {
            logger.LogInformation("Список отзыва загружен: №{Number}, отозвано {Count}", store.Current!.Number,
                store.Current.RevokedSerials.Count);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(stoppingToken);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.CrlRefreshSeconds)), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>true — получен и сохранён более новый список.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        try
        {
            var updated = store.Apply(await cloud.GetCrlAsync(ct), identity.Current!.CaCertificatePem);
            if (updated)
            {
                logger.LogInformation("Список отзыва обновлён: №{Number}, отозвано {Count}", store.Current!.Number,
                    store.Current.RevokedSerials.Count);
            }

            return updated;
        }
        catch (InvalidCrlException ex)
        {
            logger.LogError("Список отзыва от Cloud отклонён: {Error}", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Список отзыва не получен: {Error}", ex.Message);
        }

        if (store.Current?.NextUpdateUtc is { } next && next < time.GetUtcNow())
        {
            logger.LogWarning("Список отзыва просрочен с {NextUpdate}: нет связи с Cloud, действует последний полученный", next);
        }

        return false;
    }
}
