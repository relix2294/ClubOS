using ClubOS.CloudApi.Api;
using ClubOS.CloudApi.Common;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Security;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Endpoints;

/// <summary>
/// Enrollment устройств (ТЗ §3.1, §8 AUTH-007): выдача одноразового токена сотрудником
/// и обмен токена устройством на индивидуальный сертификат (dev-CA).
/// </summary>
public static class EnrollmentEndpoints
{
    private static readonly TimeSpan TokenTtl = TimeSpan.FromHours(1);

    public static void MapEnrollmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/enrollment").WithTags("Enrollment");

        // Выдача одноразового токена — только авторизованный сотрудник.
        group.MapPost("/tokens", async (
            EnrollmentTokenRequest request,
            ClubOsDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            var caller = Caller.From(http.User);
            if (caller is null)
            {
                return Results.Unauthorized();
            }

            var zone = await db.Zones
                .AsNoTracking()
                .SingleOrDefaultAsync(z => z.Id == request.ZoneId && z.LocationId == request.LocationId, ct);

            var location = await db.Locations
                .AsNoTracking()
                .SingleOrDefaultAsync(l => l.Id == request.LocationId && l.OrganizationId == caller.OrganizationId, ct);

            if (zone is null || location is null)
            {
                return Results.BadRequest(new { error = "location/zone не найдены в вашей организации." });
            }

            var token = OneTimeToken.Generate();
            var now = DateTimeOffset.UtcNow;
            var expires = now.Add(TokenTtl);

            db.EnrollmentTokens.Add(new EnrollmentToken
            {
                Id = Ids.New(),
                LocationId = request.LocationId,
                ZoneId = request.ZoneId,
                DisplayName = request.DisplayName,
                TokenHash = OneTimeToken.Hash(token),
                CreatedBy = caller.UserId,
                CreatedAtUtc = now,
                ExpiresAtUtc = expires,
            });

            AuditLog.Add(db, caller, "enrollment.token.issued", request.DisplayName, "success", request.LocationId);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new EnrollmentTokenResponse
            {
                EnrollmentToken = token,
                ExpiresAtUtc = expires,
            });
        }).RequireAuthorization();

        // Обмен токена на сертификат — устройство ещё не имеет credential (анонимно).
        group.MapPost("/devices", async (
            DeviceEnrollRequest request,
            ClubOsDbContext db,
            DevCertificateAuthority ca,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.EnrollmentToken)
                || string.IsNullOrWhiteSpace(request.CertificateSigningRequestPem))
            {
                return Results.BadRequest(new { error = "enrollmentToken и CSR обязательны." });
            }

            var hash = OneTimeToken.Hash(request.EnrollmentToken);
            var now = DateTimeOffset.UtcNow;

            var enrollment = await db.EnrollmentTokens
                .SingleOrDefaultAsync(t => t.TokenHash == hash, ct);

            if (enrollment is null || enrollment.UsedAtUtc is not null || enrollment.ExpiresAtUtc <= now)
            {
                return Results.Json(new { error = "Токен недействителен, использован или просрочен." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var deviceId = Ids.New();
            var signed = ca.SignDeviceCsr(request.CertificateSigningRequestPem, deviceId);
            if (signed is null)
            {
                return Results.BadRequest(new { error = "Некорректный CSR." });
            }

            var inv = request.Inventory;
            db.Devices.Add(new Device
            {
                Id = deviceId,
                LocationId = enrollment.LocationId,
                ZoneId = enrollment.ZoneId,
                DisplayName = enrollment.DisplayName,
                Simulated = false,
                Status = DeviceStatus.Idle,
                LastHeartbeatUtc = now,
                Hostname = inv.Hostname,
                WindowsVersion = inv.WindowsVersion,
                Cpu = inv.Cpu,
                RamMegabytes = inv.RamMegabytes,
                Ipv4 = inv.Ipv4,
                AgentVersion = inv.AgentVersion,
                CertificatePem = signed.CertificatePem,
                EnrolledAtUtc = now,
            });

            enrollment.UsedAtUtc = now;

            // Организация определяется через локацию токена — чтобы запись попала в timeline арендатора.
            var organizationId = await db.Locations
                .Where(l => l.Id == enrollment.LocationId)
                .Select(l => l.OrganizationId)
                .SingleAsync(ct);

            db.AuditEvents.Add(new AuditEvent
            {
                Id = Ids.New(),
                TenantId = organizationId,
                LocationId = enrollment.LocationId,
                OccurredAtUtc = now,
                Actor = $"device:{deviceId}",
                Action = "device.enrolled",
                Target = deviceId,
                Result = "success",
            });

            await db.SaveChangesAsync(ct);

            return Results.Ok(new DeviceEnrollResponse
            {
                DeviceId = deviceId,
                DeviceCertificatePem = signed.CertificatePem,
                CertificateExpiresAtUtc = signed.ExpiresAtUtc,
            });
        }).AllowAnonymous();
    }
}
