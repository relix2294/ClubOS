using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClubOS.Contracts;
using ClubOS.EdgeController.Storage;
using ClubOS.Security;

namespace ClubOS.EdgeController.Cloud;

/// <summary>
/// Исходящий клиент Edge → Cloud. Каждый запрос подписывается свежим токеном (ключ Edge).
/// Состояние связи (<see cref="IsReachable"/>) — для health и edge-cli.
/// </summary>
public sealed class CloudClient(HttpClient http, EdgeIdentityStore identity, TimeProvider time)
{
    public const string HttpClientName = "cloud";

    private long _lastSuccessTicks;

    public bool IsReachable => Interlocked.Read(ref _lastSuccessTicks) is var t && t > 0 &&
                               time.GetUtcNow() - new DateTimeOffset(t, TimeSpan.Zero) < TimeSpan.FromSeconds(30);

    public DateTimeOffset? LastSuccessUtc => Interlocked.Read(ref _lastSuccessTicks) is var t && t > 0
        ? new DateTimeOffset(t, TimeSpan.Zero)
        : null;

    public async Task<EdgeEnrollResponse> EnrollAsync(EdgeEnrollRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/v1/edge/enroll", request, ContractJson.Options, ct);
        await EnsureSuccess(response, ct);
        return (await response.Content.ReadFromJsonAsync<EdgeEnrollResponse>(ContractJson.Options, ct))!;
    }

    public Task<EdgeConfigResponse> GetConfigAsync(CancellationToken ct) =>
        SendAsync<EdgeConfigResponse>(HttpMethod.Get, "api/v1/edge/config", null, ct);

    public Task<SyncBatchResponse> SyncAsync(SyncBatchRequest batch, CancellationToken ct) =>
        SendAsync<SyncBatchResponse>(HttpMethod.Post, "api/v1/edge/sync", batch, ct);

    public Task<EdgeCommandsResponse> PullCommandsAsync(int waitSeconds, CancellationToken ct) =>
        SendAsync<EdgeCommandsResponse>(HttpMethod.Get, $"api/v1/edge/commands?waitSeconds={waitSeconds}", null, ct);

    public Task AckCommandsAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, "api/v1/edge/commands/ack", new EdgeCommandsAck { Ids = ids }, ct);

    public Task ReportStatusAsync(EdgeStatusReport report, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, "api/v1/edge/status", report, ct);

    public Task<DeviceEnrollResponse> EnrollDeviceAsync(DeviceEnrollRequest request, CancellationToken ct) =>
        SendAsync<DeviceEnrollResponse>(HttpMethod.Post, "api/v1/edge/devices/enroll", request, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var current = identity.Current ?? throw new InvalidOperationException("Edge не зарегистрирован в Cloud.");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(SignedToken.Scheme,
            SignedToken.Create(current.EdgeId, identity.Key.Key, SignedToken.AudienceCloud, time));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: ContractJson.Options);
        }

        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        Interlocked.Exchange(ref _lastSuccessTicks, time.GetUtcNow().UtcTicks);

        if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object))
        {
            return default!;
        }

        return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, ct))!;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new CloudRequestException(response.StatusCode, body.Length > 300 ? body[..300] : body);
    }
}

public sealed class CloudRequestException(HttpStatusCode status, string body)
    : Exception($"Cloud вернул {(int)status}: {body}")
{
    public HttpStatusCode Status { get; } = status;
}
