using ClubOS.Contracts;
using Microsoft.Extensions.Logging;

namespace ClubOS.Agent.Core;

/// <summary>
/// Исполнение команд на устройстве. Только allow-list (ShowMessage, LockTestMode) — произвольных
/// команд нет (ТЗ §10.2 CMD-004). Проверки: адресат, срок, повтор (CMD-002, CMD-006).
/// </summary>
public sealed class CommandExecutor(
    IUserPresenter presenter,
    ExecutedCommandStore executed,
    TimeProvider time,
    ILogger<CommandExecutor> logger)
{
    public const int MaxTitleLength = 80;
    public const int MaxMessageLength = 500;

    /// <summary>
    /// Возвращает последовательность отчётов для Edge: Acknowledged, затем Succeeded/Failed.
    /// Для повторно доставленной команды — только сохранённый итог, без повторного исполнения.
    /// </summary>
    public async Task ExecuteAsync(string deviceId, CommandEnvelope command,
        Func<CommandState, string?, Task> report, CancellationToken ct)
    {
        var previous = executed.Get(command.CommandId);
        if (previous is not null)
        {
            logger.LogInformation("Команда {CommandId} уже исполнялась — повторно сообщаем итог {State}",
                command.CommandId, previous.State);
            if (previous.State is CommandState.Succeeded or CommandState.Failed)
            {
                await report(previous.State, previous.Error);
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

        await report(CommandState.Acknowledged, null);

        PresentResult result;
        try
        {
            result = command.CommandType switch
            {
                CommandType.ShowMessage => await ShowMessage(command, ct),
                CommandType.LockTestMode => await Lock(command, ct),
                _ => PresentResult.Fail($"Команда {command.CommandType} не поддерживается агентом.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Ошибка исполнения команды {CommandId}", command.CommandId);
            result = PresentResult.Fail("Внутренняя ошибка агента при исполнении команды.");
        }

        await Finish(command, result.Ok ? CommandState.Succeeded : CommandState.Failed, result.Error, report, begin: false);
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
        Func<CommandState, string?, Task> report, bool begin)
    {
        var now = time.GetUtcNow();
        if (begin && !executed.TryBegin(command.CommandId, now))
        {
            return;
        }

        executed.Complete(command.CommandId, state, error, now);
        logger.LogInformation("Команда {CommandId} ({Type}): {State} {Error}", command.CommandId, command.CommandType,
            state, error);
        await report(state, error);
    }
}
