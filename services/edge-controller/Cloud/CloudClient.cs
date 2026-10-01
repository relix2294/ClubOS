using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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

    /// <summary>
    /// Long-poll очереди Cloud. Элементы разбираются по одному: элемент, который эта версия Edge не понимает
    /// (Cloud новее Edge), не блокирует остальные — он не подтверждается и истечёт в очереди Cloud сам.
    /// </summary>
    public async Task<PulledCommands> PullCommandsAsync(int waitSeconds, CancellationToken ct)
    {
        var raw = await SendAsync<RawCommandsResponse>(HttpMethod.Get, $"api/v1/edge/commands?waitSeconds={waitSeconds}",
            null, ct);
        return PulledCommands.Parse(raw.Commands);
    }

    private sealed record RawCommandsResponse(IReadOnlyList<JsonElement> Commands);

    public Task AckCommandsAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, "api/v1/edge/commands/ack", new EdgeCommandsAck { Ids = ids }, ct);

    public Task ReportStatusAsync(EdgeStatusReport report, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, "api/v1/edge/status", report, ct);

    public Task<CertificateRenewResponse> IssueServerCertificateAsync(EdgeServerCertificateRequest request,
        CancellationToken ct) =>
        SendAsync<CertificateRenewResponse>(HttpMethod.Post, "api/v1/edge/server-certificate", request, ct);

    public Task<CertificateRenewResponse> RenewEdgeAsync(CertificateRenewRequest request, CancellationToken ct) =>
        SendAsync<CertificateRenewResponse>(HttpMethod.Post, "api/v1/edge/renew", request, ct);

    public Task<CertificateRenewResponse> RenewDeviceAsync(string deviceId, CertificateRenewRequest request, CancellationToken ct) =>
        SendAsync<CertificateRenewResponse>(HttpMethod.Post, $"api/v1/edge/devices/{Uri.EscapeDataString(deviceId)}/renew",
            request, ct);

    public Task<DeviceEnrollResponse> EnrollDeviceAsync(DeviceEnrollRequest request, CancellationToken ct) =>
        SendAsync<DeviceEnrollResponse>(HttpMethod.Post, "api/v1/edge/devices/enroll", request, ct);

    /// <summary>Список отзыва CA (D-002): анонимный адрес, целостность — подпись CA (проверяет EdgeCrlStore).</summary>
    public async Task<byte[]> GetCrlAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("api/v1/pki/crl", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        if (response.Content.Headers.ContentLength > MaxCrlBytes)
        {
            throw new InvalidOperationException("CRL слишком большой.");
        }

        var der = await response.Content.ReadAsByteArrayAsync(ct);
        return der.Length > MaxCrlBytes ? throw new InvalidOperationException("CRL слишком большой.") : der;
    }

    private const int MaxCrlBytes = 16 * 1024 * 1024;

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var current = identity.Current ?? throw new InvalidOperationException("Edge не зарегистрирован в Cloud.");
        using var request = SignedRequest.Create(method, http.BaseAddress, path,
            body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), ContractJson.Options),
            current.EdgeId, identity.Key.Key, SignedToken.AudienceCloud, time);

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
