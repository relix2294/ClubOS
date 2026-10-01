using System.Text;
using System.Text.Json;
using ClubOS.Contracts;

namespace ClubOS.Agent.Core;

public sealed record RemoteResult(bool Ok, string? Error, JsonElement? Output = null)
{
    public static RemoteResult Fail(string error) => new(false, error);

    public static RemoteResult Success<T>(T output) => new(true, null, ContractJson.ToElement(output));

    public static RemoteResult Done { get; } = new(true, null);
}

/// <summary>
/// Удалённый доступ к ПК (D-022): снимок экрана, процессы пользователя, перезагрузка и выключение.
/// Только эти действия — произвольных команд нет (ТЗ §10.2 CMD-004).
/// </summary>
public interface IRemoteActions
{
    Task<RemoteResult> ScreenshotAsync(CancellationToken ct);

    RemoteResult ListProcesses();

    RemoteResult KillProcess(int processId, string name);

    RemoteResult Power(bool reboot, int delaySeconds, string? message);
}

/// <summary>Симулятор и консольный режим: синтетический снимок (SVG) и вымышленные процессы.</summary>
public sealed class SimulatedRemoteActions(string deviceName, TimeProvider time) : IRemoteActions
{
    private readonly Lock _gate = new();

    private readonly List<ProcessInfo> _processes =
    [
        new() { ProcessId = 4120, Name = "steam", MemoryMb = 310, WindowTitle = "Steam" },
        new() { ProcessId = 5288, Name = "cs2", MemoryMb = 2950, WindowTitle = "Counter-Strike 2" },
        new() { ProcessId = 6012, Name = "Discord", MemoryMb = 420, WindowTitle = "Discord" },
        new() { ProcessId = 7344, Name = "chrome", MemoryMb = 780, WindowTitle = "YouTube — Google Chrome" }
    ];

    public Task<RemoteResult> ScreenshotAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var titles = string.Join(" · ", ListSnapshot().Select(p => p.WindowTitle).OfType<string>());
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="640" height="360" viewBox="0 0 640 360">
              <rect width="640" height="360" fill="#0f172a"/>
              <rect x="20" y="20" width="600" height="280" rx="8" fill="#1e293b"/>
              <text x="40" y="70" fill="#e2e8f0" font-family="sans-serif" font-size="28">{Escape(deviceName)}</text>
              <text x="40" y="110" fill="#94a3b8" font-family="sans-serif" font-size="16">SIMULATED · {now:yyyy-MM-dd HH:mm:ss} UTC</text>
              <text x="40" y="150" fill="#cbd5e1" font-family="sans-serif" font-size="14">{Escape(titles)}</text>
              <rect x="0" y="320" width="640" height="40" fill="#334155"/>
            </svg>
            """;
        return Task.FromResult(RemoteResult.Success(new ScreenshotOutput
        {
            Mime = "image/svg+xml",
            DataBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg)),
            Width = 640,
            Height = 360,
            CapturedAtUtc = now
        }));
    }

    public RemoteResult ListProcesses() => RemoteResult.Success(new ProcessListOutput { Processes = ListSnapshot() });

    public RemoteResult KillProcess(int processId, string name)
    {
        lock (_gate)
        {
            var found = _processes.FindIndex(p => p.ProcessId == processId);
            if (found < 0 || !string.Equals(_processes[found].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return RemoteResult.Fail("Процесс уже завершён или PID занят другим процессом.");
            }

            _processes.RemoveAt(found);
            return RemoteResult.Done;
        }
    }

    public RemoteResult Power(bool reboot, int delaySeconds, string? message) => RemoteResult.Done;

    private List<ProcessInfo> ListSnapshot()
    {
        lock (_gate)
        {
            return [.. _processes];
        }
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
