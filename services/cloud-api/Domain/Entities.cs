using ClubOS.Contracts;

namespace ClubOS.CloudApi.Domain;

// Сущности домена Cloud (ТЗ §23.1). Деньги — bigint minor units; время — UTC (timestamptz).
// ID — строки с префиксом (GUID v7, сортируемые по времени) для совместимости с контрактами.
// TenantId (= OrganizationId) денормализован в операционные таблицы: tenant scope
// проверяется backend'ом в каждом запросе, а не доверяется ID из UI (ТЗ §4, §8 AUTH-001).

public sealed class Organization
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class Location
{
    public required string Id { get; set; }
    public required string OrganizationId { get; set; }
    public required string Name { get; set; }
    public required string Timezone { get; set; }   // IANA, напр. "Asia/Dushanbe"
    public required string Currency { get; set; }    // ISO 4217, напр. "TJS"
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class Zone
{
    public required string Id { get; set; }
    public required string LocationId { get; set; }
    public required string Name { get; set; }        // "Standard", "VIP"

    /// <summary>Тариф зоны (M0: 12000 = 120,00 TJS/час). Снимок берётся на старте сессии.</summary>
    public long PricePerHourMinorUnits { get; set; }
    public RoundingRule Rounding { get; set; } = RoundingRule.CeilingPerMinute;
    public int RuleVersion { get; set; } = 1;
}

public static class Roles
{
    /// <summary>Владелец: всё, включая управление персоналом.</summary>
    public const string Owner = "Owner";

    /// <summary>Администратор клуба: всё, кроме управления персоналом.</summary>
    public const string Admin = "Admin";

    /// <summary>Оператор смены: устройства, команды, сессии, аудит.</summary>
    public const string Operator = "Operator";

    public static readonly IReadOnlyList<string> All = [Owner, Admin, Operator];

    public static bool IsValid(string? role) => role is not null && All.Contains(role);
}

public sealed class User
{
    public required string Id { get; set; }
    public required string OrganizationId { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public required string PasswordHash { get; set; }
    public required string Role { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>
    /// Версия выданных токенов. Увеличивается при смене пароля/роли/отключении — все ранее выданные
    /// access-токены перестают приниматься сразу (проверка на каждом запросе), refresh отзываются.
    /// </summary>
    public int TokenVersion { get; set; }

    /// <summary>Временный пароль (создан или сброшен администратором) — до смены доступен только /me.</summary>
    public bool MustChangePassword { get; set; }

    public DateTimeOffset? PasswordChangedAtUtc { get; set; }
    public DateTimeOffset? LastLoginAtUtc { get; set; }

    // ---- MFA (TOTP, ТЗ §8) ----

    public bool MfaEnabled { get; set; }

    /// <summary>Секрет TOTP, зашифрованный <c>SecretProtector</c> (AES-GCM). В открытом виде в БД не хранится.</summary>
    public string? MfaSecretProtected { get; set; }

    /// <summary>Секрет на этапе настройки (до подтверждения первым кодом).</summary>
    public string? MfaPendingSecretProtected { get; set; }

    public DateTimeOffset? MfaPendingCreatedAtUtc { get; set; }

    /// <summary>Последний принятый шаг TOTP — повтор того же кода отклоняется.</summary>
    public long MfaLastUsedStep { get; set; }

    public DateTimeOffset? MfaEnabledAtUtc { get; set; }

    /// <summary>
    /// Доступ ко всем локациям организации (в т.ч. будущим). false — только из <see cref="StaffLocationAccess"/>.
    /// Owner всегда видит все локации независимо от флага.
    /// </summary>
    public bool AllLocations { get; set; } = true;
}

/// <summary>Локация, доступная сотруднику с ограниченным доступом (ТЗ §8, права по локациям).</summary>
public sealed class StaffLocationAccess
{
    public required string UserId { get; set; }
    public required string LocationId { get; set; }
}

/// <summary>Одноразовый код восстановления MFA; хранится SHA-256 хэш (код — 50 бит случайности).</summary>
public sealed class MfaRecoveryCode
{
    public required string Id { get; set; }
    public required string UserId { get; set; }
    public required string CodeHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? UsedAtUtc { get; set; }
}

/// <summary>
/// Второй шаг входа: выдаётся после верного пароля, если у сотрудника включена MFA. Действует 5 минут,
/// не больше 5 попыток ввода кода. Хранится хэш токена.
/// </summary>
public sealed class MfaChallenge
{
    public required string Id { get; set; }
    public required string UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
}

/// <summary>Refresh-токен с ротацией: хранится только SHA-256 хэш (ТЗ §27.3).</summary>
public sealed class RefreshToken
{
    public required string Id { get; set; }
    public required string UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? ReplacedById { get; set; }
}

/// <summary>Edge Controller клуба, зарегистрированный в Cloud (ТЗ §6.2, §25.2.3).</summary>
public sealed class Edge
{
    public required string Id { get; set; }
    public required string TenantId { get; set; }
    public required string LocationId { get; set; }
    public required string Name { get; set; }
    public required string CertificatePem { get; set; }
    public DateTimeOffset CertificateExpiresAtUtc { get; set; }
    public DateTimeOffset EnrolledAtUtc { get; set; }
    public DateTimeOffset? LastSeenAtUtc { get; set; }
    public int PendingOutboxEvents { get; set; }
    public DateTimeOffset? LastEdgeClockUtc { get; set; }

    /// <summary>Edge отключён: его запросы к Cloud отклоняются, для локации нужен новый enrollment (D-011).</summary>
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }
}

public static class EnrollmentKinds
{
    public const string Edge = "Edge";
    public const string Device = "Device";
}

/// <summary>Одноразовый enrollment-токен Edge или устройства (ТЗ §8 AUTH-007). Хранится хэш.</summary>
public sealed class EnrollmentToken
{
    public required string Id { get; set; }
    public required string TenantId { get; set; }
    public required string Kind { get; set; }
    public required string LocationId { get; set; }
    public string? ZoneId { get; set; }
    public required string DisplayName { get; set; }
    public bool Simulated { get; set; }
    public required string TokenHash { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? UsedAtUtc { get; set; }
    public string? UsedBySubjectId { get; set; }
}

public sealed class Device
{
    public required string Id { get; set; }
    public required string TenantId { get; set; }
    public required string LocationId { get; set; }
    public required string ZoneId { get; set; }
    public required string DisplayName { get; set; }
    public bool Simulated { get; set; }

    /// <summary>Последний статус, сообщённый Edge. Эффективный статус учитывает свежесть heartbeat.</summary>
    public DeviceStatus Status { get; set; } = DeviceStatus.Offline;
    public DateTimeOffset? LastHeartbeatUtc { get; set; }

    // Инвентаризация (ТЗ §9 LOC-007, §3.3).
    public string? Hostname { get; set; }
    public string? WindowsVersion { get; set; }
    public string? Cpu { get; set; }
    public int? RamMegabytes { get; set; }
    public string? Ipv4 { get; set; }
    public string? AgentVersion { get; set; }

    public required string CertificatePem { get; set; }
    public DateTimeOffset CertificateExpiresAtUtc { get; set; }
    public DateTimeOffset EnrolledAtUtc { get; set; }

    /// <summary>
    /// Устройство удалено (отозвано): не показывается, Edge его забывает, запросы агента отклоняются.
    /// Запись остаётся ради истории сессий и аудита (D-011).
    /// </summary>
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }
}

/// <summary>Команда устройству со своим жизненным циклом (ТЗ §10.2, §24.3).</summary>
public sealed class DeviceCommand
{
    public required string Id { get; set; }          // = commandId
    public required string TenantId { get; set; }
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
    public required string TenantId { get; set; }
    public required string DeviceId { get; set; }
    public required string LocationId { get; set; }
    public SessionState State { get; set; } = SessionState.Created;

    /// <summary>"cloud" — запрошена из Admin Web; "edge" — начата локально (offline/edge-cli).</summary>
    public required string Origin { get; set; }

    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? EndRequestedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }

    // Снимок тарифа (ТЗ §12.2).
    public long PricePerHourMinorUnits { get; set; }
    public required string Currency { get; set; }
    public RoundingRule Rounding { get; set; }
    public int RuleVersion { get; set; }

    public long? TotalMinorUnits { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>Запрошенный лимит (минуты); null — открытая сессия (постоплата).</summary>
    public int? DurationMinutes { get; set; }

    /// <summary>Плановое окончание — известно после старта на Edge, меняется продлениями (SessionExtended).</summary>
    public DateTimeOffset? PlannedEndAtUtc { get; set; }

    /// <summary>Причина завершения: staff / timeLimit (<see cref="SessionEndReasons"/>).</summary>
    public string? EndReason { get; set; }

    public required string StartedBy { get; set; }
    public string? EndedBy { get; set; }
    public required string CorrelationId { get; set; }
}

/// <summary>
/// Очередь Cloud → Edge (durable outbox облака). Edge забирает элементы long-poll'ом
/// и подтверждает получение; до подтверждения элемент выдаётся повторно (at-least-once),
/// Edge дедуплицирует по Id (inbox).
/// </summary>
public sealed class EdgeOutboxItem
{
    public required string Id { get; set; }
    public required string TenantId { get; set; }
    public required string LocationId { get; set; }
    public EdgeCommandKind Kind { get; set; }
    public required string PayloadJson { get; set; } // jsonb, сериализованный EdgeCommand
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? AckedAtUtc { get; set; }
}

/// <summary>Дедупликация повторно доставленных событий sync (ТЗ §4). PK = EventId.</summary>
public sealed class InboxReceipt
{
    public required string EventId { get; set; }
    public required string TenantId { get; set; }
    public required string LocationId { get; set; }
    public required string EdgeId { get; set; }
    public required string EventType { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
}

/// <summary>Иммутабельная запись аудита (ТЗ §27.4). Только INSERT.</summary>
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
    public string? DetailsJson { get; set; }         // безопасные детали, без секретов
}
