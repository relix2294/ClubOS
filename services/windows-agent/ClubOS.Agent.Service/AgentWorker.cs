using ClubOS.Agent.Core;

namespace ClubOS.Agent.Service;

/// <summary>Хост агентного цикла. Корректно останавливается по сигналу службы (ТЗ §25.2.5).</summary>
public sealed class AgentWorker(AgentRuntime runtime, ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await runtime.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("ClubOS Agent остановлен.");
        }
    }
}
