using System.Text.Json;
using ClubOS.Contracts;

namespace ClubOS.Agent.Core;

public sealed record ExecutedCommand(string CommandId, CommandState State, string? Error, DateTimeOffset AtUtc);

/// <summary>
/// Журнал исполненных команд на диске (ТЗ §10.2 CMD-002): повторная доставка той же команды
/// не исполняет её второй раз, а повторно сообщает сохранённый результат. Запись делается
/// ДО показа UI — после сбоя команда не будет показана дважды (at-most-once для эффекта).
/// </summary>
public sealed class ExecutedCommandStore
{
    private const int MaxEntries = 1000;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ExecutedCommand> _entries;

    public ExecutedCommandStore(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        _path = Path.Combine(dataPath, "executed-commands.json");
        _entries = File.Exists(_path)
            ? (JsonSerializer.Deserialize<List<ExecutedCommand>>(File.ReadAllText(_path), ContractJson.Options) ?? [])
                .ToDictionary(x => x.CommandId)
            : [];
    }

    public ExecutedCommand? Get(string commandId)
    {
        lock (_gate)
        {
            return _entries.GetValueOrDefault(commandId);
        }
    }

    /// <summary>Атомарно резервирует commandId. false — команда уже исполнялась.</summary>
    public bool TryBegin(string commandId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(commandId))
            {
                return false;
            }

            _entries[commandId] = new ExecutedCommand(commandId, CommandState.Acknowledged, null, now);
            Persist(now);
            return true;
        }
    }

    public void Complete(string commandId, CommandState state, string? error, DateTimeOffset now)
    {
        lock (_gate)
        {
            _entries[commandId] = new ExecutedCommand(commandId, state, error, now);
            Persist(now);
        }
    }

    private void Persist(DateTimeOffset now)
    {
        foreach (var old in _entries.Values.Where(x => now - x.AtUtc > Retention).Select(x => x.CommandId).ToList())
        {
            _entries.Remove(old);
        }

        var list = _entries.Values.OrderByDescending(x => x.AtUtc).Take(MaxEntries).ToList();
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, ContractJson.Options));
        File.Move(temp, _path, overwrite: true);
    }
}
