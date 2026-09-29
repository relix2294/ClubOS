using ClubOS.Agent.Core.PlayerShell;
using ClubOS.Contracts;
using Microsoft.Extensions.Logging;

namespace ClubOS.Agent.Core;

/// <summary>
/// Главный цикл агента: enrollment (если нужно) → параллельно heartbeat каждые N сек,
/// long-poll команд и Player Shell. Сбои связи с Edge не роняют агента: повтор с backoff.
/// </summary>
public sealed class AgentRuntime(
    AgentOptions options,
    AgentIdentityStore identity,
    EdgeClient edge,
    IInventoryProvider inventory,
    IUserPresenter presenter,
    CommandExecutor executor,
    PlayerShellController shell,
    TimeProvider time,
    ILogger<AgentRuntime> logger)
{
    private int _heartbeats;

    public string? DeviceId => identity.Current?.DeviceId;

    /// <summary>Симулятор может «выключить» устройство — heartbeat и команды приостанавливаются.</summary>
    public bool Paused { get; set; }

    public PlayerShellController Shell => shell;

    public async Task RunAsync(CancellationToken ct)
    {
        // Player Shell стартует до enrollment: незарегистрированный ПК в режиме Enforced тоже закрыт экраном клуба.
        var shellLoop = shell.RunAsync(ct);
        await EnsureEnrolledAsync(ct);
        logger.LogInformation("Агент {DeviceId} ({Name}) запущен, Edge: {EdgeUrl}", identity.Current!.DeviceId,
            identity.Current.DisplayName, options.EdgeUrl);
        await Task.WhenAll(HeartbeatLoop(ct), CommandLoop(ct), shellLoop);
    }

    private DeviceStatus CurrentStatus() =>
        presenter.IsLocked ? DeviceStatus.Locked : shell.InMaintenance ? DeviceStatus.Maintenance : DeviceStatus.Idle;

    public async Task EnsureEnrolledAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (identity.Current is null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(options.EnrollmentToken))
            {
                logger.LogError("Устройство не зарегистрировано: задайте одноразовый Agent:EnrollmentToken.");
                await Task.Delay(TimeSpan.FromSeconds(30), time, ct);
                continue;
            }

            try
            {
                var key = identity.GetOrCreatePendingKey();
                var response = await edge.EnrollAsync(new DeviceEnrollRequest
                {
                    EnrollmentToken = options.EnrollmentToken.Trim(),
                    Inventory = inventory.Collect(),
                    CertificateSigningRequestPem = key.CreateSigningRequestPem("clubos-device")
                }, ct);
                identity.Save(new AgentIdentity
                {
                    DeviceId = response.DeviceId,
                    DisplayName = response.DisplayName,
                    CertificatePem = response.DeviceCertificatePem,
                    CertificateExpiresAtUtc = response.CertificateExpiresAtUtc
                });
                logger.LogInformation("Устройство зарегистрировано: {DeviceId}", response.DeviceId);
            }
            catch (EdgeRequestException ex) when (ex.Status is 401 or 403)
            {
                logger.LogError("Enrollment отклонён: токен недействителен, истёк или уже использован. Нужен новый токен.");
                await Task.Delay(TimeSpan.FromMinutes(1), time, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Enrollment не удался ({Error}), повтор через {Delay} с", ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, time, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, options.MaxBackoffSeconds));
            }
        }
    }

    private async Task HeartbeatLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!Paused)
            {
                try
                {
                    // Инвентаризация — с первым heartbeat и затем раз в ~5 минут.
                    var withInventory = _heartbeats++ % 30 == 0;
                    var ack = await edge.HeartbeatAsync(new HeartbeatMessage
                    {
                        DeviceId = identity.Current!.DeviceId,
                        Status = CurrentStatus(),
                        ClockUtc = time.GetUtcNow(),
                        Inventory = withInventory ? inventory.Collect() : null
                    }, ct);
                    if (ack.State is { } state)
                    {
                        shell.Apply(state);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _heartbeats = 0; // после восстановления связи сразу отправить инвентаризацию
                    logger.LogWarning("Heartbeat не доставлен: {Error}", ex.Message);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.HeartbeatSeconds), time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CommandLoop(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            if (Paused)
            {
                await SafeDelay(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            try
            {
                var response = await edge.GetCommandsAsync(options.CommandPollSeconds, shell.KnownStamp, ct);
                if (response.State is { } state)
                {
                    shell.Apply(state);
                    await shell.TickAsync(ct); // старт/окончание сессии отражается на экране без ожидания тика
                }

                foreach (var command in response.Commands)
                {
                    await executor.ExecuteAsync(identity.Current!.DeviceId, command,
                        (state, error) => ReportWithRetry(command.CommandId, state, error, ct), ct);
                }

                delay = TimeSpan.FromSeconds(1);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Получение команд не удалось: {Error}", ex.Message);
                await SafeDelay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, options.MaxBackoffSeconds));
            }
        }
    }

    private async Task ReportWithRetry(string commandId, CommandState state, string? error, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await edge.ReportResultAsync(commandId, state, error, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < 5)
            {
                logger.LogWarning("Отчёт по команде {CommandId} не доставлен (попытка {Attempt}): {Error}", commandId,
                    attempt, ex.Message);
                await SafeDelay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }
    }

    private async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, time, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
