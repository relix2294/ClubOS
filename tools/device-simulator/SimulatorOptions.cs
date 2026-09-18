namespace ClubOS.DeviceSimulator;

/// <summary>
/// Параметры симулятора (ТЗ §3.4). Это ОТДЕЛЬНЫЙ, ЯВНО МАРКИРОВАННЫЙ инструмент —
/// он НЕ заменяет обязательный реальный Windows Agent.
/// </summary>
public sealed record SimulatorOptions
{
    /// <summary>Локальный Edge Controller — цель heartbeat/команд.</summary>
    public required string EdgeBaseUrl { get; init; }

    /// <summary>Сколько ПК симулировать.</summary>
    public int DeviceCount { get; init; } = 5;

    /// <summary>Период heartbeat, сек.</summary>
    public int HeartbeatSeconds { get; init; } = 10;

    /// <summary>Вероятность смены состояния online/offline на каждом тике (0..1).</summary>
    public double FlipProbability { get; init; } = 0.1;

    public static SimulatorOptions FromEnvironment()
    {
        var edge = Environment.GetEnvironmentVariable("SIM_EDGE_URL") ?? "http://localhost:5080/";
        if (!edge.EndsWith('/'))
        {
            edge += "/";
        }

        return new SimulatorOptions
        {
            EdgeBaseUrl = edge,
            DeviceCount = ParseInt("SIM_DEVICE_COUNT", 5),
            HeartbeatSeconds = ParseInt("SIM_HEARTBEAT_SECONDS", 10),
        };
    }

    private static int ParseInt(string envName, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(envName), out var value) && value > 0 ? value : fallback;
}
