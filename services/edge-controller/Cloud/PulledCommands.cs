using System.Text.Json;
using ClubOS.Contracts;

namespace ClubOS.EdgeController.Cloud;

/// <summary>Разобранный ответ очереди Cloud → Edge: понятные элементы и ID элементов неизвестного вида.</summary>
public sealed record PulledCommands(IReadOnlyList<EdgeCommand> Commands, IReadOnlyList<string> Unsupported)
{
    public static PulledCommands Parse(IReadOnlyList<JsonElement> items)
    {
        var commands = new List<EdgeCommand>();
        var unsupported = new List<string>();
        foreach (var item in items)
        {
            try
            {
                var command = item.Deserialize<EdgeCommand>(ContractJson.Options);
                if (command is not null && Enum.IsDefined(command.Kind))
                {
                    commands.Add(command);
                    continue;
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
            }

            unsupported.Add(item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) &&
                            id.ValueKind == JsonValueKind.String
                ? id.GetString()!
                : "?");
        }

        return new PulledCommands(commands, unsupported);
    }
}
