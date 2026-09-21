using System.Text.Json;
using ClubOS.CloudApi.Api;
using ClubOS.CloudApi.Common;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Endpoints;

/// <summary>
/// Приём sync-батча событий Edge → Cloud (ТЗ §4). Идемпотентность гарантируется
/// уникальностью EventId в inbox_receipts (не только проверкой в памяти).
/// </summary>
public static class SyncEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Батч событий домена от Edge.</summary>
    public sealed record SyncBatchRequest
    {
        public required IReadOnlyList<EventEnvelope> Events { get; init; }
    }

    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sync").WithTags("Sync").RequireAuthorization();

        group.MapPost("/events", async (
            SyncBatchRequest request,
            ClubOsDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            var caller = Caller.From(http.User);
            if (caller is null)
            {
                return Results.Unauthorized();
            }

            if (request.Events.Count == 0)
            {
                return Results.Ok(new SyncResult { Accepted = 0, Duplicates = 0 });
            }

            // Изоляция арендатора: принимаем только события локаций своей организации (ТЗ §6.1).
            var orgLocationIds = await db.Locations
                .Where(l => l.OrganizationId == caller.OrganizationId)
                .Select(l => l.Id)
                .ToListAsync(ct);
            var orgLocations = orgLocationIds.ToHashSet();

            var eventIds = request.Events.Select(e => e.EventId).ToHashSet();
            var known = await db.InboxReceipts
                .Where(r => eventIds.Contains(r.EventId))
                .Select(r => r.EventId)
                .ToListAsync(ct);
            var seen = known.ToHashSet();

            var accepted = 0;
            var duplicates = 0;

            foreach (var evt in request.Events)
            {
                if (!orgLocations.Contains(evt.LocationId))
                {
                    // Чужая/неизвестная локация — молча пропускаем (не наш арендатор).
                    continue;
                }

                if (!seen.Add(evt.EventId))
                {
                    duplicates++;
                    continue;
                }

                db.InboxReceipts.Add(new InboxReceipt
                {
                    EventId = evt.EventId,
                    TenantId = caller.OrganizationId,
                    LocationId = evt.LocationId,
                    EventType = evt.EventType,
                    Sequence = evt.Sequence,
                    ReceivedAtUtc = DateTimeOffset.UtcNow,
                });

                await ApplyAsync(db, evt, ct);
                accepted++;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new SyncResult { Accepted = accepted, Duplicates = duplicates });
        });
    }

    // Побочные эффекты известных типов событий M0. Неизвестные типы только фиксируются в inbox.
    private static async Task ApplyAsync(ClubOsDbContext db, EventEnvelope evt, CancellationToken ct)
    {
        if (evt.EventType != "DeviceHeartbeat" || evt.Payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        HeartbeatMessage? heartbeat;
        try
        {
            heartbeat = evt.Payload.Deserialize<HeartbeatMessage>(JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (heartbeat is null)
        {
            return;
        }

        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == heartbeat.DeviceId, ct);
        if (device is null || device.LocationId != evt.LocationId)
        {
            return;
        }

        device.Status = heartbeat.Status;
        device.LastHeartbeatUtc = evt.OccurredAtUtc.ToUniversalTime();

        if (heartbeat.Inventory is { } inv)
        {
            device.Hostname = inv.Hostname;
            device.WindowsVersion = inv.WindowsVersion;
            device.Cpu = inv.Cpu;
            device.RamMegabytes = inv.RamMegabytes;
            device.Ipv4 = inv.Ipv4;
            device.AgentVersion = inv.AgentVersion;
        }
    }
}
