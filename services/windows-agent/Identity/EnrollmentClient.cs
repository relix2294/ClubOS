using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClubOS.Contracts;
using ClubOS.WindowsAgent.Config;
using ClubOS.WindowsAgent.Inventory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClubOS.WindowsAgent.Identity;

/// <summary>
/// Enrollment устройства (ТЗ §3.1): генерирует пару ключей, отправляет CSR + инвентаризацию
/// в Cloud по one-time токену, сохраняет выданный deviceId и сертификат. Приватный ключ
/// сохраняется только локально и не покидает устройство.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EnrollmentClient(
    HttpClient http,
    IOptions<AgentOptions> options,
    InventoryCollector inventory,
    ILogger<EnrollmentClient> logger)
{
    private readonly AgentOptions _options = options.Value;

    /// <summary>Возвращает существующую идентичность или выполняет enrollment. null — если невозможно.</summary>
    public async Task<DeviceIdentity?> EnsureEnrolledAsync(CancellationToken ct)
    {
        var existing = DeviceIdentity.TryLoad(_options.DataDirectory);
        if (existing is not null)
        {
            return existing;
        }

        var enrollmentToken = _options.EnrollmentToken;
        if (string.IsNullOrWhiteSpace(enrollmentToken))
        {
            logger.LogWarning(
                "Устройство не enroll'ено и enrollment-токен не задан (CLUBOS_ENROLLMENT_TOKEN). Heartbeat отложен.");
            return null;
        }

        using var rsa = RSA.Create(2048);
        var subject = string.IsNullOrWhiteSpace(_options.DisplayName) ? Environment.MachineName : _options.DisplayName;
        var request = new CertificateRequest($"CN={subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var csrPem = request.CreateSigningRequestPem();
        var keyPem = rsa.ExportPkcs8PrivateKeyPem();

        var body = new DeviceEnrollRequest
        {
            EnrollmentToken = enrollmentToken,
            Inventory = inventory.Collect(),
            CertificateSigningRequestPem = csrPem,
        };

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("api/v1/enrollment/devices", body, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Enrollment: не удалось связаться с Cloud ({Url}).", _options.CloudBaseUrl);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Enrollment отклонён Cloud: HTTP {Status}.", (int)response.StatusCode);
            return null;
        }

        var enrolled = await response.Content.ReadFromJsonAsync<DeviceEnrollResponse>(ct);
        if (enrolled is null)
        {
            logger.LogError("Enrollment: пустой ответ Cloud.");
            return null;
        }

        Directory.CreateDirectory(_options.DataDirectory);
        await File.WriteAllTextAsync(DeviceIdentity.PrivateKeyFile(_options.DataDirectory), keyPem, ct);

        var identity = new DeviceIdentity
        {
            DeviceId = enrolled.DeviceId,
            CertificatePem = enrolled.DeviceCertificatePem,
            EnrolledAtUtc = DateTimeOffset.UtcNow,
        };
        identity.Save(_options.DataDirectory);

        logger.LogInformation("Enrollment успешен, deviceId={DeviceId}.", identity.DeviceId);
        return identity;
    }
}
