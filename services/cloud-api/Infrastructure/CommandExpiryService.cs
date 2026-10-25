using ClubOS.CloudApi.Data;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Infrastructure;

/// <summary>
/// Переводит просроченные нетерминальные команды в Expired (ТЗ §10.2 CMD-006) и пишет аудит.
/// Edge и Agent независимо отказываются исполнять просроченные команды.
/// </summary>
public sealed class CommandExpiryService(IServiceScopeFactory scopes, TimeProvider time, ILogger<CommandExpiryService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExpireOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Command expiry pass failed");
            }

            try
            {
                await Task.Delay(Interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task<int> ExpireOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubOsDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditWriter>();
        var now = time.GetUtcNow();

        var expired = await db.DeviceCommands
            .Where(x => (x.State == CommandState.Queued || x.State == CommandState.Delivered ||
                         x.State == CommandState.Acknowledged) && x.ExpiresAtUtc < now)
            .Take(200).ToListAsync(ct);

        foreach (var command in expired)
        {
            command.State = CommandState.Expired;
            command.Error = "Команда не выполнена до истечения срока (expiresAtUtc).";
            command.UpdatedAtUtc = now;
            audit.Write(command.TenantId, command.LocationId, "system:cloud", $"command.{command.CommandType}",
                $"device:{command.DeviceId}", AuditResults.Failed, command.CorrelationId,
                new { commandId = command.Id, state = CommandState.Expired });
        }

        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return expired.Count;
    }
}
