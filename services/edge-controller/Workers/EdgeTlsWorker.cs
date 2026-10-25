using ClubOS.Contracts;
using ClubOS.EdgeController.Cloud;
using ClubOS.EdgeController.Storage;
using Microsoft.Extensions.Options;

namespace ClubOS.EdgeController.Workers;

/// <summary>
/// Выпускает и продлевает TLS-сертификат API агентов у CA Cloud (D-007): после регистрации Edge, за 30 дней
/// до истечения и при смене IP/имён сервера клуба. Пока Cloud недоступен, повтор каждые 30 секунд до первого
/// сертификата; HTTPS-порт до этого отклоняет рукопожатия, HTTP-порт (если включён) работает.
/// </summary>
public sealed class EdgeTlsWorker(
    EdgeIdentityStore identity,
    EdgeTlsCertificateStore store,
    CloudClient cloud,
    IOptions<EdgeOptions> options,
    TimeProvider time,
    ILogger<EdgeTlsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.AgentTlsPort <= 0)
        {
            return;
        }

        await identity.WhenEnrolled.WaitAsync(stoppingToken);
        if (store.TryLoad(identity.Current!.CaCertificatePem))
        {
            logger.LogInformation("TLS-сертификат API агентов загружен, действует до {NotAfter}", store.Certificate!.NotAfter);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var retry = TimeSpan.FromMinutes(Math.Max(1, options.Value.CertificateCheckMinutes));
            try
            {
                await EnsureAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("TLS-сертификат API агентов не получен: {Error}", ex.Message);
                if (store.Certificate is null)
                {
                    retry = TimeSpan.FromSeconds(30);
                }
            }

            try
            {
                await Task.Delay(retry, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>true — выпущен новый сертификат.</summary>
    public async Task<bool> EnsureAsync(CancellationToken ct)
    {
        var (dns, ips) = EdgeTlsCertificateStore.DiscoverNames(options.Value.TlsHostNames.Split(','));
        if (!EdgeTlsCertificateStore.NeedsRenewal(store.Certificate, dns, ips, time.GetUtcNow(),
                TimeSpan.FromDays(options.Value.CertificateRenewBeforeDays)))
        {
            return false;
        }

        using var key = store.GetOrCreateKey();
        var response = await cloud.IssueServerCertificateAsync(new EdgeServerCertificateRequest
        {
            CertificateSigningRequestPem = store.CreateSigningRequestPem(key),
            DnsNames = dns,
            IpAddresses = ips
        }, ct);
        store.Save(response.CertificatePem, identity.Current!.CaCertificatePem);
        logger.LogInformation("TLS-сертификат API агентов выпущен до {ExpiresAt}: {Names}", response.CertificateExpiresAtUtc,
            string.Join(", ", dns.Concat(ips)));
        return true;
    }
}
