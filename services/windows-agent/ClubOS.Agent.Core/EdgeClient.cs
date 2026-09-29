using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClubOS.Contracts;
using ClubOS.Security;

namespace ClubOS.Agent.Core;

/// <summary>Клиент Agent → Edge. Каждый запрос (кроме enroll) подписан ключом устройства.</summary>
public sealed class EdgeClient(HttpClient http, AgentIdentityStore identity, TimeProvider time)
{
    public async Task<DeviceEnrollResponse> EnrollAsync(DeviceEnrollRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("agent/v1/enroll", request, ContractJson.Options, ct);
        await EnsureSuccess(response, ct);
        return (await response.Content.ReadFromJsonAsync<DeviceEnrollResponse>(ContractJson.Options, ct))!;
    }

    public Task<HeartbeatAck> HeartbeatAsync(HeartbeatMessage heartbeat, CancellationToken ct) =>
        SendAsync<HeartbeatAck>(HttpMethod.Post, "agent/v1/heartbeat", heartbeat, ct);

    public Task<AgentCommandsResponse> GetCommandsAsync(int waitSeconds, CancellationToken ct) =>
        GetCommandsAsync(waitSeconds, null, ct);

    /// <summary>Long-poll; с <paramref name="sessionStamp"/> Edge отвечает сразу при изменении сессии устройства.</summary>
    public Task<AgentCommandsResponse> GetCommandsAsync(int waitSeconds, string? sessionStamp, CancellationToken ct) =>
        SendAsync<AgentCommandsResponse>(HttpMethod.Get,
            $"agent/v1/commands?waitSeconds={waitSeconds}" +
            (sessionStamp is null ? string.Empty : $"&sessionStamp={Uri.EscapeDataString(sessionStamp)}"), null, ct);

    public Task ReportResultAsync(string commandId, CommandState state, string? error, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, $"agent/v1/commands/{Uri.EscapeDataString(commandId)}/result",
            new AgentCommandResultRequest { State = state, Error = error }, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var id = identity.Current ?? throw new InvalidOperationException("Устройство не зарегистрировано.");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(SignedToken.Scheme,
            SignedToken.Create(id.DeviceId, identity.Key.Key, SignedToken.AudienceEdge, time));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: ContractJson.Options);
        }

        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return typeof(T) == typeof(object)
            ? default!
            : (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, ct))!;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new EdgeRequestException((int)response.StatusCode, body.Length > 300 ? body[..300] : body);
        }
    }
}

public sealed class EdgeRequestException(int status, string body) : Exception($"Edge вернул {status}: {body}")
{
    public int Status { get; } = status;
}
