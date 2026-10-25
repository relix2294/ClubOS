using System.Text.Json;
using ClubOS.Contracts;
using Microsoft.Extensions.Logging;

namespace ClubOS.Agent.Core;

/// <summary>
/// Исполнение команд на устройстве. Только allow-list (сообщение, блокировка, удалённый доступ D-022) — произвольных
/// команд нет (ТЗ §10.2 CMD-004). Проверки: адресат, срок, повтор (CMD-002, CMD-006).
/// </summary>
public sealed class CommandExecutor(
    IUserPresenter presenter,
    ExecutedCommandStore executed,
    TimeProvider time,
    ILogger<CommandExecutor> logger,
    IRemoteActions? remote = null)
{
    public const int MaxTitleLength = 80;
    public const int MaxMessageLength = 500;

    /// <summary>
    /// Возвращает последовательность отчётов для Edge: Acknowledged, затем Succeeded/Failed.
    /// Для повторно доставленной команды — только сохранённый итог, без повторного исполнения.
    /// </summary>
    public Task ExecuteAsync(string deviceId, CommandEnvelope command, Func<CommandState, string?, Task> report, CancellationToken ct) =>
        ExecuteAsync(deviceId, command, (state, error, _) => report(state, error), ct);

    /// <param name="report">Состояние, ошибка и данные результата (снимок, процессы) — только с Succeeded.</param>
    public async Task ExecuteAsync(string deviceId, CommandEnvelope command,
        Func<CommandState, string?, JsonElement?, Task> report, CancellationToken ct)
    {
        var previous = executed.Get(command.CommandId);
        if (previous is not null)
        {
            logger.LogInformation("Команда {CommandId} уже исполнялась — повторно сообщаем итог {State}",
                command.CommandId, previous.State);
            if (previous.State is CommandState.Succeeded or CommandState.Failed)
            {
                await report(previous.State, previous.Error, null);
            }

            return;
        }

        if (!command.TargetDeviceIds.Contains(deviceId))
        {
            await Finish(command, CommandState.Failed, "Команда адресована другому устройству.", report, begin: true);
            return;
        }

        if (command.ExpiresAtUtc <= time.GetUtcNow())
        {
            await Finish(command, CommandState.Failed, "Команда просрочена (expiresAtUtc) — не исполнена.", report, begin: true);
            return;
        }

        if (!executed.TryBegin(command.CommandId, time.GetUtcNow()))
        {
            return; // параллельная доставка той же команды
        }

        await report(CommandState.Acknowledged, null, null);

        RemoteResult result;
        try
        {
            result = command.CommandType switch
            {
                CommandType.ShowMessage => ToRemote(await ShowMessage(command, ct)),
                CommandType.LockTestMode => ToRemote(await Lock(command, ct)),
                CommandType.Screenshot or CommandType.ListProcesses or CommandType.KillProcess or CommandType.Reboot
                    or CommandType.Shutdown when remote is null => RemoteResult.Fail("Удалённый доступ на этом ПК недоступен."),
                CommandType.Screenshot => await remote!.ScreenshotAsync(ct),
                CommandType.ListProcesses => remote!.ListProcesses(),
                CommandType.KillProcess => Kill(command),
                CommandType.Reboot => Power(command, reboot: true),
                CommandType.Shutdown => Power(command, reboot: false),
                _ => RemoteResult.Fail($"Команда {command.CommandType} не поддерживается агентом.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Ошибка исполнения команды {CommandId}", command.CommandId);
            result = RemoteResult.Fail("Внутренняя ошибка агента при исполнении команды.");
        }

        // Результат больше лимита канала не отправляется: команда считается неудачной.
        if (result.Output is { } output && output.GetRawText().Length > RemoteLimits.MaxOutputChars)
        {
            result = RemoteResult.Fail("Результат команды слишком большой для передачи.");
        }

        await Finish(command, result.Ok ? CommandState.Succeeded : CommandState.Failed, result.Error, report, begin: false,
            result.Ok ? result.Output : null);
    }

    private static RemoteResult ToRemote(PresentResult r) => new(r.Ok, r.Error);

    private RemoteResult Kill(CommandEnvelope command)
    {
        KillProcessPayload payload;
        try
        {
            payload = ContractJson.FromElement<KillProcessPayload>(command.Payload);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return RemoteResult.Fail("Некорректный payload KillProcess.");
        }

        if (payload.ProcessId <= 0 || string.IsNullOrWhiteSpace(payload.Name) || payload.Name.Length > 260)
        {
            return RemoteResult.Fail("Некорректный процесс.");
        }

        return ProtectedProcesses.IsProtected(payload.Name)
            ? RemoteResult.Fail($"Процесс «{payload.Name}» системный — завершать нельзя.")
            : remote!.KillProcess(payload.ProcessId, payload.Name);
    }

    private RemoteResult Power(CommandEnvelope command, bool reboot)
    {
        PowerPayload payload;
        try
        {
            payload = command.Payload.ValueKind == JsonValueKind.Object ? ContractJson.FromElement<PowerPayload>(command.Payload) : new PowerPayload();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return RemoteResult.Fail("Некорректный payload питания.");
        }

        if (payload.DelaySeconds is < 0 or > RemoteLimits.MaxPowerDelaySeconds || payload.Message?.Length > MaxMessageLength)
        {
            return RemoteResult.Fail("Payload питания вне допустимых значений.");
        }

        return remote!.Power(reboot, payload.DelaySeconds, payload.Message);
    }

    private async Task<PresentResult> ShowMessage(CommandEnvelope command, CancellationToken ct)
    {
        ShowMessagePayload payload;
        try
        {
            payload = ContractJson.FromElement<ShowMessagePayload>(command.Payload);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return PresentResult.Fail("Некорректный payload ShowMessage.");
        }

        if (payload.Title.Length is 0 or > MaxTitleLength || payload.Message.Length is 0 or > MaxMessageLength)
        {
            return PresentResult.Fail("Payload ShowMessage вне допустимых размеров.");
        }

        return await presenter.ShowMessageAsync(command.CommandId, payload.Title, payload.Message, ct);
    }

    private async Task<PresentResult> Lock(CommandEnvelope command, CancellationToken ct)
    {
        LockTestModePayload payload;
        try
        {
            payload = ContractJson.FromElement<LockTestModePayload>(command.Payload);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return PresentResult.Fail("Некорректный payload LockTestMode.");
        }

        return await presenter.SetLockAsync(command.CommandId, payload.Lock, payload.Reason, ct);
    }

    private async Task Finish(CommandEnvelope command, CommandState state, string? error,
        Func<CommandState, string?, JsonElement?, Task> report, bool begin, JsonElement? output = null)
    {
        var now = time.GetUtcNow();
        if (begin && !executed.TryBegin(command.CommandId, now))
        {
            return;
        }

        executed.Complete(command.CommandId, state, error, now);
        logger.LogInformation("Команда {CommandId} ({Type}): {State} {Error}", command.CommandId, command.CommandType,
            state, error);
        await report(state, error, output);
    }
}
