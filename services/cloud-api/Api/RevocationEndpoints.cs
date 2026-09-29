using ClubOS.CloudApi.Auth;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Edges;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.CloudApi.Security;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Api;

/// <summary>
/// Удаление (отзыв) устройств и отключение Edge (D-011). Запись остаётся ради истории, но сертификат больше
/// не принимается: Edge забывает устройство (команда + конфигурация), Cloud отклоняет отключённый Edge.
/// Вернуть можно только новой регистрацией по одноразовому токену.
/// </summary>
public static class RevocationEndpoints
{
    public static void MapRevocationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/devices/{deviceId}/revoke", RevokeDevice).WithTags("Devices")
            .RequirePermission(Permissions.EnrollmentManage);
        app.MapPost("/api/v1/edges/{edgeId}/revoke", RevokeEdge).WithTags("Edge")
            .RequirePermission(Permissions.EnrollmentManage);
    }

    private static async Task<IResult> RevokeDevice(string deviceId, HttpContext http, LocationScope scope,
        ClubOsDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var device = await db.Devices.SingleOrDefaultAsync(
            x => x.Id == deviceId && x.TenantId == staff.TenantId && x.RevokedAtUtc == null, ct);
        if (device is null || !await scope.CanAccessAsync(device.LocationId, ct))
        {
            return Problems.NotFound("Устройство");
        }

        if (await db.Sessions.AnyAsync(x => x.DeviceId == deviceId &&
                                            (x.State == SessionState.Created || x.State == SessionState.Active), ct))
        {
            return Problems.Conflict("session_open", "На устройстве идёт сессия. Завершите её перед удалением.");
        }

        var now = time.GetUtcNow();
        device.RevokedAtUtc = now;
        device.RevokedBy = staff.Actor;
        device.Status = DeviceStatus.Offline;
        EdgeQueue.Enqueue(db, staff.TenantId, device.LocationId, new EdgeCommand
        {
            Id = Ids.New("ecm"),
            Kind = EdgeCommandKind.RevokeDevice,
            IssuedAtUtc = now,
            ExpiresAtUtc = now.AddDays(30), // отзыв не должен потеряться, пока Edge offline
            RevokeDevice = new RevokeDeviceCommand { DeviceId = deviceId, Actor = staff.Actor }
        });
        audit.Write(staff.TenantId, device.LocationId, staff.Actor, "device.revoked", $"device:{deviceId}",
            AuditResults.Success, details: new { device.DisplayName, device.Hostname });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RevokeEdge(string edgeId, HttpContext http, LocationScope scope, ClubOsDbContext db,
        AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var edge = await db.Edges.SingleOrDefaultAsync(
            x => x.Id == edgeId && x.TenantId == staff.TenantId && x.RevokedAtUtc == null, ct);
        if (edge is null || !await scope.CanAccessAsync(edge.LocationId, ct))
        {
            return Problems.NotFound("Edge");
        }

        edge.RevokedAtUtc = time.GetUtcNow();
        edge.RevokedBy = staff.Actor;
        audit.Write(staff.TenantId, edge.LocationId, staff.Actor, "edge.revoked", $"edge:{edgeId}", AuditResults.Success,
            details: new { edge.Name, edge.PendingOutboxEvents });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
