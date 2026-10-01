using System.Net;
using System.Security.Cryptography.X509Certificates;
using ClubOS.Agent.Core;
using ClubOS.Contracts;
using ClubOS.EdgeController.Storage;
using ClubOS.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>
/// PKI (D-002): реестр выпущенных сертификатов, CRL с подписью CA (анонимно), CRL на Edge, mTLS агентов и режим
/// «только с сертификатом».
/// </summary>
[Collection(CloudCollection.Name)]
public class PkiTests(CloudFixture cloud)
{
    private async Task<CertificateRevocationList> CrlAsync(string caPem)
    {
        var response = await cloud.Anonymous().GetAsync("/api/v1/pki/crl");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pkix-crl", response.Content.Headers.ContentType!.MediaType);
        using var ca = X509Certificate2.CreateFromPem(caPem);
        return CrlReader.Read(await response.Content.ReadAsByteArrayAsync(), ca);
    }

    private static async Task<int> HeartbeatStatusAsync(HttpClient http, AgentIdentityStore identity)
    {
        try
        {
            await new EdgeClient(http, identity, TimeProvider.System).HeartbeatAsync(new HeartbeatMessage
            {
                DeviceId = identity.Current!.DeviceId,
                Status = DeviceStatus.Idle,
                ClockUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);
            return 200;
        }
        catch (EdgeRequestException ex)
        {
            return ex.Status;
        }
    }

    [Fact]
    public async Task Issued_certificates_are_recorded_and_revocation_reaches_the_signed_crl_and_the_edge()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        await using var edge = new EdgeHost(cloud, AuthAndTenancyTests.TempPath(), await owner.EdgeTokenAsync(location));
        await using var agent = new AgentHost(edge, AuthAndTenancyTests.TempPath(), await owner.DeviceTokenAsync(location),
            presentClientCertificate: true);
        var deviceId = await Wait.ForAsync(async () => agent.Runtime.DeviceId, what: "enrollment");
        await Wait.UntilAsync(async () => (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}")).Str("status") == "Idle",
            what: "устройство online по mTLS");
        var edgeId = edge.Service<EdgeIdentityStore>().Current!.EdgeId;
        var caPem = edge.Service<EdgeIdentityStore>().Current!.CaCertificatePem;
        var tls = edge.Service<EdgeTlsCertificateStore>();
        await Wait.UntilAsync(async () => tls.Certificate is not null, what: "TLS-сертификат Edge");

        // Реестр: сертификаты устройства, Edge и TLS API агентов записаны при выпуске.
        var device = await cloud.WithDb(db => db.Certificates.AsNoTracking().SingleAsync(x => x.SubjectId == deviceId));
        Assert.Equal(DevCertificateAuthority.RoleDevice, device.Role);
        Assert.Equal(DevCertificateAuthority.SerialOf(agent.Identity.Current!.CertificatePem), device.Serial);
        var edgeRoles = await cloud.WithDb(db => db.Certificates.AsNoTracking().Where(x => x.SubjectId == edgeId)
            .Select(x => x.Role).ToListAsync());
        Assert.Contains(DevCertificateAuthority.RoleEdge, edgeRoles);
        Assert.Contains(DevCertificateAuthority.RoleEdgeServer, edgeRoles);

        // CRL: анонимно, подписан CA; Edge забирает его сам.
        Assert.False((await CrlAsync(caPem)).IsRevoked(agent.Identity.Current!.CertificatePem));
        var crl = edge.Service<EdgeCrlStore>();
        await Wait.UntilAsync(async () => crl.Current is not null, what: "CRL на Edge");

        // Удаление устройства: его сертификат в CRL Cloud и на Edge.
        var deviceCert = agent.Identity.Current!.CertificatePem;
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/devices/{deviceId}/revoke", null)).StatusCode);
        Assert.True((await CrlAsync(caPem)).IsRevoked(deviceCert));
        await Wait.UntilAsync(async () => crl.Current!.IsRevoked(deviceCert), what: "отзыв устройства в CRL на Edge");
        var record = await cloud.WithDb(db => db.Certificates.AsNoTracking().SingleAsync(x => x.SubjectId == deviceId));
        Assert.Equal((int)X509RevocationReason.CessationOfOperation, record.RevocationReason);

        // Отключение Edge: клиентский сертификат Edge и TLS-сертификат API агентов отозваны.
        var edgeCert = edge.Service<EdgeIdentityStore>().Current!.CertificatePem;
        var tlsCert = tls.Certificate!;
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/edges/{edgeId}/revoke", null)).StatusCode);
        var afterEdge = await CrlAsync(caPem);
        Assert.True(afterEdge.IsRevoked(edgeCert));
        Assert.True(afterEdge.IsRevoked(tlsCert));
        Assert.True(afterEdge.IsRevoked(deviceCert));
        var audit = await owner.GetJsonAsync($"/api/v1/audit?target=edge:{edgeId}");
        var revoked = audit.EnumerateArray().Single(a => a.Str("action") == "edge.revoked");
        Assert.Contains("certificatesRevoked", revoked.GetRawText());
    }

    [Fact]
    public async Task Required_client_certificate_rejects_agents_without_it_or_with_another_devices_certificate()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        await using var edge = new EdgeHost(cloud, AuthAndTenancyTests.TempPath(), await owner.EdgeTokenAsync(location),
            requireClientCertificate: true);

        // С сертификатом: регистрация, online, продление с ротацией ключа и команда после него.
        await using var withCert = new AgentHost(edge, AuthAndTenancyTests.TempPath(), await owner.DeviceTokenAsync(location),
            renewBeforeDays: 100, presentClientCertificate: true);
        var deviceId = await Wait.ForAsync(async () => withCert.Runtime.DeviceId, what: "enrollment с сертификатом");
        await Wait.UntilAsync(async () => (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}")).Str("status") == "Idle",
            what: "online с сертификатом");
        var before = withCert.Identity.Current!.CertificatePem;
        Assert.True(await withCert.Runtime.RenewCertificateIfDueAsync(CancellationToken.None));
        Assert.NotEqual(before, withCert.Identity.Current!.CertificatePem);
        var command = await owner.PostJsonAsync($"/api/v1/devices/{deviceId}/commands",
            new { commandType = "ShowMessage", title = "mTLS", message = "ok" });
        await Wait.UntilAsync(async () => (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}/commands"))
            .EnumerateArray().Any(c => c.Str("commandId") == command.Str("commandId") && c.Str("state") == "Succeeded"),
            what: "команда после продления по mTLS");

        // Без сертификата: регистрация открыта, но остальные запросы отклоняются — ПК не выходит online.
        await using var without = new AgentHost(edge, AuthAndTenancyTests.TempPath(), await owner.DeviceTokenAsync(location));
        var otherId = await Wait.ForAsync(async () => without.Runtime.DeviceId, what: "enrollment без сертификата");
        Assert.Equal(401, await HeartbeatStatusAsync(edge.CreateClient(), without.Identity));
        await Task.Delay(3000);
        Assert.Equal("Offline", (await owner.GetJsonAsync($"/api/v1/devices/{otherId}")).Str("status"));

        // Свой токен, но сертификат другого ПК — отказ; свой сертификат — принят.
        var foreign = new HttpClient(new TestClientCertificate.Sender(withCert.Identity.ClientCertificate, edge.CreateHandler()))
        {
            BaseAddress = edge.CreateClient().BaseAddress
        };
        Assert.Equal(401, await HeartbeatStatusAsync(foreign, without.Identity));
        var own = new HttpClient(new TestClientCertificate.Sender(without.Identity.ClientCertificate, edge.CreateHandler()))
        {
            BaseAddress = edge.CreateClient().BaseAddress
        };
        Assert.Equal(200, await HeartbeatStatusAsync(own, without.Identity));
    }
}
