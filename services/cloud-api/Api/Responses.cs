using ClubOS.Contracts;

namespace ClubOS.CloudApi.Api;

/// <summary>Ответ карточки устройства (ТЗ §9). Только несекретные поля инвентаризации.</summary>
public sealed record DeviceResponse
{
    public required string Id { get; init; }
    public required string LocationId { get; init; }
    public required string ZoneId { get; init; }
    public required string DisplayName { get; init; }
    public required bool Simulated { get; init; }
    public required DeviceStatus Status { get; init; }
    public DateTimeOffset? LastHeartbeatUtc { get; init; }
    public string? Hostname { get; init; }
    public string? WindowsVersion { get; init; }
    public string? Cpu { get; init; }
    public int? RamMegabytes { get; init; }
    public string? Ipv4 { get; init; }
    public string? AgentVersion { get; init; }
}

/// <summary>Ответ жизненного цикла команды (ТЗ §10.2).</summary>
public sealed record CommandResponse
{
    public required string Id { get; init; }
    public required string DeviceId { get; init; }
    public required CommandType CommandType { get; init; }
    public required CommandState State { get; init; }
    public required string IssuedBy { get; init; }
    public required DateTimeOffset IssuedAtUtc { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public required string CorrelationId { get; init; }
    public string? Error { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>Запрос на постановку команды ShowMessage в очередь (ТЗ §25.2.5).</summary>
public sealed record IssueShowMessageRequest
{
    public required string Title { get; init; }
    public required string Message { get; init; }

    /// <summary>TTL команды в секундах; по умолчанию 120 (ТЗ §10.2 CMD-006).</summary>
    public int? TtlSeconds { get; init; }
}

/// <summary>Запись аудита для timeline (ТЗ §27.4).</summary>
public sealed record AuditResponse
{
    public required string Id { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required string Actor { get; init; }
    public required string Action { get; init; }
    public required string Target { get; init; }
    public required string Result { get; init; }
    public string? CorrelationId { get; init; }
}

/// <summary>Ответ приёма sync-батча (ТЗ §4): сколько принято и сколько дублей.</summary>
public sealed record SyncResult
{
    public required int Accepted { get; init; }
    public required int Duplicates { get; init; }
}
