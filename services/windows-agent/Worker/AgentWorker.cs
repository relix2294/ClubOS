using System.Runtime.Versioning;
using ClubOS.Contracts;
using ClubOS.WindowsAgent.Config;
using ClubOS.WindowsAgent.Identity;
using ClubOS.WindowsAgent.Inventory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClubOS.WindowsAgent.Worker;

/// <summary>
/// Основной цикл агента (ТЗ §10.1): enrollment (однократно) → heartbeat каждые N сек →
/// опрос команд, исполнение и отчёт (Acknowledged → Succeeded/Failed). Инвентаризация
/// отправляется при первом успешном heartbeat.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentWorker(
    EnrollmentClient enrollment,
    EdgeApiClient edge,
    InventoryCollector inventory,
    CommandExecutor executor,
    IOptions<AgentOptions> options,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private readonly AgentOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, _options.HeartbeatSeconds));
        using var timer = new PeriodicTimer(period);

        DeviceIdentity? identity = null;
        var inventorySnapshot = inventory.Collect();
        var inventorySent = false;

        try
        {
            do
            {
                try
                {
                    identity ??= await enrollment.EnsureEnrolledAsync(stoppingToken);
                    if (identity is null)
                    {
                        continue; // ещё не enroll'ены — ждём следующего тика
                    }

                    var heartbeat = new HeartbeatMessage
                    {
                        DeviceId = identity.DeviceId,
                        Status = DeviceStatus.Idle,
                        ClockUtc = DateTimeOffset.UtcNow,
                        Inventory = inventorySent ? null : inventorySnapshot,
                    };

                    if (await edge.SendHeartbeatAsync(heartbeat, stoppingToken))
                    {
                        inventorySent = true;
                    }

                    await ProcessCommandsAsync(identity.DeviceId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Ошибка цикла агента");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка службы.
        }
    }

    private async Task ProcessCommandsAsync(string deviceId, CancellationToken ct)
    {
        var commands = await edge.GetCommandsAsync(deviceId, ct);
        foreach (var command in commands)
        {
            await edge.ReportResultAsync(new CommandResult
            {
                CommandId = command.CommandId,
                DeviceId = deviceId,
                State = CommandState.Acknowledged,
                ReportedAtUtc = DateTimeOffset.UtcNow,
            }, ct);

            var (ok, error) = await executor.ExecuteAsync(command.CommandType, command.Payload, ct);

            await edge.ReportResultAsync(new CommandResult
            {
                CommandId = command.CommandId,
                DeviceId = deviceId,
                State = ok ? CommandState.Succeeded : CommandState.Failed,
                ReportedAtUtc = DateTimeOffset.UtcNow,
                Error = error,
            }, ct);
        }
    }
}
