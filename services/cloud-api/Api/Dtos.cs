using System.Text.Json;
using ClubOS.Contracts;

namespace ClubOS.CloudApi.Api;

// DTO Admin Web ↔ Cloud API. Зеркало — packages/contracts-typescript/index.ts (править синхронно).

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LoginResponse(
    string AccessToken,
    DateTimeOffset ExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAtUtc,
    UserView User);

public sealed record UserView(
    string UserId,
    string Email,
    string DisplayName,
    string Role,
    string OrganizationId,
    string OrganizationName,
    IReadOnlyList<string> Permissions,
    bool MustChangePassword,
    bool MfaEnabled,
    bool MfaSetupRequired);

// ---- MFA (TOTP, ТЗ §8) ----

/// <summary>Ответ на верный пароль при включённой MFA: нужен второй шаг <c>/auth/mfa</c>.</summary>
public sealed record MfaChallengeResponse(bool MfaRequired, string MfaToken, DateTimeOffset ExpiresAtUtc);

public sealed record MfaLoginRequest(string MfaToken, string? Code, string? RecoveryCode);

public sealed record MfaStatusView(bool Enabled, bool Required, int RecoveryCodesLeft, DateTimeOffset? EnabledAtUtc);

public sealed record MfaSetupResponse(string Secret, string OtpAuthUri);

public sealed record MfaCodeRequest(string? Code);

public sealed record MfaEnableResponse(LoginResponse Session, IReadOnlyList<string> RecoveryCodes);

public sealed record MfaDisableRequest(string Password, string? Code, string? RecoveryCode);

public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

// ---- Персонал (ТЗ §8) ----

public sealed record StaffMemberView(
    string UserId,
    string Email,
    string DisplayName,
    string Role,
    bool IsActive,
    bool MustChangePassword,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastLoginAtUtc,
    bool MfaEnabled,
    bool AllLocations,
    IReadOnlyList<string> LocationIds);

/// <param name="LocationIds">null — доступ ко всем локациям; список — только к ним (кроме Owner).</param>
public sealed record CreateStaffRequest(string Email, string DisplayName, string Role, IReadOnlyList<string>? LocationIds = null);

/// <summary>Доступ сотрудника к локациям: все (в т.ч. будущие) или список.</summary>
public sealed record StaffLocationsRequest(bool AllLocations, IReadOnlyList<string>? LocationIds);

/// <summary>Результат создания сотрудника или сброса пароля: временный пароль показывается один раз.</summary>
public sealed record TemporaryPasswordResponse(StaffMemberView User, string TemporaryPassword);

public sealed record ChangeRoleRequest(string Role);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record MeResponse(UserView User, IReadOnlyList<LocationView> Locations);

public sealed record LocationView(string LocationId, string Name, string Timezone, string Currency,
    IReadOnlyList<ZoneView> Zones, IReadOnlyList<EdgeView> Edges);

public sealed record ZoneView(
    string ZoneId,
    string Name,
    long PricePerHourMinorUnits,
    IReadOnlyList<PricePeriod> Periods,
    IReadOnlyList<TariffPackageView> Packages);

public sealed record TariffPackageView(
    string PackageId,
    string ZoneId,
    string Name,
    int DurationMinutes,
    long PriceMinorUnits,
    int? AvailableFromMinute,
    int? AvailableToMinute,
    bool IsActive);

public sealed record TariffPackageInput(
    string? Name,
    int DurationMinutes,
    long PriceMinorUnits,
    int? AvailableFromMinute,
    int? AvailableToMinute,
    bool? IsActive);

public sealed record ZonePeriodsRequest(IReadOnlyList<PricePeriod>? Periods);

public sealed record EdgeView(string EdgeId, string Name, bool Online, DateTimeOffset? LastSeenAtUtc,
    int PendingOutboxEvents, DateTimeOffset EnrolledAtUtc, DateTimeOffset CertificateExpiresAtUtc);

public sealed record DeviceView(
    string DeviceId,
    string DisplayName,
    string LocationId,
    string ZoneId,
    string ZoneName,
    DeviceStatus Status,
    bool Simulated,
    DateTimeOffset? LastHeartbeatUtc,
    DeviceInventory? Inventory,
    DateTimeOffset EnrolledAtUtc,
    SessionView? ActiveSession,
    DateTimeOffset CertificateExpiresAtUtc,
    string? HardwareId = null);

public sealed record IssueCommandRequest(
    CommandType CommandType,
    string? Title,
    string? Message,
    bool? Lock,
    string? Reason,
    int? TtlSeconds,
    string? CommandId);

public sealed record CommandView(
    string CommandId,
    CommandType CommandType,
    string DeviceId,
    CommandState State,
    string IssuedBy,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? Error,
    JsonElement Payload);

public sealed record SessionView(
    string SessionId,
    string DeviceId,
    SessionState State,
    string Origin,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndRequestedAtUtc,
    DateTimeOffset? EndedAtUtc,
    long PricePerHourMinorUnits,
    string Currency,
    RoundingRule Rounding,
    long? TotalMinorUnits,
    string? FailureReason,
    string StartedBy,
    string? EndedBy,
    int? DurationMinutes,
    DateTimeOffset? PlannedEndAtUtc,
    string? EndReason,
    string? ClientId = null,
    IReadOnlyList<PricePeriod>? Periods = null,
    int UtcOffsetMinutes = 0,
    string? PackageName = null,
    int? PackageMinutes = null,
    long? PackagePriceMinorUnits = null);

/// <summary>Тело запроса старта; пустое тело — открытая сессия. ClientId — сессия клиента (оплата с его баланса).</summary>
/// <param name="PackageId">Пакет зоны ПК: лимит и цена из пакета, DurationMinutes не задаётся.</param>
public sealed record StartSessionRequestBody(int? DurationMinutes, string? ClientId = null, string? PackageId = null);

public sealed record ExtendSessionRequest(int Minutes);

public sealed record AuditEventView(
    string AuditId,
    DateTimeOffset OccurredAtUtc,
    string Actor,
    string ActorDisplay,
    string Action,
    string Target,
    string Result,
    string? CorrelationId,
    JsonElement? Details);

public sealed record EdgeEnrollmentTokenRequest(string LocationId, string Name);

// ---- Локации, зоны и тарифы (locations.manage) ----

public sealed record ZoneInput(string Name, long PricePerHourMinorUnits);

public sealed record CreateLocationRequest(string Name, string Timezone, string Currency, IReadOnlyList<ZoneInput> Zones);
