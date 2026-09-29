namespace ClubOS.Contracts;

// Протокол Windows Agent ↔ Edge (ТЗ §10, §11). Agent всегда говорит только с Edge.

/// <summary>
/// Команды для конкретного устройства (long-poll). Повторная выдача возможна — Agent дедуплицирует.
/// Вместе с командами Edge отдаёт текущее состояние сессии устройства для Player Shell: long-poll
/// завершается досрочно, если состояние изменилось относительно <c>sessionStamp</c> агента.
/// </summary>
public sealed record AgentCommandsResponse
{
    public required IReadOnlyList<CommandEnvelope> Commands { get; init; }

    /// <summary>Состояние устройства на Edge; null — старый Edge (M0), Player Shell не управляется.</summary>
    public AgentDeviceState? State { get; init; }
}

public sealed record HeartbeatAck
{
    public required DateTimeOffset ServerTimeUtc { get; init; }

    public AgentDeviceState? State { get; init; }
}

/// <summary>Отчёт агента о команде: Acknowledged / Succeeded / Failed.</summary>
public sealed record AgentCommandResultRequest
{
    public required CommandState State { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Что Edge знает об устройстве: название клуба и ПК, активная сессия, итог последней завершённой.
/// Edge — источник истины (ТЗ §23.3); агент только отображает и не считает деньги сам.
/// </summary>
public sealed record AgentDeviceState
{
    /// <summary>Часы Edge — агент считает таймер по ним (защита от сбитых часов ПК).</summary>
    public required DateTimeOffset ServerTimeUtc { get; init; }

    /// <summary>Отпечаток состояния сессии: меняется при старте, продлении и завершении.</summary>
    public required string Stamp { get; init; }

    public required string DeviceName { get; init; }
    public string? LocationName { get; init; }

    public AgentSessionInfo? Session { get; init; }

    /// <summary>Последняя сессия, завершённая недавно (для экрана «Сессия завершена»).</summary>
    public AgentEndedSessionInfo? LastEnded { get; init; }

    public const string NoSessionStamp = "none";

    public static string StampFor(string? sessionId, DateTimeOffset? plannedEndAtUtc) =>
        sessionId is null ? NoSessionStamp : $"{sessionId}:{plannedEndAtUtc?.ToUnixTimeMilliseconds() ?? 0}";
}

public sealed record AgentSessionInfo
{
    public required string SessionId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? PlannedEndAtUtc { get; init; }
    public required PriceSnapshot PriceSnapshot { get; init; }
}

public sealed record AgentEndedSessionInfo
{
    public required string SessionId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset EndedAtUtc { get; init; }
    public required long TotalMinorUnits { get; init; }
    public required string Currency { get; init; }
    public string? Reason { get; init; }
}
