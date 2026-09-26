using ClubOS.EdgeController.Cloud;
using ClubOS.EdgeController.Storage;
using ClubOS.Contracts;
using Microsoft.Extensions.Options;

namespace ClubOS.EdgeController.Workers;

/// <summary>
/// Первый запуск: регистрация Edge в Cloud по одноразовому токену, затем периодическое
/// обновление кэша конфигурации (зоны/тарифы/устройства) — нужен для offline-работы.
/// </summary>
public sealed class EnrollmentWorker(
    EdgeIdentityStore identity,
    CloudClient cloud,
    EdgeStore store,
    IOptions<EdgeOptions> options,
    TimeProvider time,
    ILogger<EnrollmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = new Backoff(TimeSpan.FromSeconds(options.Value.MaxBackoffSeconds));
        while (!identity.IsEnrolled && !stoppingToken.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(options.Value.EnrollmentToken))
            {
                logger.LogError("Edge не зарегистрирован: задайте одноразовый токен CLUBOS_Edge__EnrollmentToken и перезапустите.");
                await Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            try
            {
                var key = identity.GetOrCreatePendingKey();
                var response = await cloud.EnrollAsync(new EdgeEnrollRequest
                {
                    EnrollmentToken = options.Value.EnrollmentToken.Trim(),
                    CertificateSigningRequestPem = key.CreateSigningRequestPem("clubos-edge")
                }, stoppingToken);

                identity.Save(new EdgeIdentity
                {
                    EdgeId = response.EdgeId,
                    TenantId = response.TenantId,
                    LocationId = response.LocationId,
                    CertificatePem = response.EdgeCertificatePem,
                    CaCertificatePem = response.CaCertificatePem,
                    CertificateExpiresAtUtc = response.CertificateExpiresAtUtc
                });
                logger.LogInformation("Edge зарегистрирован: {EdgeId}, локация {LocationId}", response.EdgeId, response.LocationId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Enrollment Edge не удался: {Error}", ex.Message);
                await Delay(backoff.Next(), stoppingToken);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await store.SaveConfigAsync(await cloud.GetConfigAsync(stoppingToken), stoppingToken);
                backoff.Reset();
                await Delay(TimeSpan.FromSeconds(options.Value.ConfigRefreshSeconds), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Не удалось обновить конфигурацию из Cloud (работаем по кэшу): {Error}", ex.Message);
                await Delay(backoff.Next(), stoppingToken);
            }
        }
    }

    private async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, time, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
