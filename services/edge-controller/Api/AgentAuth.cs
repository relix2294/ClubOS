using ClubOS.EdgeController.Storage;
using ClubOS.Security;
using System.Security.Cryptography.X509Certificates;

namespace ClubOS.EdgeController.Api;

/// <summary>
/// Проверка подписанных токенов агентов: сертификат устройства выдан dev CA (из identity Edge),
/// CN = deviceId, подпись/срок/jti. Валидатор живёт весь процесс — кэш jti против повторов.
/// </summary>
public sealed class AgentAuth(EdgeIdentityStore identity, EdgeStore store, TimeProvider time,
    Microsoft.Extensions.Options.IOptions<EdgeOptions> options, ILogger<AgentAuth> logger)
{
    private readonly Lock _gate = new();
    private SignedTokenValidator? _validator;

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

        var result = Validator().Validate(token, device.CertificatePem, SignedToken.AudienceEdge,
            DevCertificateAuthority.RoleDevice, SignedBody.Binding(http), options.Value.RequireAgentRequestBinding);
        if (!result.Success)
        {
            logger.LogWarning("Agent {DeviceId} auth rejected: {Reason}", deviceId, result.Error);
            return null;
        }

        return deviceId;
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
