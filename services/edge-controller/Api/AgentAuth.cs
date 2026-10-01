using ClubOS.EdgeController.Storage;
using ClubOS.Security;
using System.Security.Cryptography.X509Certificates;

namespace ClubOS.EdgeController.Api;

/// <summary>
/// Проверка подписанных токенов агентов: сертификат устройства выдан dev CA (из identity Edge),
/// CN = deviceId, подпись/срок/jti. Валидатор живёт весь процесс — кэш jti против повторов.
/// </summary>
public sealed class AgentAuth(EdgeIdentityStore identity, EdgeStore store, TimeProvider time,
    Microsoft.Extensions.Options.IOptions<EdgeOptions> options, DisklessAuthority diskless, EdgeCrlStore crl,
    ILogger<AgentAuth> logger)
{
    private readonly Lock _gate = new();
    private SignedTokenValidator? _validator;
    private SignedTokenValidator? _disklessValidator;
    private X509Certificate2? _cloudCa;

    /// <summary>Ключ элементов соединения: клиентский сертификат уже проверен (цепочка и CN) для этого устройства.</summary>
    private const string VerifiedClientItem = "clubos.agent.clientCertificate";

    /// <summary>Ключ HttpContext.Items: запрос подписан прежним ключом устройства (D-011).</summary>
    public const string PreviousKeyItem = "clubos.agent.previousKey";

    public static bool UsedPreviousKey(HttpContext http) => http.Items.TryGetValue(PreviousKeyItem, out var v) && v is true;

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
        var binding = SignedBody.Binding(http);
        var result = validator.Validate(token, certificate, SignedToken.AudienceEdge,
            DevCertificateAuthority.RoleDevice, binding, options.Value.RequireAgentRequestBinding);
        var usedPrevious = false;
        // После ротации ключа (D-011) прежний ключ агента принимается, пока агент не перешёл на новый.
        if (!result.Success && result.Error == "bad signature" && !device.Diskless && device.PreviousCertificatePem is { } previous)
        {
            result = validator.Validate(token, previous, SignedToken.AudienceEdge, DevCertificateAuthority.RoleDevice, binding,
                options.Value.RequireAgentRequestBinding);
            usedPrevious = result.Success;
        }

        if (!result.Success)
        {
            logger.LogWarning("Agent {DeviceId} auth rejected: {Reason}", deviceId, result.Error);
            return null;
        }

        // Сертификат, которым подписан токен, отозван CA (CRL, D-002) — даже если конфигурация Cloud ещё не пришла.
        if (!device.Diskless && crl.Current is { } list && list.IsRevoked(usedPrevious ? device.PreviousCertificatePem! : certificate))
        {
            logger.LogWarning("Agent {DeviceId} auth rejected: сертификат устройства отозван (CRL)", deviceId);
            return null;
        }

        if (CheckClientCertificate(http, deviceId, device.Diskless) is { } rejected)
        {
            logger.LogWarning("Agent {DeviceId} auth rejected: {Reason}", deviceId, rejected);
            return null;
        }

        http.Items[PreviousKeyItem] = usedPrevious;
        if (!usedPrevious && device.PreviousCertificatePem is not null)
        {
            store.ForgetPreviousDeviceCertificateAsync(deviceId).GetAwaiter().GetResult();
            logger.LogInformation("Агент {DeviceId} перешёл на новый ключ — прежний сертификат забыт", deviceId);
        }

        return deviceId;
    }

    /// <summary>
    /// mTLS (D-002): предъявленный при TLS-рукопожатии сертификат должен быть выпущен CA устройства (Cloud или
    /// локальный CA бездисковых ПК), действовать, принадлежать этому же устройству (CN = deviceId из токена) и не
    /// быть в CRL. Без сертификата — отказ только в режиме <see cref="EdgeOptions.RequireAgentClientCertificate"/>.
    /// Ключ сертификата не сверяется с ключом токена: после ротации ключа открытое соединение ещё несёт прежний
    /// сертификат того же устройства, а владение текущим ключом доказывает подпись токена.
    /// </summary>
    /// <returns>null — проверка пройдена, иначе причина отказа.</returns>
    public string? CheckClientCertificate(HttpContext http, string deviceId, bool disklessDevice)
    {
        var client = http.Connection.ClientCertificate;
        if (client is null)
        {
            return !options.Value.RequireAgentClientCertificate ? null
                : http.Request.IsHttps ? "нет клиентского сертификата (Edge:RequireAgentClientCertificate)"
                : "запрос без TLS, а клиентский сертификат обязателен (Edge:RequireAgentClientCertificate)";
        }

        if (!disklessDevice && crl.IsRevoked(client))
        {
            return "клиентский сертификат отозван (CRL)";
        }

        // Цепочка проверяется один раз на соединение (keep-alive), CRL — на каждый запрос.
        var items = http.Features.Get<Microsoft.AspNetCore.Connections.Features.IConnectionItemsFeature>()?.Items;
        if (items is not null && items.TryGetValue(VerifiedClientItem, out var verified) && verified is string id && id == deviceId)
        {
            return null;
        }

        if (client.GetNameInfo(X509NameType.SimpleName, false) != deviceId)
        {
            return "клиентский сертификат выдан другому устройству";
        }

        var ca = disklessDevice ? diskless.Ca.Certificate : CloudCa();
        if (!EdgeTlsTrust.IsClientIssuedBy(client, ca, time.GetUtcNow()))
        {
            return "клиентский сертификат выпущен не CA ClubOS или истёк";
        }

        if (items is not null)
        {
            items[VerifiedClientItem] = deviceId;
        }

        return null;
    }

    private X509Certificate2 CloudCa()
    {
        lock (_gate)
        {
            return _cloudCa ??= X509Certificate2.CreateFromPem(identity.Current!.CaCertificatePem);
        }
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
            return _validator ??= new SignedTokenValidator(CloudCa(), time);
        }
    }
}
