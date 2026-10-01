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

    /// <summary>Сертификат dev CA: агент закрепляет его и дальше доверяет TLS Edge только от этого CA (D-007).</summary>
    public string? CaCertificatePem { get; init; }
}

/// <summary>
/// Загрузка бездискового ПК (D-018): агент без сохранённой identity сообщает Edge аппаратный идентификатор и CSR
/// нового ключа. Анонимно, но только по TLS с закреплённым CA. Подтверждённый ПК получает сертификат локального CA
/// Edge на тот же deviceId; неизвестный попадает в «Ожидают подтверждения» в Admin Web.
/// </summary>
public sealed record DisklessBootRequest
{
    /// <summary>Нормализованный MAC загрузочной сетевой карты (12 шестнадцатеричных цифр, верхний регистр).</summary>
    public required string HardwareId { get; init; }

    public required IReadOnlyList<string> MacAddresses { get; init; }
    public required DeviceInventory Inventory { get; init; }
    public required string CertificateSigningRequestPem { get; init; }

    /// <summary>Device Simulator (демо-зал): подтверждённый ПК получит метку SIMULATED.</summary>
    public bool Simulated { get; init; }
}

public enum DisklessBootStatus
{
    /// <summary>ПК подтверждён: в ответе сертификат и данные устройства.</summary>
    Approved,

    /// <summary>ПК ещё не подтверждён администратором — повторить через RetryAfterSeconds.</summary>
    Pending,

    /// <summary>ПК с этим идентификатором сейчас на связи (второй экземпляр или подмена MAC) — повторить позже.</summary>
    Conflict
}

public sealed record DisklessBootResponse
{
    public required DisklessBootStatus Status { get; init; }
    public DeviceEnrollResponse? Enrollment { get; init; }
    public int RetryAfterSeconds { get; init; }
    public string? Message { get; init; }
}

/// <summary>Аппаратный идентификатор бездискового ПК.</summary>
public static class HardwareIds
{
    /// <summary>"0a-1b-2c:3d.4e5f" → "0A1B2C3D4E5F"; null — не MAC из 12 шестнадцатеричных цифр или «пустой» MAC.</summary>
    public static string? NormalizeMac(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var hex = new string(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        return hex.Length == 12 && hex != "000000000000" && hex != "FFFFFFFFFFFF" &&
               value.All(c => Uri.IsHexDigit(c) || c is ':' or '-' or '.' or ' ')
            ? hex
            : null;
    }

    /// <summary>"0A1B2C3D4E5F" → "0A:1B:2C:3D:4E:5F" для показа администратору.</summary>
    public static string FormatMac(string hardwareId) =>
        hardwareId.Length == 12 ? string.Join(':', Enumerable.Range(0, 6).Select(i => hardwareId.Substring(i * 2, 2))) : hardwareId;
}
