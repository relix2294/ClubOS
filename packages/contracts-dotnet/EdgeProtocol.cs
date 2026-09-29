using System.Text.Json.Serialization;

namespace ClubOS.Contracts;

// Протокол Edge ↔ Cloud (ТЗ §6.2, §24). Edge всегда инициирует исходящее соединение.

/// <summary>Регистрация Edge по одноразовому токену (ТЗ §3.1, §25.2.3).</summary>
public sealed record EdgeEnrollRequest
{
    public required string EnrollmentToken { get; init; }

    /// <summary>CSR (PEM). Приватный ключ Edge остаётся на Edge.</summary>
    public required string CertificateSigningRequestPem { get; init; }
}

public sealed record EdgeEnrollResponse
{
    public required string EdgeId { get; init; }
    public required string TenantId { get; init; }
    public required string LocationId { get; init; }
    public required string EdgeCertificatePem { get; init; }

    /// <summary>Сертификат dev CA — Edge проверяет им credential агентов.</summary>
    public required string CaCertificatePem { get; init; }

    public required DateTimeOffset CertificateExpiresAtUtc { get; init; }
}

/// <summary>Кэшируемая на Edge конфигурация локации (нужна для offline-сессий).</summary>
public sealed record EdgeConfigResponse
{
    public required string LocationId { get; init; }
    public required string LocationName { get; init; }
    public required string Timezone { get; init; }
    public required string Currency { get; init; }
    public required IReadOnlyList<EdgeZoneConfig> Zones { get; init; }
    public required IReadOnlyList<EdgeDeviceConfig> Devices { get; init; }

    /// <summary>Отозванные устройства локации: Edge удаляет их и больше не принимает их запросы (D-011).</summary>
    public IReadOnlyList<string> RevokedDeviceIds { get; init; } = [];
}

public sealed record EdgeZoneConfig
{
    public required string ZoneId { get; init; }
    public required string Name { get; init; }
    public required long PricePerHourMinorUnits { get; init; }
    public required RoundingRule Rounding { get; init; }
    public required int RuleVersion { get; init; }
}

public sealed record EdgeDeviceConfig
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public required string ZoneId { get; init; }
    public required bool Simulated { get; init; }
    public required string CertificatePem { get; init; }
}

/// <summary>Пакет событий Edge → Cloud. Повторная отправка безопасна (UNIQUE eventId).</summary>
public sealed record SyncBatchRequest
{
    public required IReadOnlyList<EventEnvelope> Events { get; init; }
}

public sealed record SyncBatchResponse
{
    /// <summary>Приняты впервые.</summary>
    public required IReadOnlyList<string> Accepted { get; init; }

    /// <summary>Уже были приняты ранее (дубли) — Edge тоже считает их доставленными.</summary>
    public required IReadOnlyList<string> Duplicates { get; init; }

    /// <summary>Отклонены (невалидны); Edge помечает их как failed и не повторяет.</summary>
    public required IReadOnlyList<RejectedEvent> Rejected { get; init; }
}

public sealed record RejectedEvent
{
    public required string EventId { get; init; }
    public required string Reason { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EdgeCommandKind
{
    /// <summary>Команда устройству (ShowMessage/LockTestMode).</summary>
    DeviceCommand,

    /// <summary>Запрос старта сессии; источник истины — Edge (ТЗ §23.3).</summary>
    StartSession,

    EndSession,

    /// <summary>Продление сессии с лимитом времени (M1, Player Shell).</summary>
    ExtendSession,

    /// <summary>Устройство удалено в Admin Web: Edge забывает его сертификат (D-011).</summary>
    RevokeDevice
}

/// <summary>Элемент очереди Cloud → Edge. Edge дедуплицирует по <see cref="Id"/>.</summary>
public sealed record EdgeCommand
{
    public required string Id { get; init; }
    public required EdgeCommandKind Kind { get; init; }
    public required DateTimeOffset IssuedAtUtc { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public CommandEnvelope? DeviceCommand { get; init; }
    public StartSessionCommand? StartSession { get; init; }
    public EndSessionCommand? EndSession { get; init; }
    public ExtendSessionCommand? ExtendSession { get; init; }
    public RevokeDeviceCommand? RevokeDevice { get; init; }
}

public sealed record RevokeDeviceCommand
{
    public required string DeviceId { get; init; }
    public required string Actor { get; init; }
}

/// <summary>
/// Продление сертификата (Edge или устройства). Запрос подписан текущим ключом субъекта; CSR — от того же
/// ключа (ротация ключа — M2). CA назначает subject сам, как при enrollment.
/// </summary>
public sealed record CertificateRenewRequest
{
    public required string CertificateSigningRequestPem { get; init; }
}

/// <summary>
/// Серверный TLS-сертификат API агентов (D-007). Edge сообщает имена и адреса, по которым к нему обращаются ПК
/// клуба; CA выпускает сертификат с этими SAN и CN = edgeId. Ключ TLS остаётся на Edge.
/// </summary>
public sealed record EdgeServerCertificateRequest
{
    public required string CertificateSigningRequestPem { get; init; }
    public required IReadOnlyList<string> DnsNames { get; init; }
    public required IReadOnlyList<string> IpAddresses { get; init; }
}

public sealed record CertificateRenewResponse
{
    public required string CertificatePem { get; init; }
    public required DateTimeOffset CertificateExpiresAtUtc { get; init; }
}

public sealed record StartSessionCommand
{
    public required string SessionId { get; init; }
    public required string DeviceId { get; init; }
    public required PriceSnapshot PriceSnapshot { get; init; }
    public required string Actor { get; init; }
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Лимит времени в минутах (предоплаченный пакет). null — открытая сессия (постоплата).
    /// Отсчёт — от фактического старта на Edge, не от запроса в Cloud.
    /// </summary>
    public int? DurationMinutes { get; init; }
}

public sealed record EndSessionCommand
{
    public required string SessionId { get; init; }
    public required string Actor { get; init; }
    public required string CorrelationId { get; init; }
}

/// <summary>Продление лимита на <see cref="Minutes"/>. Идемпотентность — по Id элемента очереди (inbox Edge).</summary>
public sealed record ExtendSessionCommand
{
    public required string SessionId { get; init; }
    public required int Minutes { get; init; }
    public required string Actor { get; init; }
    public required string CorrelationId { get; init; }
}

public sealed record EdgeCommandsResponse
{
    public required IReadOnlyList<EdgeCommand> Commands { get; init; }
}

public sealed record EdgeCommandsAck
{
    public required IReadOnlyList<string> Ids { get; init; }
}

/// <summary>
/// Периодический снимок состояния устройств (не durable: потеря одного отчёта безопасна,
/// следующий отчёт его заменит). Значимые переходы идут durable-событиями.
/// </summary>
public sealed record EdgeStatusReport
{
    public required DateTimeOffset EdgeClockUtc { get; init; }
    public required int PendingOutboxEvents { get; init; }
    public required IReadOnlyList<DeviceStatusEntry> Devices { get; init; }
}

public sealed record DeviceStatusEntry
{
    public required string DeviceId { get; init; }
    public required DeviceStatus Status { get; init; }
    public DateTimeOffset? LastHeartbeatUtc { get; init; }
    public DeviceInventory? Inventory { get; init; }
}
