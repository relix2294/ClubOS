using System.Net;
using ClubOS.EdgeController.Cloud;
using ClubOS.EdgeController.Storage;
using ClubOS.EdgeController.Workers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>Удаление устройств, отключение Edge и продление сертификатов (D-011).</summary>
[Collection(CloudCollection.Name)]
public class RevocationTests(CloudFixture cloud)
{
    private async Task<(HttpClient Owner, EdgeHost Edge, AgentHost Agent, string DeviceId, TestLocation Location)> StackAsync(
        int edgeRenewBeforeDays = 30, int agentRenewBeforeDays = 30)
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var edge = new EdgeHost(cloud, AuthAndTenancyTests.TempPath(), await owner.EdgeTokenAsync(location), edgeRenewBeforeDays);
        var agent = new AgentHost(edge, AuthAndTenancyTests.TempPath(), await owner.DeviceTokenAsync(location),
            agentRenewBeforeDays);
        var deviceId = await Wait.ForAsync(async () => agent.Runtime.DeviceId, what: "enrollment");
        await Wait.UntilAsync(async () =>
            (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}")).Str("status") == "Idle", what: "online");
        return (owner, edge, agent, deviceId, location);
    }

    [Fact]
    public async Task Revoked_device_disappears_everywhere_and_is_rejected_by_edge()
    {
        var (owner, edge, agent, deviceId, location) = await StackAsync();
        await using var _e = edge;
        await using var _a = agent;

        // С открытой сессией удалить нельзя.
        var session = (await (await owner.PostAsync($"/api/v1/devices/{deviceId}/sessions", null)).JsonAsync()).Str("sessionId");
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/v1/devices/{deviceId}/revoke", null)).StatusCode);
        await owner.PostAsync($"/api/v1/sessions/{session}/end", null);
        await Wait.UntilAsync(async () => (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}/sessions"))[0].Str("state") == "Ended",
            what: "сессия завершена");

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/devices/{deviceId}/revoke", null)).StatusCode);

        // Cloud: устройство скрыто, повторное удаление — 404.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/v1/devices/{deviceId}")).StatusCode);
        Assert.DoesNotContain((await owner.GetJsonAsync($"/api/v1/locations/{location.LocationId}/devices")).EnumerateArray(),
            d => d.Str("deviceId") == deviceId);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/v1/devices/{deviceId}/revoke", null)).StatusCode);

        // Edge забыл устройство (команда из очереди Cloud), запросы агента больше не принимаются.
        var store = edge.Service<EdgeStore>();
        await Wait.UntilAsync(async () => store.GetDevice(deviceId) is null, what: "Edge удалил устройство");
        var client = new ClubOS.Agent.Core.EdgeClient(edge.CreateClient(), agent.Identity, TimeProvider.System);
        var rejected = await Assert.ThrowsAsync<ClubOS.Agent.Core.EdgeRequestException>(() => client.HeartbeatAsync(
            new ClubOS.Contracts.HeartbeatMessage
            {
                DeviceId = deviceId,
                Status = ClubOS.Contracts.DeviceStatus.Idle,
                ClockUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None));
        Assert.Equal(401, rejected.Status);

        // История и аудит сохраняются.
        var revoked = await cloud.WithDb(async db => await db.Devices.SingleAsync(x => x.Id == deviceId));
        Assert.NotNull(revoked.RevokedAtUtc);
        var audit = await owner.GetJsonAsync($"/api/v1/audit?target=device:{deviceId}");
        Assert.Contains(audit.EnumerateArray(), a => a.Str("action") == "device.revoked");
    }

    [Fact]
    public async Task Revoked_edge_is_rejected_by_cloud()
    {
        var (owner, edge, agent, _, location) = await StackAsync();
        await using var _e = edge;
        await using var _a = agent;

        var edgeId = edge.Service<EdgeIdentityStore>().Current!.EdgeId;
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/edges/{edgeId}/revoke", null)).StatusCode);

        var ex = await Assert.ThrowsAsync<CloudRequestException>(() => edge.Service<CloudClient>().GetConfigAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);

        var me = await owner.GetJsonAsync("/api/v1/me");
        var loc = me.GetProperty("locations").EnumerateArray().Single(l => l.Str("locationId") == location.LocationId);
        Assert.Empty(loc.GetProperty("edges").EnumerateArray());
    }

    [Fact]
    public async Task Edge_and_device_certificates_are_renewed_before_expiry()
    {
        // Порог продления больше срока сертификата (90 дней) — продление «пора» сразу.
        var (owner, edge, agent, deviceId, _) = await StackAsync(edgeRenewBeforeDays: 100, agentRenewBeforeDays: 100);
        await using var _e = edge;
        await using var _a = agent;

        // Edge продлевает свой сертификат сам при старте воркера.
        var edgeId = edge.Service<EdgeIdentityStore>().Current!.EdgeId;
        await Wait.UntilAsync(async () => await cloud.WithDb(async db =>
            (await db.AuditEvents.AnyAsync(a => a.Action == "edge.certificate_renewed" && a.Target == $"edge:{edgeId}"))),
            what: "продление сертификата Edge");
        var edgeCert = await cloud.WithDb(async db => (await db.Edges.SingleAsync(x => x.Id == edgeId)).CertificatePem);
        Assert.Equal(edgeCert, edge.Service<EdgeIdentityStore>().Current!.CertificatePem);
        await edge.Service<CloudClient>().GetConfigAsync(CancellationToken.None); // новый сертификат принимается

        // Устройство: продление через Edge.
        var before = agent.Identity.Current!.CertificatePem;
        Assert.True(await agent.Runtime.RenewCertificateIfDueAsync(CancellationToken.None));
        var after = agent.Identity.Current!.CertificatePem;
        Assert.NotEqual(before, after);
        Assert.Equal(after, edge.Service<EdgeStore>().GetDevice(deviceId)!.CertificatePem);
        Assert.Equal(after, await cloud.WithDb(async db => (await db.Devices.SingleAsync(x => x.Id == deviceId)).CertificatePem));

        // Агент продолжает работать с новым сертификатом: команда проходит до конца.
        var command = await owner.PostJsonAsync($"/api/v1/devices/{deviceId}/commands",
            new { commandType = "ShowMessage", title = "После продления", message = "ok" });
        await Wait.UntilAsync(async () => (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}/commands"))
            .EnumerateArray().Any(c => c.Str("commandId") == command.Str("commandId") && c.Str("state") == "Succeeded"),
            what: "команда после продления");
        _ = typeof(CertificateRenewalWorker);
    }

    /// <summary>
    /// Ротация ключа при продлении (D-011): новый сертификат — на новый ключ; прежний ключ принимается, пока агент
    /// не подписал запрос новым (ответ мог потеряться), затем отклоняется. То же для Edge ↔ Cloud.
    /// </summary>
    [Fact]
    public async Task Renewal_rotates_keys_and_survives_a_lost_response()
    {
        var (owner, edge, agent, deviceId, _) = await StackAsync(edgeRenewBeforeDays: 100, agentRenewBeforeDays: 100);
        await using var _e = edge;
        await using var _a = agent;

        // Edge: продлил сертификат с новым ключом при старте; Cloud принял новый ключ и забыл прежний.
        var edgeIdentity = edge.Service<EdgeIdentityStore>();
        await Wait.UntilAsync(async () => await cloud.WithDb(async db =>
            await db.AuditEvents.AnyAsync(a => a.Action == "edge.certificate_renewed" && a.Target == $"edge:{edgeIdentity.Current!.EdgeId}")),
            what: "продление Edge");
        Assert.True(edgeIdentity.Key.Matches(edgeIdentity.Current!.CertificatePem));
        await edge.Service<CloudClient>().GetConfigAsync(CancellationToken.None);
        Assert.Null(await cloud.WithDb(async db => (await db.Edges.SingleAsync(x => x.Id == edgeIdentity.Current!.EdgeId)).PreviousCertificatePem));

        // Снимок «старого» агента до ротации: тот же ключ, что сейчас у устройства.
        var oldDir = AuthAndTenancyTests.TempPath();
        Directory.CreateDirectory(oldDir);
        var agentDir = Path.GetDirectoryName(agent.Identity.GetType().GetField("_keyPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(agent.Identity) as string)!;
        foreach (var file in new[] { "device.key", "identity.json" })
        {
            File.Copy(Path.Combine(agentDir, file), Path.Combine(oldDir, file));
        }

        var oldKeyClient = new ClubOS.Agent.Core.EdgeClient(edge.CreateClient(),
            new ClubOS.Agent.Core.AgentIdentityStore(oldDir, new ClubOS.Agent.Core.FileKeyProtector()), TimeProvider.System);
        var oldCert = agent.Identity.Current!.CertificatePem;

        // «Потерянный ответ»: продление на чужой новый ключ, подписанное текущим ключом, — ответ агент не сохранил.
        using var lost = ClubOS.Security.DeviceKey.Generate();
        var lostResponse = await oldKeyClient.RenewAsync(new ClubOS.Contracts.CertificateRenewRequest
        {
            CertificateSigningRequestPem = lost.CreateSigningRequestPem("clubos-device")
        }, CancellationToken.None);
        var device = edge.Service<EdgeStore>().GetDevice(deviceId)!;
        Assert.Equal(lostResponse.CertificatePem, device.CertificatePem);
        Assert.Equal(oldCert, device.PreviousCertificatePem);
        await oldKeyClient.GetCommandsAsync(0, CancellationToken.None); // старый ключ ещё принимается

        // Агент продлевает снова (старым ключом): «прежний» остаётся старым, текущий — его новый ключ.
        Assert.True(await agent.Runtime.RenewCertificateIfDueAsync(CancellationToken.None));
        Assert.True(agent.Identity.Key.Matches(agent.Identity.Current!.CertificatePem));
        Assert.False(agent.Identity.Key.Matches(oldCert));
        device = edge.Service<EdgeStore>().GetDevice(deviceId)!;
        Assert.Equal(agent.Identity.Current!.CertificatePem, device.CertificatePem);
        Assert.Equal(oldCert, device.PreviousCertificatePem);

        // Первый запрос новым ключом — прежний забыт; старый ключ больше не принимается.
        var command = await owner.PostJsonAsync($"/api/v1/devices/{deviceId}/commands",
            new { commandType = "ShowMessage", title = "Новый ключ", message = "ok" });
        await Wait.UntilAsync(async () => (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}/commands"))
            .EnumerateArray().Any(c => c.Str("commandId") == command.Str("commandId") && c.Str("state") == "Succeeded"),
            what: "команда с новым ключом");
        Assert.Null(edge.Service<EdgeStore>().GetDevice(deviceId)!.PreviousCertificatePem);
        var rejected = await Assert.ThrowsAsync<ClubOS.Agent.Core.EdgeRequestException>(() => oldKeyClient.GetCommandsAsync(0, CancellationToken.None));
        Assert.Equal((int)HttpStatusCode.Unauthorized, rejected.Status);
    }
}
