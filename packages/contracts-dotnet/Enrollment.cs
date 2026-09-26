namespace ClubOS.Contracts;

/// <summary>
/// Запрос выдачи одноразового enrollment-токена для устройства (ТЗ §8 AUTH-007, §3.1).
/// Выдаётся авторизованным сотрудником через Cloud API.
/// </summary>
public sealed record EnrollmentTokenRequest
{
    public required string LocationId { get; init; }
    public required string ZoneId { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>true — токен для Device Simulator; устройство будет помечено SIMULATED (ТЗ §3.4).</summary>
    public bool Simulated { get; init; }
}

/// <summary>Ответ с одноразовым токеном (используется один раз, имеет TTL).</summary>
public sealed record EnrollmentTokenResponse
{
    public required string EnrollmentToken { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
}

/// <summary>
/// Обмен одноразового токена на индивидуальную credential устройства (ТЗ §3.1).
/// После enrollment общий статический токен не используется.
/// </summary>
public sealed record DeviceEnrollRequest
{
    public required string EnrollmentToken { get; init; }
    public required DeviceInventory Inventory { get; init; }

    /// <summary>
    /// CSR/публичный ключ устройства в PEM. Приватный ключ НИКОГДА не покидает устройство
    /// и не попадает в Cloud-логи (ТЗ §3.1, §27.3).
    /// </summary>
    public required string CertificateSigningRequestPem { get; init; }
}

/// <summary>Индивидуальная credential устройства после enrollment.</summary>
public sealed record DeviceEnrollResponse
{
    public required string DeviceId { get; init; }

    /// <summary>Подписанный dev-CA сертификат устройства (PEM). Не приватный ключ.</summary>
    public required string DeviceCertificatePem { get; init; }

    public required DateTimeOffset CertificateExpiresAtUtc { get; init; }

    public required string DisplayName { get; init; }
    public required string ZoneId { get; init; }
    public required bool Simulated { get; init; }
}
