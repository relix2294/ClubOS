using System.Text.Json.Serialization;
using ClubOS.Agent.Core.PlayerShell;

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

    /// <summary>Service → SessionHost: новое состояние Player Shell (<see cref="HostRequest.Shell"/>).</summary>
    public const string TypeShell = "shell";

    /// <summary>SessionHost → Service: техник ввёл PIN (<see cref="HostMessage.Pin"/>); ответ — <see cref="TypeMaintenanceResult"/>.</summary>
    public const string TypeMaintenanceRequest = "maintenanceRequest";

    /// <summary>Service → SessionHost: результат проверки PIN (Ok/Error), Id = Id запроса.</summary>
    public const string TypeMaintenanceResult = "maintenanceResult";

    /// <summary>SessionHost → Service: техник завершил обслуживание досрочно.</summary>
    public const string TypeMaintenanceEnd = "maintenanceEnd";

    /// <summary>Service → SessionHost: снимок экрана (удалённый доступ, D-022); ответ — JPEG в <see cref="HostMessage.Data"/>.</summary>
    public const string TypeScreenshot = "screenshot";

    /// <summary>Максимальная длина строки протокола: защита от «бесконечной» строки в pipe (вмещает снимок экрана).</summary>
    public const int MaxLineLength = 1024 * 1024;
}

/// <summary>Сообщение Service → SessionHost.</summary>
public sealed record HostRequest
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("lock")] public bool? Lock { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("shell")] public ShellState? Shell { get; init; }

    /// <summary>Для ответов службы на запросы SessionHost (maintenanceResult).</summary>
    [JsonPropertyName("ok")] public bool? Ok { get; init; }

    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>Сообщение SessionHost → Service: ответ на запрос или уведомление/запрос по инициативе SessionHost.</summary>
public sealed record HostMessage
{
    /// <summary>ID запроса, на который это ответ; для запросов SessionHost — собственный ID.</summary>
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("pin")] public string? Pin { get; init; }

    /// <summary>Снимок экрана: JPEG в base64 и его размер.</summary>
    [JsonPropertyName("data")] public string? Data { get; init; }
    [JsonPropertyName("width")] public int? Width { get; init; }
    [JsonPropertyName("height")] public int? Height { get; init; }
}
