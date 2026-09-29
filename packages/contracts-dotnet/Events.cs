namespace ClubOS.Contracts;

/// <summary>Имена типов событий (ТЗ §24.4). Меняются только вместе с SchemaVersions.Event.</summary>
public static class EventTypes
{
    public const string SessionStarted = "SessionStarted";
    public const string SessionStartRejected = "SessionStartRejected";
    public const string SessionEnded = "SessionEnded";
    public const string SessionExtended = "SessionExtended";
    public const string CommandStateChanged = "CommandStateChanged";
    public const string DeviceConnectivityChanged = "DeviceConnectivityChanged";
}

public sealed record SessionStartedPayload
{
    public required string SessionId { get; init; }
    public required string DeviceId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required PriceSnapshot PriceSnapshot { get; init; }
    public required string Actor { get; init; }

    /// <summary>"cloud" — запрошено из Admin Web; "edge" — локально (edge-cli, offline).</summary>
    public required string Origin { get; init; }

    /// <summary>Плановое окончание для сессии с лимитом; null — открытая сессия.</summary>
    public DateTimeOffset? PlannedEndAtUtc { get; init; }
}

/// <summary>Лимит сессии продлён; <see cref="PlannedEndAtUtc"/> — новое плановое окончание.</summary>
public sealed record SessionExtendedPayload
{
    public required string SessionId { get; init; }
    public required string DeviceId { get; init; }
    public required int AddedMinutes { get; init; }
    public required DateTimeOffset PlannedEndAtUtc { get; init; }
    public required string Actor { get; init; }
}

public sealed record SessionStartRejectedPayload
{
    public required string SessionId { get; init; }
    public required string DeviceId { get; init; }
    public required string Reason { get; init; }
}

public sealed record SessionEndedPayload
{
    public required string SessionId { get; init; }
    public required string DeviceId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset EndedAtUtc { get; init; }
    public required PriceSnapshot PriceSnapshot { get; init; }
    public required long TotalMinorUnits { get; init; }
    public required string Actor { get; init; }
    public required string Origin { get; init; }

    /// <summary>Причина: <see cref="SessionEndReasons"/>.</summary>
    public string? Reason { get; init; }
}

public static class SessionEndReasons
{
    /// <summary>Завершена сотрудником (Admin Web / edge-cli).</summary>
    public const string Staff = "staff";

    /// <summary>Истёк оплаченный лимит времени — завершил таймер Edge.</summary>
    public const string TimeLimit = "timeLimit";

    /// <summary>Актор таймера Edge в аудите.</summary>
    public const string TimerActor = "system:edge-timer";
}

public sealed record CommandStateChangedPayload
{
    public required string CommandId { get; init; }
    public required string DeviceId { get; init; }
    public required CommandState State { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
    public string? Error { get; init; }
}

public sealed record DeviceConnectivityChangedPayload
{
    public required string DeviceId { get; init; }
    public required bool Online { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
}
