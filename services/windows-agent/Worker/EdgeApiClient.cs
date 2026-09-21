using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.Contracts;

namespace ClubOS.WindowsAgent.Worker;

/// <summary>Команда, как её отдаёт Edge агенту (зеркало EdgeEndpoints.AgentCommand).</summary>
public sealed record AgentCommandDto(
    string CommandId,
    string DeviceId,
    CommandType CommandType,
    JsonElement Payload,
    DateTimeOffset ExpiresAtUtc);

/// <summary>Клиент локального Edge API (ТЗ §10.1): heartbeat, получение команд, отчёт о результате.</summary>
public sealed class EdgeApiClient(HttpClient http)
{
    public async Task<bool> SendHeartbeatAsync(HeartbeatMessage heartbeat, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/edge/agent/heartbeat", heartbeat, ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<AgentCommandDto>> GetCommandsAsync(string deviceId, CancellationToken ct)
    {
        try
        {
            var commands = await http.GetFromJsonAsync<List<AgentCommandDto>>(
                $"api/edge/agent/{deviceId}/commands", ct);
            return commands ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    public async Task ReportResultAsync(CommandResult result, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                $"api/edge/agent/commands/{result.CommandId}/result", result, ct);
            _ = response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            // Результат уйдёт при следующем цикле — Edge идемпотентен по состоянию команды.
        }
    }
}
