using System.Text.Json.Serialization;

namespace ClubOS.Contracts;

/// <summary>Состояния устройства (ТЗ §9 LOC-006). В M0 используются online/offline/idle/active.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceStatus
{
    Offline,
    Idle,
    Locked,
    Active,
    Reserved,
    Maintenance,
    Updating,
    Error
}

/// <summary>
/// Безопасная инвентаризация, собираемая Windows Agent (ТЗ §3.3, §25.2.5).
/// Только несекретные поля. Никаких токенов/ключей.
/// </summary>
public sealed record DeviceInventory
{
    public required string Hostname { get; init; }
    public required string WindowsVersion { get; init; }
    public required string Cpu { get; init; }
    public required int RamMegabytes { get; init; }
    public required string Ipv4 { get; init; }
    public required string AgentVersion { get; init; }
}

/// <summary>
/// Heartbeat Agent → Edge (ТЗ §10.1). Каждые 10 сек. Без тяжёлых логов/скриншотов.
/// clockUtc фиксирует часы агента для оценки clock skew (ТЗ §4).
/// </summary>
public sealed record HeartbeatMessage
{
    public required string DeviceId { get; init; }
    public required DeviceStatus Status { get; init; }

    /// <summary>Часы устройства (UTC) на момент heartbeat — для оценки clock skew.</summary>
    public required DateTimeOffset ClockUtc { get; init; }

    public int SchemaVersion { get; init; } = SchemaVersions.Heartbeat;

    /// <summary>Инвентаризация; может присылаться реже, чем heartbeat.</summary>
    public DeviceInventory? Inventory { get; init; }
}
