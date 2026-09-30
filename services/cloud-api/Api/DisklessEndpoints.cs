using ClubOS.CloudApi.Auth;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Edges;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.CloudApi.Security;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Api;

public sealed record PendingDisklessView(
    string CandidateId,
    string LocationId,
    string HardwareId,
    string Mac,
    IReadOnlyList<string> MacAddresses,
    string Hostname,
    string? Ipv4,
    bool Simulated,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc);

public sealed record ApproveDisklessRequest(string? DisplayName, string? ZoneId);

/// <summary>
/// Бездисковые ПК (D-018): ПК, загрузившиеся в клубе без регистрации, ждут подтверждения администратором.
/// Подтверждение привязывает MAC к новому устройству; Edge получает конфигурацию сразу (RefreshConfig)
/// и при следующей попытке загрузки выдаёт ПК сертификат своего локального CA.
/// </summary>
public static class DisklessEndpoints
{
    /// <summary>Сертификат бездискового ПК живёт на Edge; в Cloud срок не отслеживается.</summary>
    public static readonly DateTimeOffset NoCertificateExpiry = new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void MapDisklessEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").WithTags("Diskless").RequirePermission(Permissions.EnrollmentManage);
        api.MapGet("/locations/{locationId}/diskless-candidates", List);
        api.MapPost("/diskless-candidates/{candidateId}/approve", Approve);
        api.MapPost("/diskless-candidates/{candidateId}/dismiss", Dismiss);
    }

    public static PendingDisklessView ToView(this PendingDisklessDevice c) => new(
        c.Id, c.LocationId, c.HardwareId, HardwareIds.FormatMac(c.HardwareId),
        c.MacAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(HardwareIds.FormatMac).ToList(),
        c.Hostname, c.Ipv4, c.Simulated, c.FirstSeenUtc, c.LastSeenUtc);

    private static async Task<IResult> List(string locationId, HttpContext http, LocationScope scope, ClubOsDbContext db,
        CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        if (!await scope.CanAccessAsync(locationId, ct))
        {
            return Problems.NotFound("Локация");
        }

        var rows = await db.PendingDisklessDevices.AsNoTracking()
            .Where(x => x.TenantId == staff.TenantId && x.LocationId == locationId)
            .OrderBy(x => x.FirstSeenUtc).ToListAsync(ct);
        return Results.Ok(rows.Select(x => x.ToView()).ToList());
    }

    private static async Task<IResult> Approve(string candidateId, ApproveDisklessRequest request, HttpContext http,
        LocationScope scope, ClubOsDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var candidate = await db.PendingDisklessDevices.SingleOrDefaultAsync(x => x.Id == candidateId && x.TenantId == staff.TenantId, ct);
        if (candidate is null || !await scope.CanAccessAsync(candidate.LocationId, ct))
        {
            return Problems.NotFound("ПК");
        }

        var name = request.DisplayName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 64)
        {
            return Problems.Validation("invalid_name", "Имя ПК 1–64 символа.");
        }

        var zone = await db.Zones.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ZoneId && x.LocationId == candidate.LocationId, ct);
        if (zone is null)
        {
            return Problems.Validation("invalid_zone", "Выберите зону этой локации.");
        }

        if (await db.Devices.AnyAsync(x => x.TenantId == staff.TenantId && x.HardwareId == candidate.HardwareId && x.RevokedAtUtc == null, ct))
        {
            db.PendingDisklessDevices.Remove(candidate);
            await db.SaveChangesAsync(ct);
            return Problems.Conflict("already_approved", "Этот ПК уже подтверждён.");
        }

        var now = time.GetUtcNow();
        var device = new Device
        {
            Id = Ids.New("dev"),
            TenantId = staff.TenantId,
            LocationId = candidate.LocationId,
            ZoneId = zone.Id,
            DisplayName = name,
            Simulated = candidate.Simulated,
            HardwareId = candidate.HardwareId,
            Hostname = candidate.Hostname,
            Ipv4 = candidate.Ipv4,
            CertificatePem = string.Empty,
            CertificateExpiresAtUtc = NoCertificateExpiry,
            EnrolledAtUtc = now
        };
        db.Devices.Add(device);
        db.PendingDisklessDevices.Remove(candidate);
        EdgeQueue.Enqueue(db, staff.TenantId, candidate.LocationId, new EdgeCommand
        {
            Id = Ids.New("ecm"),
            Kind = EdgeCommandKind.RefreshConfig,
            IssuedAtUtc = now,
            ExpiresAtUtc = now.AddDays(1)
        });
        audit.Write(staff.TenantId, candidate.LocationId, staff.Actor, "device.diskless_approved", $"device:{device.Id}",
            AuditResults.Success, details: new { mac = HardwareIds.FormatMac(candidate.HardwareId), name, zone = zone.Name });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Problems.Conflict("already_approved", "Этот ПК уже подтверждён.");
        }

        return Results.Created($"/api/v1/devices/{device.Id}", new { deviceId = device.Id });
    }

    private static async Task<IResult> Dismiss(string candidateId, HttpContext http, LocationScope scope, ClubOsDbContext db,
        AuditWriter audit, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var candidate = await db.PendingDisklessDevices.SingleOrDefaultAsync(x => x.Id == candidateId && x.TenantId == staff.TenantId, ct);
        if (candidate is null || !await scope.CanAccessAsync(candidate.LocationId, ct))
        {
            return Problems.NotFound("ПК");
        }

        db.PendingDisklessDevices.Remove(candidate);
        audit.Write(staff.TenantId, candidate.LocationId, staff.Actor, "device.diskless_dismissed", $"diskless:{candidate.HardwareId}",
            AuditResults.Success, details: new { mac = HardwareIds.FormatMac(candidate.HardwareId), candidate.Hostname });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
