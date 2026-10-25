using ClubOS.Contracts;
using ClubOS.EdgeController.Storage;
using ClubOS.Security;
using Microsoft.Extensions.Options;

namespace ClubOS.EdgeController.Api;

/// <summary>
/// Локальный CA Edge для бездисковых ПК (D-018). Ключ ПК генерируется при каждой загрузке, поэтому сертификат нужен
/// каждый раз и без связи с Cloud: его выпускает Edge. Эти сертификаты принимает только сам Edge (API агентов);
/// Cloud их не видит — запросы ПК к Cloud всегда идут от имени Edge.
/// </summary>
public sealed class DisklessAuthority(IOptions<EdgeOptions> options, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan CertificateValidity = TimeSpan.FromDays(90);

    private readonly Lazy<DevCertificateAuthority> _ca = new(() => DevCertificateAuthority.LoadOrCreate(
        Path.Combine(options.Value.DataPath, "diskless-ca"), time, "CN=ClubOS Edge Local CA (diskless), O=ClubOS"));

    public DevCertificateAuthority Ca => _ca.Value;

    public void Dispose()
    {
        if (_ca.IsValueCreated)
        {
            _ca.Value.Dispose();
        }
    }
}

public static class DisklessBoot
{
    public const int PendingRetrySeconds = 10;
    public const int ConflictRetrySeconds = 15;
    public const int MaxCsrLength = 8192;

    /// <summary>
    /// Загрузка бездискового ПК. Подтверждённый (MAC привязан в Cloud) получает сертификат локального CA на свой
    /// deviceId; неизвестный попадает в список ожидающих (уходит в Cloud с отчётом о статусе).
    /// </summary>
    public static async Task<IResult> Boot(DisklessBootRequest request, EdgeIdentityStore identity, EdgeStore store,
        DisklessAuthority authority, IOptions<EdgeOptions> options, TimeProvider time, ILoggerFactory logs, CancellationToken ct)
    {
        var log = logs.CreateLogger("Diskless");
        if (identity.Current is not { } edge)
        {
            return Results.Problem(statusCode: 503, title: "Edge не зарегистрирован в Cloud.");
        }

        var hardwareId = HardwareIds.NormalizeMac(request.HardwareId);
        if (hardwareId is null || request.Inventory is null || string.IsNullOrWhiteSpace(request.CertificateSigningRequestPem) ||
            request.CertificateSigningRequestPem.Length > MaxCsrLength)
        {
            return Results.Problem(statusCode: 400, title: "Неверный запрос загрузки бездискового ПК.");
        }

        var device = store.FindDeviceByHardware(hardwareId);
        if (device is null)
        {
            var recorded = await store.RecordDisklessCandidateAsync(request, hardwareId, ct);
            if (recorded)
            {
                log.LogInformation("Бездисковый ПК {Mac} ({Host}) ожидает подтверждения", HardwareIds.FormatMac(hardwareId),
                    request.Inventory.Hostname);
            }

            return Results.Ok(new DisklessBootResponse
            {
                Status = DisklessBootStatus.Pending,
                RetryAfterSeconds = PendingRetrySeconds,
                Message = recorded
                    ? $"ПК ожидает подтверждения в Admin Web (MAC {HardwareIds.FormatMac(hardwareId)})."
                    : "Слишком много неподтверждённых ПК — подтвердите или отклоните их в Admin Web."
            });
        }

        var now = time.GetUtcNow();
        // Heartbeat свежее окна — ПК с этим MAC на связи: второй «такой же» ПК получает Conflict.
        if (device.Online && device.LastHeartbeatUtc is { } heartbeat &&
            now - heartbeat < TimeSpan.FromSeconds(options.Value.DisklessConflictSeconds))
        {
            log.LogWarning("Бездисковый ПК {Mac} ({DeviceId}) уже на связи — повторная загрузка отклонена (второй экземпляр или подмена MAC)",
                HardwareIds.FormatMac(hardwareId), device.DeviceId);
            return Results.Ok(new DisklessBootResponse
            {
                Status = DisklessBootStatus.Conflict,
                RetryAfterSeconds = ConflictRetrySeconds,
                Message = $"ПК с MAC {HardwareIds.FormatMac(hardwareId)} уже на связи. Если это перезагрузка — подождите."
            });
        }

        IssuedCertificate issued;
        try
        {
            issued = authority.Ca.Issue(request.CertificateSigningRequestPem, device.DeviceId, DevCertificateAuthority.RoleDevice,
                DisklessAuthority.CertificateValidity);
        }
        catch (InvalidCsrException ex)
        {
            return Results.Problem(statusCode: 400, title: "Неверный CSR.", detail: ex.Message);
        }

        await store.SetLocalCertificateAsync(device.DeviceId, issued.CertificatePem, request.Inventory, ct);
        log.LogInformation("Бездисковый ПК {DeviceId} ({Name}) загрузился, MAC {Mac}", device.DeviceId, device.DisplayName,
            HardwareIds.FormatMac(hardwareId));
        return Results.Ok(new DisklessBootResponse
        {
            Status = DisklessBootStatus.Approved,
            Enrollment = new DeviceEnrollResponse
            {
                DeviceId = device.DeviceId,
                DeviceCertificatePem = issued.CertificatePem,
                CertificateExpiresAtUtc = issued.ExpiresAtUtc,
                DisplayName = device.DisplayName,
                ZoneId = device.ZoneId,
                Simulated = device.Simulated,
                CaCertificatePem = edge.CaCertificatePem
            }
        });
    }
}
