using ClubOS.Contracts;
using ClubOS.EdgeController.Cloud;
using ClubOS.EdgeController.Storage;
using Microsoft.Extensions.Options;

namespace ClubOS.EdgeController.Workers;

/// <summary>
/// Продлевает сертификат Edge за <see cref="EdgeOptions.CertificateRenewBeforeDays"/> дней до истечения (D-011).
/// Запрос подписан ещё действующим ключом; ключ остаётся прежним (ротация ключа — M2).
/// Если Cloud недоступен, попытка повторяется при следующей проверке — запас в 30 дней покрывает долгий offline.
/// </summary>
public sealed class CertificateRenewalWorker(
    EdgeIdentityStore identity,
    CloudClient cloud,
    IOptions<EdgeOptions> options,
    TimeProvider time,
    ILogger<CertificateRenewalWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await identity.WhenEnrolled.WaitAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RenewIfDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Продление сертификата Edge не удалось, повтор позже: {Error}", ex.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, options.Value.CertificateCheckMinutes)), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>true — сертификат продлён.</summary>
    public async Task<bool> RenewIfDueAsync(CancellationToken ct)
    {
        var current = identity.Current;
        if (current is null ||
            current.CertificateExpiresAtUtc - time.GetUtcNow() > TimeSpan.FromDays(options.Value.CertificateRenewBeforeDays))
        {
            return false;
        }

        var response = await cloud.RenewEdgeAsync(new CertificateRenewRequest
        {
            CertificateSigningRequestPem = identity.Key.CreateSigningRequestPem("clubos-edge")
        }, ct);
        identity.Save(current with
        {
            CertificatePem = response.CertificatePem,
            CertificateExpiresAtUtc = response.CertificateExpiresAtUtc
        });
        logger.LogInformation("Сертификат Edge продлён до {ExpiresAt}", response.CertificateExpiresAtUtc);
        return true;
    }
}
