using System.Runtime.Versioning;
using System.Text.Json;
using ClubOS.Contracts;
using ClubOS.WindowsAgent.Ipc;

namespace ClubOS.WindowsAgent.Worker;

/// <summary>
/// Исполнение команд из allow-list M0 (ТЗ §25.2.5): ShowMessage и LockTestMode.
/// Отображение делегируется session-host через <see cref="UiDispatcher"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CommandExecutor(UiDispatcher ui)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<(bool Ok, string? Error)> ExecuteAsync(
        CommandType type, JsonElement payload, CancellationToken ct)
    {
        try
        {
            switch (type)
            {
                case CommandType.ShowMessage:
                    var message = payload.Deserialize<ShowMessagePayload>(JsonOptions);
                    if (message is null)
                    {
                        return (false, "пустой payload ShowMessage");
                    }

                    var showResp = await ui.SendAsync(new UiRequest
                    {
                        Kind = "ShowMessage",
                        Title = message.Title,
                        Message = message.Message,
                    }, ct);
                    return (showResp.Ok, showResp.Error);

                case CommandType.LockTestMode:
                    var lockPayload = payload.Deserialize<LockTestModePayload>(JsonOptions);
                    if (lockPayload is null)
                    {
                        return (false, "пустой payload LockTestMode");
                    }

                    var lockResp = await ui.SendAsync(new UiRequest
                    {
                        Kind = "LockTestMode",
                        Lock = lockPayload.Lock,
                        Message = lockPayload.Reason,
                    }, ct);
                    return (lockResp.Ok, lockResp.Error);

                default:
                    return (false, $"неизвестный тип команды: {type}");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            return (false, $"некорректный payload: {ex.Message}");
        }
    }
}
