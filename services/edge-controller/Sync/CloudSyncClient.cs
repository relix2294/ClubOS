using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClubOS.Contracts;
using ClubOS.EdgeController.Config;
using Microsoft.Extensions.Options;

namespace ClubOS.EdgeController.Sync;

/// <summary>
/// Клиент исходящей связи Edge → Cloud (ТЗ §25.2.4). На M0 — HTTPS POST на Cloud
/// `/api/v1/sync/events` (D-010: HTTP вместо gRPC/WebSocket). Соединение всегда исходящее.
/// </summary>
public sealed class CloudSyncClient(HttpClient http, IOptions<EdgeOptions> options)
{
    private readonly EdgeOptions _options = options.Value;

    /// <summary>Отправляет батч событий. true — Cloud принял (2xx); false — доставка не удалась.</summary>
    public async Task<bool> SendAsync(IReadOnlyList<EventEnvelope> events, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/sync/events")
        {
            Content = JsonContent.Create(new { events }),
        };

        if (!string.IsNullOrWhiteSpace(_options.CloudToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.CloudToken);
        }

        using var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }
}
