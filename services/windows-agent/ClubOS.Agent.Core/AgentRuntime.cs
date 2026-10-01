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
    ILogger<AgentRuntime> logger,
    IHardwareIdentity? hardware = null)
{
    /// <summary>Столько 401 подряд — бездисковый ПК заново получает сертификат у Edge (Edge переустановлен или ПК удалён).</summary>
    public const int DisklessRebootAfterUnauthorized = 3;

    private readonly IHardwareIdentity _hardware = hardware ?? new NetworkHardwareIdentity(options);
    private int _heartbeats;
    private int _unauthorized;

    public string? DeviceId => identity.Current?.DeviceId;

    /// <summary>Симулятор может «выключить» устройство — heartbeat и команды приостанавливаются.</summary>
    public bool Paused { get; set; }

    public PlayerShellController Shell => shell;

    public async Task RunAsync(CancellationToken ct)
    {
        // Player Shell стартует до enrollment: незарегистрированный ПК в режиме Enforced тоже закрыт экраном клуба.
        var shellLoop = shell.RunAsync(ct);
        if (options.Diskless)
        {
            // Бездисковый ПК: identity из общего образа или прошлой загрузки не используется — новый ключ каждую загрузку.
            identity.Reset();
        }

        await EnsureEdgeTrustAsync(ct);
        if (options.Diskless)
        {
            await EnsureDisklessBootAsync(rotateKey: false, ct);
        }
        else
        {
            await EnsureEnrolledAsync(ct);
        }

        logger.LogInformation("Агент {DeviceId} ({Name}) запущен, Edge: {EdgeUrl}", identity.Current!.DeviceId,
            identity.Current.DisplayName, options.EdgeUrl);
        // Сертификат бездискового ПК обновляется при каждой загрузке — продление через Cloud ему не нужно.
        await Task.WhenAll(HeartbeatLoop(ct), CommandLoop(ct), options.Diskless ? Task.CompletedTask : RenewalLoop(ct), shellLoop);
    }

    /// <summary>
    /// Продление сертификата устройства через Edge за <see cref="AgentOptions.CertificateRenewBeforeDays"/> дней
    /// до истечения, с новым ключом (D-011): CSR на новый ключ, запрос подписан текущим. Edge принимает прежний
    /// ключ, пока агент не подпишет запрос новым, — потеря ответа не отрезает ПК. true — продлён.
    /// </summary>
    public async Task<bool> RenewCertificateIfDueAsync(CancellationToken ct)
    {
        var current = identity.Current;
        if (current is null ||
            current.CertificateExpiresAtUtc - time.GetUtcNow() > TimeSpan.FromDays(options.CertificateRenewBeforeDays))
        {
            return false;
        }

        var next = identity.CreateNextKey();
        var response = await edge.RenewAsync(new CertificateRenewRequest
        {
            CertificateSigningRequestPem = next.CreateSigningRequestPem("clubos-device")
        }, ct);
        if (!next.Matches(response.CertificatePem))
        {
            throw new InvalidOperationException("Edge вернул сертификат не на новый ключ.");
        }

        identity.CommitNextKey(next, current with
        {
            CertificatePem = response.CertificatePem,
            CertificateExpiresAtUtc = response.CertificateExpiresAtUtc
        });
        logger.LogInformation("Сертификат устройства продлён до {ExpiresAt} с новым ключом", response.CertificateExpiresAtUtc);
        return true;
    }

    private async Task RenewalLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!Paused)
            {
                try
                {
                    await RenewCertificateIfDueAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Продление сертификата не удалось, повтор позже: {Error}", ex.Message);
                }
            }

            await SafeDelay(TimeSpan.FromMinutes(Math.Max(1, options.CertificateCheckMinutes)), ct);
        }
    }

    private DeviceStatus CurrentStatus() =>
        presenter.IsLocked ? DeviceStatus.Locked : shell.InMaintenance ? DeviceStatus.Maintenance : DeviceStatus.Idle;

    /// <summary>
    /// HTTPS к Edge без закреплённого CA (первая регистрация или агент M0 после перехода на HTTPS): CA берётся
    /// у Edge и сверяется с <see cref="AgentOptions.EdgeCaFingerprint"/> (D-007).
    /// </summary>
    public async Task EnsureEdgeTrustAsync(CancellationToken ct)
    {
        var url = new Uri(options.EdgeUrl.TrimEnd('/') + "/");
        var delay = TimeSpan.FromSeconds(2);
        while (url.Scheme == Uri.UriSchemeHttps && identity.TrustedCaPem is null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(options.EdgeCaFingerprint))
            {
                logger.LogError("EdgeUrl по HTTPS, но CA не закреплён: задайте Agent:EdgeCaFingerprint (Admin Web → Подключение).");
                await SafeDelay(TimeSpan.FromSeconds(30), ct);
                continue;
            }

            try
            {
                var ca = await EdgeTls.FetchTrustedCaAsync(url, options.EdgeCaFingerprint, ct);
                if (identity.Current is { } current)
                {
                    identity.Save(current with { CaCertificatePem = ca });
                }
                else
                {
                    identity.BootstrapCaPem = ca;
                }

                logger.LogInformation("CA Edge проверен по отпечатку и закреплён");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Проверка CA Edge не удалась, повтор через {Delay} с: {Error}", delay.TotalSeconds, ex.Message);
                await SafeDelay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, options.MaxBackoffSeconds));
            }
        }
    }

    /// <summary>
    /// Бездисковый ПК (D-018): сертификат у Edge по MAC. Пока ПК не подтверждён в Admin Web, на экране клуба
    /// видна подсказка с MAC, агент повторяет запрос. <paramref name="rotateKey"/> — новый ключ (повторная загрузка
    /// без перезапуска: Edge перестал принимать прежний).
    /// </summary>
    public async Task EnsureDisklessBootAsync(bool rotateKey, CancellationToken ct)
    {
        var key = rotateKey ? identity.RotateKey() : identity.GetOrCreatePendingKey();
        var delay = TimeSpan.FromSeconds(2);
        string? lastNotice = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var hw = _hardware.Collect();
                var response = await edge.DisklessBootAsync(new DisklessBootRequest
                {
                    HardwareId = hw.HardwareId,
                    MacAddresses = hw.MacAddresses,
                    Inventory = inventory.Collect(),
                    CertificateSigningRequestPem = key.CreateSigningRequestPem("clubos-device"),
                    Simulated = options.SimulatedDevice
                }, ct);
                if (response is { Status: DisklessBootStatus.Approved, Enrollment: { } enrolled })
                {
                    identity.Save(new AgentIdentity
                    {
                        DeviceId = enrolled.DeviceId,
                        DisplayName = enrolled.DisplayName,
                        CertificatePem = enrolled.DeviceCertificatePem,
                        CertificateExpiresAtUtc = enrolled.CertificateExpiresAtUtc,
                        CaCertificatePem = enrolled.CaCertificatePem ?? identity.TrustedCaPem
                    });
                    shell.SetNotice(null);
                    logger.LogInformation("Бездисковый ПК подтверждён: {DeviceId} ({Name}), MAC {Mac}", enrolled.DeviceId,
                        enrolled.DisplayName, HardwareIds.FormatMac(hw.HardwareId));
                    return;
                }

                var notice = response.Message ?? $"Ожидание Edge (MAC {HardwareIds.FormatMac(hw.HardwareId)})";
                if (notice != lastNotice)
                {
                    logger.LogWarning("{Notice}", notice);
                    lastNotice = notice;
                }

                shell.SetNotice(notice);
                delay = TimeSpan.FromSeconds(2);
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(response.RetryAfterSeconds, 3, 60)), time, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Загрузка бездискового ПК не удалась ({Error}), повтор через {Delay} с", ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, time, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, options.MaxBackoffSeconds));
            }
        }
    }

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
                    CertificateExpiresAtUtc = response.CertificateExpiresAtUtc,
                    CaCertificatePem = response.CaCertificatePem ?? identity.BootstrapCaPem
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

                    _unauthorized = 0;
                }
                catch (EdgeRequestException ex) when (ex.Status == 401 && options.Diskless &&
                                                      ++_unauthorized >= DisklessRebootAfterUnauthorized)
                {
                    // Edge не узнаёт ключ бездискового ПК (Edge переустановлен, ПК удалён или загрузился «двойник»):
                    // получить сертификат заново; удалённый ПК снова окажется в «Ожидают подтверждения».
                    _heartbeats = 0;
                    _unauthorized = 0;
                    logger.LogWarning("Edge не принимает ключ бездискового ПК — повторная загрузка");
                    await EnsureDisklessBootAsync(rotateKey: true, ct);
                }
                catch (EdgeRequestException ex) when (ex.Status == 401)
                {
                    _heartbeats = 0;
                    // Не стираем identity: 401 бывает и при сбитых часах ПК. Сообщение — для техника в журнале.
                    logger.LogError("Edge не принимает это устройство (401): оно удалено в Admin Web, Edge переустановлен " +
                                    "или часы ПК сильно расходятся с Edge. Для повторной регистрации: install-agent.ps1 -ReEnroll.");
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
                        (state, error, output) => ReportWithRetry(command.CommandId, state, error, output, ct), ct);
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

    private async Task ReportWithRetry(string commandId, CommandState state, string? error, System.Text.Json.JsonElement? output,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await edge.ReportResultAsync(commandId, state, error, output, ct);
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
