using ClubOS.EdgeController.Storage;
using ClubOS.Security;
using System.Security.Cryptography.X509Certificates;

namespace ClubOS.EdgeController.Api;

/// <summary>
/// Проверка подписанных токенов агентов: сертификат устройства выдан dev CA (из identity Edge),
/// CN = deviceId, подпись/срок/jti. Валидатор живёт весь процесс — кэш jti против повторов.
/// </summary>
public sealed class AgentAuth(EdgeIdentityStore identity, EdgeStore store, TimeProvider time,
    Microsoft.Extensions.Options.IOptions<EdgeOptions> options, DisklessAuthority diskless, ILogger<AgentAuth> logger)
{
    private readonly Lock _gate = new();
    private SignedTokenValidator? _validator;
    private SignedTokenValidator? _disklessValidator;

    public string? Authenticate(HttpContext http)
    {
        string? header = http.Request.Headers.Authorization;
        if (identity.Current is null || string.IsNullOrEmpty(header) ||
            !header.StartsWith(SignedToken.Scheme + " ", StringComparison.Ordinal))
        {
            return null;
        }

        var token = header[(SignedToken.Scheme.Length + 1)..].Trim();
        if (!SignedToken.TryReadKeyId(token, out var deviceId))
        {
            return null;
        }

        var device = store.GetDevice(deviceId);
        if (device is null)
        {
            return null;
        }

        // Бездисковый ПК (D-018): сертификат последней загрузки от локального CA Edge; обычный — от CA Cloud.
        if (device.Diskless && device.LocalCertificatePem is null)
        {
            return null;
        }

        var (validator, certificate) = device.Diskless
            ? (DisklessValidator(), device.LocalCertificatePem!)
            : (Validator(), device.CertificatePem);
        var result = validator.Validate(token, certificate, SignedToken.AudienceEdge,
            DevCertificateAuthority.RoleDevice, SignedBody.Binding(http), options.Value.RequireAgentRequestBinding);
        if (!result.Success)
        {
            logger.LogWarning("Agent {DeviceId} auth rejected: {Reason}", deviceId, result.Error);
            return null;
        }

        return deviceId;
    }

    private SignedTokenValidator DisklessValidator()
    {
        lock (_gate)
        {
            return _disklessValidator ??= new SignedTokenValidator(diskless.Ca.Certificate, time);
        }
    }

    private SignedTokenValidator Validator()
    {
        lock (_gate)
        {
            return _validator ??= new SignedTokenValidator(
                X509Certificate2.CreateFromPem(identity.Current!.CaCertificatePem), time);
        }
    }
}
