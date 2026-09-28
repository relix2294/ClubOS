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
    bool MustChangePassword);

// ---- Персонал (ТЗ §8) ----

public sealed record StaffMemberView(
    string UserId,
    string Email,
    string DisplayName,
    string Role,
    bool IsActive,
    bool MustChangePassword,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastLoginAtUtc);

public sealed record CreateStaffRequest(string Email, string DisplayName, string Role);

/// <summary>Результат создания сотрудника или сброса пароля: временный пароль показывается один раз.</summary>
public sealed record TemporaryPasswordResponse(StaffMemberView User, string TemporaryPassword);

public sealed record ChangeRoleRequest(string Role);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record MeResponse(UserView User, IReadOnlyList<LocationView> Locations);

public sealed record LocationView(string LocationId, string Name, string Timezone, string Currency,
    IReadOnlyList<ZoneView> Zones, IReadOnlyList<EdgeView> Edges);

public sealed record ZoneView(string ZoneId, string Name, long PricePerHourMinorUnits);

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
    SessionView? ActiveSession);

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
    string? EndedBy);

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
