using ClubOS.CloudApi.Api;
using ClubOS.CloudApi.Data;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Live;

/// <summary>
/// Изменения, которые не записываются в БД, а наступают со временем: устройство «Не в сети», когда heartbeat
/// устарел, и Edge «не на связи», когда он перестал отчитываться. Раз в несколько секунд сравнивает
/// эффективные статусы с прошлым снимком и публикует разницу. Работает, только пока есть подписчики.
/// </summary>
public sealed class LivePresenceMonitor(
    IServiceScopeFactory scopes,
    LiveBroker broker,
    TimeProvider time,
    ILogger<LivePresenceMonitor> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    private Dictionary<string, (string Tenant, string Location, DeviceStatus Status)>? _devices;
    private Dictionary<string, (string Tenant, string Location, bool Online)>? _edges;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, stoppingToken);
                if (broker.SubscriberCount == 0)
                {
                    _devices = null; // без подписчиков не опрашиваем БД; снимок строится заново при подключении
                    _edges = null;
                    continue;
                }

                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Мониторинг присутствия для live-обновлений не удался: {Error}", ex.Message);
            }
        }
    }

    public async Task ScanAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubOsDbContext>();
        var now = time.GetUtcNow();

        var devices = (await db.Devices.AsNoTracking().Where(d => d.RevokedAtUtc == null)
                .Select(d => new { d.Id, d.TenantId, d.LocationId, d.Status, d.LastHeartbeatUtc })
                .ToListAsync(ct))
            .ToDictionary(d => d.Id, d => (Tenant: d.TenantId, Location: d.LocationId,
                Status: d.LastHeartbeatUtc is null || now - d.LastHeartbeatUtc > Mapping.HeartbeatWindow ? DeviceStatus.Offline : d.Status));
        var edges = (await db.Edges.AsNoTracking().Where(e => e.RevokedAtUtc == null)
                .Select(e => new { e.Id, e.TenantId, e.LocationId, e.LastSeenAtUtc })
                .ToListAsync(ct))
            .ToDictionary(e => e.Id, e => (Tenant: e.TenantId, Location: e.LocationId,
                Online: e.LastSeenAtUtc is not null && now - e.LastSeenAtUtc <= Mapping.EdgeOnlineWindow));

        if (_devices is not null && _edges is not null)
        {
            foreach (var (id, current) in devices)
            {
                if (!_devices.TryGetValue(id, out var previous) || previous.Status != current.Status)
                {
                    broker.Publish(new LiveEvent(LiveTopics.Devices, current.Tenant, current.Location, id));
                }
            }

            foreach (var (id, current) in edges)
            {
                if (!_edges.TryGetValue(id, out var previous) || previous.Online != current.Online)
                {
                    broker.Publish(new LiveEvent(LiveTopics.Edges, current.Tenant, current.Location, null, id));
                }
            }
        }

        _devices = devices;
        _edges = edges;
    }
}
