using ClubOS.DeviceSimulator;

var options = SimulatorOptions.FromEnvironment();

Console.WriteLine("==================================================");
Console.WriteLine("  ClubOS DEVICE SIMULATOR — SIMULATED устройства");
Console.WriteLine("  Это НЕ реальные Agent (ТЗ §3.4). Только для dev.");
Console.WriteLine("==================================================");
SimLog.Log($"Edge: {options.EdgeBaseUrl}, устройств: {options.DeviceCount}, heartbeat: {options.HeartbeatSeconds}s");

using var http = new HttpClient { BaseAddress = new Uri(options.EdgeBaseUrl) };
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    SimLog.Log("Остановка симулятора…");
};

var devices = Enumerable.Range(1, options.DeviceCount)
    .Select(i => new SimulatedDevice($"SIM-PC-{i:D2}", http, options))
    .ToList();

try
{
    await Task.WhenAll(devices.Select(d => d.RunAsync(cts.Token)));
}
catch (OperationCanceledException)
{
    // Штатная остановка по Ctrl+C.
}

SimLog.Log("Симулятор остановлен.");

/// <summary>Простое логирование симулятора с явной пометкой SIMULATED.</summary>
internal static class SimLog
{
    private static readonly object Gate = new();

    public static void Log(string message)
    {
        lock (Gate)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] [SIMULATED] {message}");
        }
    }
}
