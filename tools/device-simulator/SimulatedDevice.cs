using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.Contracts;

namespace ClubOS.DeviceSimulator;

/// <summary>Команда, как её отдаёт Edge (зеркало EdgeEndpoints.AgentCommand).</summary>
public sealed record SimCommand(
    string CommandId,
    string DeviceId,
    CommandType CommandType,
    JsonElement Payload,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Один симулированный ПК (ТЗ §3.4). Шлёт heartbeat в Edge, случайно уходит offline/online,
/// исполняет команды «на бумаге» и рапортует результат. Всё помечено SIMULATED.
/// </summary>
public sealed class SimulatedDevice(string deviceId, HttpClient http, SimulatorOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DeviceInventory _inventory = new()
    {
        Hostname = $"SIMULATED-{deviceId}",
        WindowsVersion = "Windows 11 (SIMULATED)",
        Cpu = "Simulated CPU @ 3.0GHz",
        RamMegabytes = 16384,
        Ipv4 = "10.0.0." + deviceId[^2..].TrimStart('0'),
        AgentVersion = "sim-1.0.0",
    };

    private bool _online = true;
    private bool _inventorySent;

    public async Task RunAsync(CancellationToken ct)
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, options.HeartbeatSeconds));
        using var timer = new PeriodicTimer(period);

        do
        {
            try
            {
                MaybeToggleOnline();
                if (!_online)
                {
                    SimLog.Log($"{deviceId}: offline (heartbeat пропущен)");
                    continue;
                }

                await SendHeartbeatAsync(ct);
                await ProcessCommandsAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SimLog.Log($"{deviceId}: ошибка тика — {ex.Message}");
            }
        }
        while (await SafeWaitAsync(timer, ct));
    }

    private void MaybeToggleOnline()
    {
        if (Random.Shared.NextDouble() < options.FlipProbability)
        {
            _online = !_online;
            SimLog.Log($"{deviceId}: смена состояния → {(_online ? "online" : "offline")}");
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        var heartbeat = new HeartbeatMessage
        {
            DeviceId = deviceId,
            Status = DeviceStatus.Idle,
            ClockUtc = DateTimeOffset.UtcNow,
            Inventory = _inventorySent ? null : _inventory,
        };

        using var response = await http.PostAsJsonAsync("api/edge/agent/heartbeat", heartbeat, ct);
        if (response.IsSuccessStatusCode)
        {
            _inventorySent = true;
        }
    }

    private async Task ProcessCommandsAsync(CancellationToken ct)
    {
        List<SimCommand>? commands;
        try
        {
            commands = await http.GetFromJsonAsync<List<SimCommand>>($"api/edge/agent/{deviceId}/commands", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            return;
        }

        if (commands is null || commands.Count == 0)
        {
            return;
        }

        foreach (var command in commands)
        {
            await ReportAsync(command.CommandId, CommandState.Acknowledged, null, ct);
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            SimLog.Log($"{deviceId}: SIMULATED исполнил {command.CommandType} ({command.CommandId})");
            await ReportAsync(command.CommandId, CommandState.Succeeded, null, ct);
        }
    }

    private async Task ReportAsync(string commandId, CommandState state, string? error, CancellationToken ct)
    {
        var result = new CommandResult
        {
            CommandId = commandId,
            DeviceId = deviceId,
            State = state,
            ReportedAtUtc = DateTimeOffset.UtcNow,
            Error = error,
        };

        try
        {
            using var response = await http.PostAsJsonAsync(
                $"api/edge/agent/commands/{commandId}/result", result, ct);
            _ = response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            // Edge недоступен — при следующем цикле повторим (Edge идемпотентен).
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
