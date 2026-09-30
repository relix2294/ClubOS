using System.Text.Json;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.Contracts;
using ClubOS.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClubOS.CloudApi.Edges;

/// <summary>
/// API для Edge Controller. Все соединения инициирует Edge (исходящие, ТЗ §7.2).
/// Кроме enroll, все запросы требуют подписи ключом Edge (схема EdgeSignature).
/// </summary>
public static class EdgeEndpoints
{
    private const int MaxLongPollSeconds = 25;

    public static void MapEdgeEndpoints(this IEndpointRouteBuilder app)
    {
        var anonymous = app.MapGroup("/api/v1/edge").WithTags("Edge").RequireRateLimiting(RateLimits.Auth);
        anonymous.MapPost("/enroll", Enroll).AllowAnonymous();

        var edge = app.MapGroup("/api/v1/edge").WithTags("Edge").RequireAuthorization(Policies.Edge);
        edge.MapGet("/config", GetConfig);
        edge.MapPost("/sync", Sync);
        edge.MapGet("/commands", PullCommands);
        edge.MapPost("/commands/ack", AckCommands);
        edge.MapPost("/status", ReportStatus);
        edge.MapPost("/devices/enroll", EnrollDevice).RequireRateLimiting(RateLimits.Auth);
        edge.MapPost("/renew", RenewEdge);
        edge.MapPost("/server-certificate", IssueServerCertificate);
        edge.MapPost("/devices/{deviceId}/renew", RenewDevice);
    }

    private static async Task<IResult> Enroll(EdgeEnrollRequest request, ClubOsDbContext db, DevCertificateAuthority ca,
        IOptions<PkiOptions> pki, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var token = await ConsumeToken(db, request.EnrollmentToken, EnrollmentKinds.Edge, time, ct);
        if (token is null)
        {
            return Problems.Unauthorized("Enrollment-токен недействителен, истёк или уже использован.");
        }

        var edgeId = Ids.New("edge");
        IssuedCertificate cert;
        try
        {
            cert = ca.Issue(request.CertificateSigningRequestPem, edgeId, DevCertificateAuthority.RoleEdge,
                pki.Value.CertificateValidity);
        }
        catch (InvalidCsrException ex)
        {
            return Problems.Validation("invalid_csr", ex.Message);
        }

        var now = time.GetUtcNow();
        token.UsedBySubjectId = edgeId;
        db.Edges.Add(new Domain.Edge
        {
            Id = edgeId,
            TenantId = token.TenantId,
            LocationId = token.LocationId,
            Name = token.DisplayName,
            CertificatePem = cert.CertificatePem,
            CertificateExpiresAtUtc = cert.ExpiresAtUtc,
            EnrolledAtUtc = now
        });
        audit.Write(token.TenantId, token.LocationId, $"edge:{edgeId}", "edge.enrolled", $"edge:{edgeId}",
            AuditResults.Success, details: new { enrollmentTokenId = token.Id, name = token.DisplayName });
        await db.SaveChangesAsync(ct);

        return Results.Ok(new EdgeEnrollResponse
        {
            EdgeId = edgeId,
            TenantId = token.TenantId,
            LocationId = token.LocationId,
            EdgeCertificatePem = cert.CertificatePem,
            CaCertificatePem = ca.CertificatePem,
            CertificateExpiresAtUtc = cert.ExpiresAtUtc
        });
    }

    private static async Task<IResult> GetConfig(HttpContext http, ClubOsDbContext db, CancellationToken ct)
    {
        var edge = EdgeContext.From(http.User);
        var location = await db.Locations.AsNoTracking().SingleAsync(x => x.Id == edge.LocationId, ct);
        var zones = await db.Zones.AsNoTracking().Where(x => x.LocationId == edge.LocationId).ToListAsync(ct);
        var all = await db.Devices.AsNoTracking()
            .Where(x => x.LocationId == edge.LocationId && x.TenantId == edge.TenantId).ToListAsync(ct);
        var devices = all.Where(x => x.RevokedAtUtc is null).ToList();

        return Results.Ok(new EdgeConfigResponse
        {
            LocationId = location.Id,
            LocationName = location.Name,
            Timezone = location.Timezone,
            Currency = location.Currency,
            Zones = zones.Select(z => new EdgeZoneConfig
            {
                ZoneId = z.Id,
                Name = z.Name,
                PricePerHourMinorUnits = z.PricePerHourMinorUnits,
                Rounding = z.Rounding,
                RuleVersion = z.RuleVersion
            }).ToList(),
            Devices = devices.Select(d => new EdgeDeviceConfig
            {
                DeviceId = d.Id,
                DisplayName = d.DisplayName,
                ZoneId = d.ZoneId,
                Simulated = d.Simulated,
                CertificatePem = d.CertificatePem,
                HardwareId = d.HardwareId
            }).ToList(),
            RevokedDeviceIds = all.Where(x => x.RevokedAtUtc is not null).Select(x => x.Id).ToList()
        });
    }

    /// <summary>TLS-сертификат API агентов для Edge (D-007): SAN — имена и адреса Edge в LAN клуба.</summary>
    private static async Task<IResult> IssueServerCertificate(EdgeServerCertificateRequest request, HttpContext http,
        DevCertificateAuthority ca, IOptions<PkiOptions> pki, AuditWriter audit, ClubOsDbContext db, CancellationToken ct)
    {
        var edge = EdgeContext.From(http.User);
        var dns = (request.DnsNames ?? []).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList();
        if (dns.Any(x => x.Length > 253 || !System.Text.RegularExpressions.Regex.IsMatch(x,
                @"^(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))*$")))
        {
            return Problems.Validation("invalid_dns", "Недопустимое DNS-имя в SAN (без звёздочек и спецсимволов).");
        }

        var ips = new List<System.Net.IPAddress>();
        foreach (var raw in request.IpAddresses ?? [])
        {
            if (!System.Net.IPAddress.TryParse(raw, out var ip))
            {
                return Problems.Validation("invalid_ip", $"Недопустимый IP-адрес: {raw}.");
            }

            ips.Add(ip);
        }

        IssuedCertificate cert;
        try
        {
            cert = ca.IssueServer(request.CertificateSigningRequestPem, edge.EdgeId, dns, ips.Distinct().ToList(),
                pki.Value.CertificateValidity);
        }
        catch (InvalidCsrException ex)
        {
            return Problems.Validation("invalid_csr", ex.Message);
        }

        audit.Write(edge.TenantId, edge.LocationId, edge.Actor, "edge.tls_certificate_issued", $"edge:{edge.EdgeId}",
            AuditResults.Success, details: new { dns, ips = ips.Select(x => x.ToString()), cert.ExpiresAtUtc });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new CertificateRenewResponse
        {
            CertificatePem = cert.CertificatePem,
            CertificateExpiresAtUtc = cert.ExpiresAtUtc
        });
    }

    /// <summary>Продление сертификата Edge: запрос подписан текущим (ещё действующим) ключом Edge.</summary>
    private static async Task<IResult> RenewEdge(CertificateRenewRequest request, HttpContext http, ClubOsDbContext db,
        DevCertificateAuthority ca, IOptions<PkiOptions> pki, AuditWriter audit, CancellationToken ct)
    {
        var context = EdgeContext.From(http.User);
        var edge = await db.Edges.SingleAsync(x => x.Id == context.EdgeId, ct);
        IssuedCertificate cert;
        try
        {
            cert = ca.Issue(request.CertificateSigningRequestPem, edge.Id, DevCertificateAuthority.RoleEdge,
                pki.Value.CertificateValidity);
        }
        catch (InvalidCsrException ex)
        {
            return Problems.Validation("invalid_csr", ex.Message);
        }

        var previous = edge.CertificateExpiresAtUtc;
        edge.CertificatePem = cert.CertificatePem;
        edge.CertificateExpiresAtUtc = cert.ExpiresAtUtc;
        audit.Write(edge.TenantId, edge.LocationId, context.Actor, "edge.certificate_renewed", $"edge:{edge.Id}",
            AuditResults.Success, details: new { previous, cert.ExpiresAtUtc });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new CertificateRenewResponse
        {
            CertificatePem = cert.CertificatePem,
            CertificateExpiresAtUtc = cert.ExpiresAtUtc
        });
    }

    /// <summary>
    /// Продление сертификата устройства: агент подписал запрос к Edge своим ключом, Edge переслал.
    /// Только устройство локации этого Edge и только не отозванное.
    /// </summary>
    private static async Task<IResult> RenewDevice(string deviceId, CertificateRenewRequest request, HttpContext http,
        ClubOsDbContext db, DevCertificateAuthority ca, IOptions<PkiOptions> pki, AuditWriter audit, CancellationToken ct)
    {
        var edge = EdgeContext.From(http.User);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId && x.TenantId == edge.TenantId &&
                                                                x.LocationId == edge.LocationId && x.RevokedAtUtc == null, ct);
        if (device is null)
        {
            return Problems.NotFound("Устройство");
        }

        IssuedCertificate cert;
        try
        {
            cert = ca.Issue(request.CertificateSigningRequestPem, device.Id, DevCertificateAuthority.RoleDevice,
                pki.Value.CertificateValidity);
        }
        catch (InvalidCsrException ex)
        {
            return Problems.Validation("invalid_csr", ex.Message);
        }

        var previous = device.CertificateExpiresAtUtc;
        device.CertificatePem = cert.CertificatePem;
        device.CertificateExpiresAtUtc = cert.ExpiresAtUtc;
        audit.Write(edge.TenantId, edge.LocationId, edge.Actor, "device.certificate_renewed", $"device:{device.Id}",
            AuditResults.Success, details: new { previous, cert.ExpiresAtUtc });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new CertificateRenewResponse
        {
            CertificatePem = cert.CertificatePem,
            CertificateExpiresAtUtc = cert.ExpiresAtUtc
        });
    }

    private static async Task<IResult> Sync(SyncBatchRequest request, HttpContext http, SyncIngestor ingestor,
        CancellationToken ct)
    {
        if (request.Events is null || request.Events.Count > SyncIngestor.MaxBatchSize)
        {
            return Problems.Validation("invalid_batch", $"Пакет от 1 до {SyncIngestor.MaxBatchSize} событий.");
        }

        return Results.Ok(await ingestor.IngestAsync(EdgeContext.From(http.User), request.Events, ct));
    }

    /// <summary>
    /// Long-poll очереди Cloud → Edge. Неподтверждённые элементы выдаются повторно
    /// (at-least-once); просроченные не выдаются.
    /// </summary>
    private static async Task<IResult> PullCommands(int? waitSeconds, HttpContext http, ClubOsDbContext db,
        TimeProvider time, CancellationToken ct)
    {
        var edge = EdgeContext.From(http.User);
        var deadline = time.GetUtcNow().AddSeconds(Math.Clamp(waitSeconds ?? 0, 0, MaxLongPollSeconds));

        while (true)
        {
            var now = time.GetUtcNow();
            var items = await db.EdgeOutbox.AsNoTracking()
                .Where(x => x.LocationId == edge.LocationId && x.TenantId == edge.TenantId && x.AckedAtUtc == null &&
                            x.ExpiresAtUtc > now)
                .OrderBy(x => x.CreatedAtUtc).Take(50).ToListAsync(ct);

            if (items.Count > 0 || now >= deadline)
            {
                return Results.Ok(new EdgeCommandsResponse
                {
                    Commands = items.Select(x => JsonSerializer.Deserialize<EdgeCommand>(x.PayloadJson, ContractJson.Options)!)
                        .ToList()
                });
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), time, ct);
        }
    }

    private static async Task<IResult> AckCommands(EdgeCommandsAck request, HttpContext http, ClubOsDbContext db,
        TimeProvider time, CancellationToken ct)
    {
        var edge = EdgeContext.From(http.User);
        var ids = request.Ids?.Take(200).ToList() ?? [];
        var now = time.GetUtcNow();
        var count = await db.EdgeOutbox
            .Where(x => ids.Contains(x.Id) && x.LocationId == edge.LocationId && x.TenantId == edge.TenantId &&
                        x.AckedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AckedAtUtc, now), ct);
        return Results.Ok(new { acked = count });
    }

    private static async Task<IResult> ReportStatus(EdgeStatusReport report, HttpContext http, ClubOsDbContext db,
        TimeProvider time, CancellationToken ct)
    {
        var edge = EdgeContext.From(http.User);
        var entity = await db.Edges.SingleAsync(x => x.Id == edge.EdgeId, ct);
        entity.LastSeenAtUtc = time.GetUtcNow();
        entity.LastEdgeClockUtc = report.EdgeClockUtc;
        entity.PendingOutboxEvents = report.PendingOutboxEvents;

        var ids = report.Devices.Select(x => x.DeviceId).ToList();
        var devices = await db.Devices
            .Where(x => ids.Contains(x.Id) && x.LocationId == edge.LocationId && x.TenantId == edge.TenantId)
            .ToDictionaryAsync(x => x.Id, ct);
        foreach (var entry in report.Devices)
        {
            if (!devices.TryGetValue(entry.DeviceId, out var device))
            {
                continue;
            }

            device.Status = entry.Status;
            device.LastHeartbeatUtc = entry.LastHeartbeatUtc;
            if (entry.Inventory is { } inv)
            {
                device.Hostname = Trunc(inv.Hostname, 128);
                device.WindowsVersion = Trunc(inv.WindowsVersion, 128);
                device.Cpu = Trunc(inv.Cpu, 128);
                device.RamMegabytes = inv.RamMegabytes;
                device.Ipv4 = Trunc(inv.Ipv4, 64);
                device.AgentVersion = Trunc(inv.AgentVersion, 32);
            }
        }

        await UpsertDisklessCandidatesAsync(db, edge, report.DisklessCandidates, time, ct);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    public const int MaxDisklessCandidates = 200;

    /// <summary>Неподтверждённые бездисковые ПК из отчёта Edge → «Ожидают подтверждения» в Admin Web (D-018).</summary>
    private static async Task UpsertDisklessCandidatesAsync(ClubOsDbContext db, EdgeContext edge,
        IReadOnlyList<DisklessCandidate>? candidates, TimeProvider time, CancellationToken ct)
    {
        var seen = (candidates ?? [])
            .Select(c => (Candidate: c, HardwareId: HardwareIds.NormalizeMac(c.HardwareId)))
            .Where(x => x.HardwareId is not null)
            .DistinctBy(x => x.HardwareId)
            .Take(MaxDisklessCandidates)
            .ToList();
        if (seen.Count == 0)
        {
            return;
        }

        var ids = seen.Select(x => x.HardwareId!).ToList();
        var bound = await db.Devices.Where(x => x.TenantId == edge.TenantId && x.RevokedAtUtc == null &&
                                                x.HardwareId != null && ids.Contains(x.HardwareId))
            .Select(x => x.HardwareId!).ToListAsync(ct);
        var existing = await db.PendingDisklessDevices.Where(x => x.LocationId == edge.LocationId && ids.Contains(x.HardwareId))
            .ToDictionaryAsync(x => x.HardwareId, ct);
        var now = time.GetUtcNow();
        foreach (var (candidate, hardwareId) in seen.Where(x => !bound.Contains(x.HardwareId!)))
        {
            var macs = string.Join(",", candidate.MacAddresses.Select(HardwareIds.NormalizeMac).OfType<string>().Distinct().Take(16));
            if (existing.TryGetValue(hardwareId!, out var row))
            {
                // Отчёт приходит каждые несколько секунд: обновляем не чаще раза в минуту, чтобы не дёргать UI.
                if (candidate.LastSeenUtc - row.LastSeenUtc > TimeSpan.FromMinutes(1) || row.Hostname != Trunc(candidate.Hostname, 64))
                {
                    row.LastSeenUtc = candidate.LastSeenUtc > now ? now : candidate.LastSeenUtc;
                    row.Hostname = Trunc(candidate.Hostname, 64) ?? row.Hostname;
                    row.Ipv4 = Trunc(candidate.Ipv4, 45);
                    row.MacAddresses = macs;
                }

                continue;
            }

            db.PendingDisklessDevices.Add(new PendingDisklessDevice
            {
                Id = Ids.New("dlc"),
                TenantId = edge.TenantId,
                LocationId = edge.LocationId,
                EdgeId = edge.EdgeId,
                HardwareId = hardwareId!,
                MacAddresses = macs,
                Hostname = Trunc(candidate.Hostname, 64) ?? "PC",
                Ipv4 = Trunc(candidate.Ipv4, 45),
                Simulated = candidate.Simulated,
                FirstSeenUtc = candidate.FirstSeenUtc > now ? now : candidate.FirstSeenUtc,
                LastSeenUtc = candidate.LastSeenUtc > now ? now : candidate.LastSeenUtc
            });
        }
    }

    /// <summary>
    /// Enrollment устройства через Edge: Agent → Edge → Cloud. Токен одноразовый и привязан к локации Edge;
    /// CA назначает deviceId в сертификате. Приватный ключ агента сюда не передаётся.
    /// </summary>
    private static async Task<IResult> EnrollDevice(DeviceEnrollRequest request, HttpContext http, ClubOsDbContext db,
        IOptions<PkiOptions> pki,
        DevCertificateAuthority ca, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var edge = EdgeContext.From(http.User);
        // Токен привязан к локации: токен другой локации/tenant'а не находится и не «сжигается».
        var token = await ConsumeToken(db, request.EnrollmentToken, EnrollmentKinds.Device, time, ct,
            edge.TenantId, edge.LocationId);
        if (token?.ZoneId is null)
        {
            return Problems.Unauthorized("Enrollment-токен недействителен для этой локации.");
        }

        var deviceId = Ids.New("dev");
        IssuedCertificate cert;
        try
        {
            cert = ca.Issue(request.CertificateSigningRequestPem, deviceId, DevCertificateAuthority.RoleDevice,
                pki.Value.CertificateValidity);
        }
        catch (InvalidCsrException ex)
        {
            return Problems.Validation("invalid_csr", ex.Message);
        }

        var inv = request.Inventory;
        var now = time.GetUtcNow();
        token.UsedBySubjectId = deviceId;
        db.Devices.Add(new Device
        {
            Id = deviceId,
            TenantId = edge.TenantId,
            LocationId = edge.LocationId,
            ZoneId = token.ZoneId,
            DisplayName = token.DisplayName,
            Simulated = token.Simulated,
            Status = DeviceStatus.Offline,
            Hostname = Trunc(inv.Hostname, 128),
            WindowsVersion = Trunc(inv.WindowsVersion, 128),
            Cpu = Trunc(inv.Cpu, 128),
            RamMegabytes = inv.RamMegabytes,
            Ipv4 = Trunc(inv.Ipv4, 64),
            AgentVersion = Trunc(inv.AgentVersion, 32),
            CertificatePem = cert.CertificatePem,
            CertificateExpiresAtUtc = cert.ExpiresAtUtc,
            EnrolledAtUtc = now
        });
        audit.Write(edge.TenantId, edge.LocationId, edge.Actor, "device.enrolled", $"device:{deviceId}",
            AuditResults.Success, details: new
            {
                enrollmentTokenId = token.Id,
                name = token.DisplayName,
                token.Simulated,
                hostname = inv.Hostname
            });
        await db.SaveChangesAsync(ct);

        return Results.Ok(new DeviceEnrollResponse
        {
            DeviceId = deviceId,
            DeviceCertificatePem = cert.CertificatePem,
            CertificateExpiresAtUtc = cert.ExpiresAtUtc,
            DisplayName = token.DisplayName,
            ZoneId = token.ZoneId,
            Simulated = token.Simulated,
            CaCertificatePem = ca.Certificate.ExportCertificatePem()
        });
    }

    /// <summary>
    /// Атомарно «сжигает» одноразовый токен: UPDATE ... WHERE used_at IS NULL. Два параллельных
    /// enrollment с одним токеном не пройдут — второй UPDATE затронет 0 строк.
    /// </summary>
    private static async Task<EnrollmentToken?> ConsumeToken(ClubOsDbContext db, string? secret, string kind,
        TimeProvider time, CancellationToken ct, string? tenantId = null, string? locationId = null)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 128)
        {
            return null;
        }

        var hash = Ids.HashSecret(secret.Trim());
        var now = time.GetUtcNow();
        var query = db.EnrollmentTokens
            .Where(x => x.TokenHash == hash && x.Kind == kind && x.UsedAtUtc == null && x.ExpiresAtUtc > now);
        if (tenantId is not null)
        {
            query = query.Where(x => x.TenantId == tenantId && x.LocationId == locationId);
        }

        var updated = await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, now), ct);
        if (updated == 0)
        {
            return null;
        }

        return await db.EnrollmentTokens.SingleAsync(x => x.TokenHash == hash, ct);
    }

    private static string? Trunc(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
