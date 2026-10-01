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

    /// <summary>Периоды цены (jsonb, список <see cref="PricePeriod"/>); null — одна цена.</summary>
    public string? PeriodsJson { get; set; }
}

/// <summary>
/// Пакет зоны: N минут за фиксированную цену («3 часа», «Ночь»). Сессия по пакету получает лимит N минут;
/// ранний конец — цена пакета, продление — по тарифу зоны. Неактивный пакет нельзя выбрать, история сохраняется.
/// </summary>
public sealed class TariffPackage
{
    public required string Id { get; set; }
    public required string TenantId { get; set; }
    public required string LocationId { get; set; }
    public required string ZoneId { get; set; }
    public required string Name { get; set; }
    public int DurationMinutes { get; set; }
    public long PriceMinorUnits { get; set; }

    /// <summary>Окно начала по местному времени (минуты от полуночи, через полночь — начало больше конца).</summary>
    public int? AvailableFromMinute { get; set; }
    public int? AvailableToMinute { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
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

    /// <summary>
    /// Бездисковый ПК (D-018): MAC загрузочной сетевой карты. Сертификат выдаёт локальный CA Edge при каждой загрузке,
    /// поэтому <see cref="CertificatePem"/> пуст. null — обычный ПК с регистрацией по токену.
    /// </summary>
    public string? HardwareId { get; set; }
}

/// <summary>
/// Бездисковый ПК, который загрузился в клубе, но ещё не подтверждён (D-018). Обновляется по отчётам Edge о статусе.
/// Подтверждение создаёт <see cref="Device"/> и удаляет запись; отклонение — удаляет (ПК появится снова при загрузке).
/// </summary>
public sealed class PendingDisklessDevice
{
    public required string Id { get; set; }
    public required string TenantId { get; set; }
    public required string LocationId { get; set; }
    public required string EdgeId { get; set; }
    public required string HardwareId { get; set; }
    public required string MacAddresses { get; set; }   // через запятую
    public required string Hostname { get; set; }
    public string? Ipv4 { get; set; }
    public bool Simulated { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
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

    /// <summary>Клиент, для которого начата сессия (оплата с его баланса по умолчанию).</summary>
    public string? ClientId { get; set; }

    // Снимок тарифа: периоды, смещение местного времени и пакет (ТЗ §12.4).
    public string? PeriodsJson { get; set; }
    public int UtcOffsetMinutes { get; set; }
    public string? PackageId { get; set; }
    public string? PackageName { get; set; }
    public int? PackageMinutes { get; set; }
    public long? PackagePriceMinorUnits { get; set; }
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

/// <summary>Способ оплаты.</summary>
public static class PaymentMethods
{
    public const string Cash = "Cash";
    public const string Card = "Card";

    /// <summary>Списание с баланса клиента (только оплата и возврат по сессии; деньги в кассу не поступают).</summary>
    public const string Balance = "Balance";

    /// <summary>Способ оплаты сессии или возврата.</summary>
    public static bool IsValid(string? method) => method is Cash or Card or Balance;

    /// <summary>Деньги, которые проходят через кассу: наличные или карта (пополнение баланса, внесение, изъятие).</summary>
    public static bool IsMoney(string? method) => method is Cash or Card;
}

/// <summary>Виды кассовых операций.</summary>
public static class CashOperationKinds
{
    /// <summary>Оплата сессии клиентом (сумма положительная).</summary>
    public const string SessionPayment = "SessionPayment";

    /// <summary>Возврат клиенту по сессии (сумма отрицательная) — отдельная связанная операция (ТЗ §12.2.1).</summary>
    public const string Refund = "Refund";

    /// <summary>Внесение наличных в кассу (размен).</summary>
    public const string CashIn = "CashIn";

    /// <summary>Изъятие наличных из кассы (инкассация, расходы).</summary>
    public const string CashOut = "CashOut";

    /// <summary>Пополнение баланса клиента наличными или картой: деньги в кассе, но это аванс, а не выручка.</summary>
    public const string BalanceTopUp = "BalanceTopUp";

    /// <summary>Продажа товаров бара (сумма чека, положительная) — выручка.</summary>
    public const string ProductSale = "ProductSale";

    /// <summary>Возврат чека бара (отрицательная) — отдельная операция.</summary>
    public const string ProductRefund = "ProductRefund";
}

/// <summary>
/// Кассовая смена локации. Одновременно открыта не больше одной смены на локацию (частичный уникальный индекс).
/// При закрытии фиксируются ожидаемая сумма наличных (остаток на начало + движения наличных) и пересчитанная.
/// Закрытая смена не меняется.
/// </summary>
public sealed class CashShift
{
    public required string Id { get; set; }
    public required string TenantId { get; set; }
    public required string LocationId { get; set; }
    public required string Currency { get; set; }

    public required string OpenedBy { get; set; }
    public DateTimeOffset OpenedAtUtc { get; set; }
    public long OpeningCashMinorUnits { get; set; }

    public string? ClosedBy { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }

    /// <summary>Наличные по учёту на момент закрытия.</summary>
    public long? ExpectedCashMinorUnits { get; set; }

    /// <summary>Наличные по факту пересчёта.</summary>
    public long? CountedCashMinorUnits { get; set; }

    public string? CloseNote { get; set; }
}

/// <summary>
/// Иммутабельная кассовая операция внутри смены. Сумма со знаком: плюс — деньги поступили
/// (оплата, внесение), минус — ушли (возврат, изъятие). Исправление — только новой операцией.
/// </summary>
public sealed class CashOperation
{
    public required string Id { get; set; }
    public required string TenantId { get; set; }
    public required string LocationId { get; set; }
    public required string ShiftId { get; set; }
    public required string Kind { get; set; }
    public required string Method { get; set; }
    public long AmountMinorUnits { get; set; }
    public required string Currency { get; set; }

    public string? SessionId { get; set; }
    public string? DeviceId { get; set; }
    public string? Reason { get; set; }

    /// <summary>Клиент: пополнение баланса или оплата/возврат через баланс.</summary>
    public string? ClientId { get; set; }

    /// <summary>Чек бара (продажа или её возврат).</summary>
    public string? SaleId { get; set; }

    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Ключ идемпотентности от клиента: повтор запроса не создаёт вторую операцию.</summary>
    public string? IdempotencyKey { get; set; }
}

/// <summary>
/// Клиент клуба: телефон (уникален в организации), имя и баланс (аванс). Баланс — сумма записей
/// <see cref="ClientLedgerEntry"/>; хранится и в строке клиента, обновляется в той же транзакции под блокировкой.
/// </summary>
public sealed class Client
{
    public required string Id { get; set; }
    public required string TenantId { get; set; }

    /// <summary>Только цифры с кодом страны: 992901234567.</summary>
    public required string Phone { get; set; }

    public required string DisplayName { get; set; }
    public required string Currency { get; set; }
    public long BalanceMinorUnits { get; set; }
    public bool IsBlocked { get; set; }
    public string? Note { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public static class ClientLedgerKinds
{
    /// <summary>Оплата чека бара с баланса.</summary>
    public const string ProductPayment = "ProductPayment";

    /// <summary>Возврат чека бара на баланс.</summary>
    public const string ProductRefund = "ProductRefund";

    /// <summary>Пополнение через кассу (наличные/карта).</summary>
    public const string TopUp = "TopUp";

    /// <summary>Оплата сессии с баланса.</summary>
    public const string SessionPayment = "SessionPayment";

    /// <summary>Возврат по сессии на баланс.</summary>
    public const string SessionRefund = "SessionRefund";

    /// <summary>Ручная корректировка администратором (бонус, исправление) — с причиной.</summary>
    public const string Adjustment = "Adjustment";
}

/// <summary>Иммутабельная запись движения по балансу клиента (UPDATE/DELETE запрещены триггером БД).</summary>
public sealed class ClientLedgerEntry
{
    public required string Id { get; set; }
    public required string TenantId { get; set; }
    public required string ClientId { get; set; }
    public required string Kind { get; set; }

    /// <summary>Со знаком: плюс — баланс вырос, минус — списание.</summary>
    public long AmountMinorUnits { get; set; }

    public long BalanceAfterMinorUnits { get; set; }
    public string? LocationId { get; set; }
    public string? SessionId { get; set; }
    public string? CashOperationId { get; set; }
    public string? Reason { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public static class BookingStatuses
{
    /// <summary>Ожидает гостя: держит ПК (ограничение БД — без пересечений по ПК).</summary>
    public const string Booked = "Booked";

    /// <summary>Гость пришёл: по брони начата сессия.</summary>
    public const string Started = "Started";

    public const string Cancelled = "Cancelled";

    /// <summary>Гость не пришёл до конца льготного времени.</summary>
    public const string NoShow = "NoShow";
}

/// <summary>
/// Бронь ПК на время (ТЗ: бронирования). Пока статус Booked, интервал не пересекается с другими бронями этого ПК
/// (exclusion constraint). За <see cref="Api.BookingEndpoints.HoldMinutes"/> минут до начала ПК держится для гостя.
/// </summary>
public sealed class Booking
{
    public required string Id { get; set; }
    public required string TenantId { get; set; }
    public required string LocationId { get; set; }
    public required string DeviceId { get; set; }
    public string? ClientId { get; set; }
    public required string GuestName { get; set; }
    public string? GuestPhone { get; set; }
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset EndsAtUtc { get; set; }
    public string Status { get; set; } = BookingStatuses.Booked;
    public string? Note { get; set; }
    public string? SessionId { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? ClosedBy { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
    public string? CancelReason { get; set; }
}

/// <summary>Товар бара локации (ТЗ: товары / POS). Цена — на момент продажи копируется в позицию чека.</summary>
public sealed class Product
{
    public required string Id { get; set; }
    public required string TenantId { get; set; }
    public required string LocationId { get; set; }
    public required string Name { get; set; }
    public string? Category { get; set; }
    public long PriceMinorUnits { get; set; }

    /// <summary>Вести остаток: продажа уменьшает его и не проходит при нехватке. Услуги (например, «Чай») — без учёта.</summary>
    public bool TrackStock { get; set; } = true;

    /// <summary>Остаток (штук) — сумма движений <see cref="StockMovement"/>, обновляется под блокировкой строки.</summary>
    public int StockQuantity { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public static class StockMovementKinds
{
    public const string Receipt = "Receipt";
    public const string Sale = "Sale";
    public const string Return = "Return";
    public const string WriteOff = "WriteOff";

    /// <summary>Инвентаризация: разница между пересчитанным и учётным остатком.</summary>
    public const string Count = "Count";
}

/// <summary>Движение склада (append-only, триггер БД). Количество со знаком, остаток после движения.</summary>
public sealed class StockMovement
{
    public required string Id { get; set; }
    public required string TenantId { get; set; }
    public required string LocationId { get; set; }
    public required string ProductId { get; set; }
    public required string Kind { get; set; }
    public int Quantity { get; set; }
    public int QuantityAfter { get; set; }
    public string? Reason { get; set; }
    public string? SaleId { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public static class SaleStatuses
{
    public const string Paid = "Paid";
    public const string Refunded = "Refunded";
}

/// <summary>Чек бара: позиции, способ оплаты, кассовая операция в смене. Возврат — только целиком (D-021).</summary>
public sealed class Sale
{
    public required string Id { get; set; }
    public required string TenantId { get; set; }
    public required string LocationId { get; set; }
    public required string ShiftId { get; set; }
    public required string CashOperationId { get; set; }
    public required string Method { get; set; }
    public string? ClientId { get; set; }
    public long TotalMinorUnits { get; set; }
    public required string Currency { get; set; }
    public string Status { get; set; } = SaleStatuses.Paid;
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? RefundOperationId { get; set; }
    public string? RefundedBy { get; set; }
    public DateTimeOffset? RefundedAtUtc { get; set; }
    public string? RefundReason { get; set; }
}

public sealed class SaleItem
{
    public required string Id { get; set; }
    public required string SaleId { get; set; }
    public required string ProductId { get; set; }

    /// <summary>Название и цена на момент продажи.</summary>
    public required string Name { get; set; }
    public long PriceMinorUnits { get; set; }
    public int Quantity { get; set; }
    public long TotalMinorUnits { get; set; }
}
