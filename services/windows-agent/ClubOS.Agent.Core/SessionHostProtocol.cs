using System.Text.Json.Serialization;

namespace ClubOS.Agent.Core;

/// <summary>
/// Протокол Service ↔ AgentSessionHost по Named Pipe (JSON, одна строка = одно сообщение).
/// Service (Session 0) не рисует UI — всё отображение делает SessionHost в сессии пользователя.
/// </summary>
public static class SessionHostProtocol
{
    public const string PipeName = "ClubOS.Agent.SessionHost.v1";

    public const string TypeShowMessage = "show";
    public const string TypeLock = "lock";
    public const string TypePing = "ping";

    /// <summary>Уведомление от SessionHost: пользователь снял overlay аварийной комбинацией.</summary>
    public const string TypeLocalUnlock = "localUnlock";
}

public sealed record HostRequest
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("lock")] public bool? Lock { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

public sealed record HostMessage
{
    /// <summary>ID запроса, на который это ответ; null — уведомление по инициативе SessionHost.</summary>
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}
