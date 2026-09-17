using ClubOS.Contracts;

namespace ClubOS.CloudApi.Domain;

// Сущности домена Cloud (ТЗ §23.1). Деньги — bigint minor units; время — UTC (timestamptz).
// ID — строки (ULID/GUID) для совместимости с контрактами.

public sealed class Organization
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public List<Location> Locations { get; } = [];
    public List<User> Users { get; } = [];
}

public sealed class Location
{
    public required string Id { get; set; }
    public required string OrganizationId { get; set; }
    public required string Name { get; set; }
    public required string Timezone { get; set; }   // IANA, напр. "Asia/Dushanbe"
    public required string Currency { get; set; }    // ISO 4217, напр. "TJS"
    public DateTimeOffset CreatedAtUtc { get; set; }

    public List<Zone> Zones { get; } = [];
    public List<Device> Devices { get; } = [];
}

public sealed class Zone
{
    public required string Id { get; set; }
    public required string LocationId { get; set; }
    public required string Name { get; set; }        // "Standard", "VIP"
}

public sealed class User
{
    public required string Id { get; set; }
    public required string OrganizationId { get; set; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public required string Role { get; set; }        // "Owner" и т.п. (ТЗ §5)
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>Edge Controller клуба, зарегистрированный в Cloud (ТЗ §6.2, §25.2.3).</summary>
public sealed class Edge
{
    public required string Id { get; set; }
    public required string LocationId { get; set; }
    public required string Name { get; set; }
    public string? CertificatePem { get; set; }
    public DateTimeOffset? EnrolledAtUtc { get; set; }
    public DateTimeOffset? LastSeenAtUtc { get; set; }
}

/// <summary>Одноразовый enrollment-токен для устройства (ТЗ §8 AUTH-007). Хранится хэш.</summary>
public sealed class EnrollmentToken
{
    public required string Id { get; set; }
    public required string LocationId { get; set; }
    public required string ZoneId { get; set; }
    public required string DisplayName { get; set; }
    public required string TokenHash { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? UsedAtUtc { get; set; }
}

public sealed class Device
{
    public required string Id { get; set; }
    public required string LocationId { get; set; }
    public required string ZoneId { get; set; }
    public required string DisplayName { get; set; }
    public bool Simulated { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.Offline;
    public DateTimeOffset? LastHeartbeatUtc { get; set; }

    // Инвентаризация (ТЗ §9 LOC-007, §3.3).
    public string? Hostname { get; set; }
    public string? WindowsVersion { get; set; }
    public string? Cpu { get; set; }
    public int? RamMegabytes { get; set; }
    public string? Ipv4 { get; set; }
    public string? AgentVersion { get; set; }

    public string? CertificatePem { get; set; }
    public DateTimeOffset? EnrolledAtUtc { get; set; }
}

/// <summary>Команда устройству со своим жизненным циклом (ТЗ §10.2, §24.3).</summary>
public sealed class DeviceCommand
{
    public required string Id { get; set; }          // = commandId
    public required string DeviceId { get; set; }
    public required string LocationId { get; set; }
    public CommandType CommandType { get; set; }
    public required string PayloadJson { get; set; } // jsonb
    public required string IssuedBy { get; set; }
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public required string CorrelationId { get; set; }
    public CommandState State { get; set; } = CommandState.Queued;
    public string? Error { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Сессия (ТЗ §12). Price snapshot фиксируется на старте; ended не редактируется.</summary>
public sealed class Session
{
    public required string Id { get; set; }
    public required string DeviceId { get; set; }
    public required string LocationId { get; set; }
    public SessionState State { get; set; } = SessionState.Created;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }

    // Снимок тарифа (ТЗ §12.2).
    public long PricePerHourMinorUnits { get; set; }
    public required string Currency { get; set; }
    public RoundingRule Rounding { get; set; }
    public int RuleVersion { get; set; }

    public long? TotalMinorUnits { get; set; }

    public required string Actor { get; set; }
    public required string CorrelationId { get; set; }
}

/// <summary>Дедупликация повторно доставленных событий sync (ТЗ §4). UNIQUE(EventId).</summary>
public sealed class InboxReceipt
{
    public required string EventId { get; set; }     // PK — ключ идемпотентности
    public required string TenantId { get; set; }
    public required string LocationId { get; set; }
    public required string EventType { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
}

/// <summary>Иммутабельная запись аудита (ТЗ §27.4).</summary>
public sealed class AuditEvent
{
    public required string Id { get; set; }
    public required string TenantId { get; set; }
    public string? LocationId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string Target { get; set; }
    public required string Result { get; set; }
    public string? CorrelationId { get; set; }
    public string? BeforeAfterJson { get; set; }     // безопасный diff без секретов
}
