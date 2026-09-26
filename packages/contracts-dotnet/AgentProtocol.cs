namespace ClubOS.Contracts;

// Протокол Windows Agent ↔ Edge (ТЗ §10, §11). Agent всегда говорит только с Edge.

/// <summary>Команды для конкретного устройства (long-poll). Повторная выдача возможна — Agent дедуплицирует.</summary>
public sealed record AgentCommandsResponse
{
    public required IReadOnlyList<CommandEnvelope> Commands { get; init; }
}

public sealed record HeartbeatAck
{
    public required DateTimeOffset ServerTimeUtc { get; init; }
}

/// <summary>Отчёт агента о команде: Acknowledged / Succeeded / Failed.</summary>
public sealed record AgentCommandResultRequest
{
    public required CommandState State { get; init; }
    public string? Error { get; init; }
}
