using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.Contracts;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>
/// Бездисковые ПК (D-018): загрузка без токена → «Ожидают подтверждения» → подтверждение в Admin API → ПК на связи
/// с сертификатом локального CA Edge; перезагрузка = новый ключ и тот же deviceId; «двойник» с тем же MAC
/// отклоняется, пока оригинал на связи; работа без интернета; удалённый ПК снова ждёт подтверждения.
/// </summary>
[Collection(CloudCollection.Name)]
public class DisklessTests(CloudFixture cloud)
{
    private const string Mac = "02:AA:00:00:00:01";

    private static async Task<JsonElement?> Candidate(HttpClient owner, TestLocation location, string mac) =>
        (await owner.GetJsonAsync($"/api/v1/locations/{location.LocationId}/diskless-candidates")).EnumerateArray()
        .Cast<JsonElement?>().FirstOrDefault(c => c!.Value.Str("mac") == mac);

    private static Task<JsonElement> WaitCandidate(HttpClient owner, TestLocation location, string mac) =>
        Wait.ForAsync(async () => await Candidate(owner, location, mac) is { } c ? (object)c : null, what: $"ПК {mac} в «Ожидают подтверждения»")
            .ContinueWith(t => (JsonElement)t.Result);

    [Fact]
    public async Task Diskless_pc_waits_for_approval_then_boots_with_edge_certificate_and_survives_reboots()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        await using var edge = new EdgeHost(cloud, AuthAndTenancyTests.TempPath(), await owner.EdgeTokenAsync(location));

        // 1. ПК из общего образа загрузился: токена нет, Edge его не знает.
        var first = new AgentHost(edge, AuthAndTenancyTests.TempPath(), null, disklessMac: Mac);
        var candidate = await WaitCandidate(owner, location, Mac);
        Assert.Equal("02AA00000001", candidate.Str("hardwareId"));
        Assert.Null(first.Runtime.DeviceId);
        await Wait.UntilAsync(async () => first.Presenter.Shell?.Notice?.Contains("ожидает подтверждения") == true,
            what: "подсказка на экране клуба");

        // 2. Администратор подтверждает: имя и зона.
        var approved = await owner.PostJsonAsync($"/api/v1/diskless-candidates/{candidate.Str("candidateId")}/approve",
            new { displayName = "DL-01", zoneId = location.ZoneId });
        var deviceId = approved.Str("deviceId");
        await Wait.UntilAsync(async () => first.Runtime.DeviceId == deviceId, what: "ПК получил сертификат у Edge");
        await Wait.UntilAsync(async () => (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}")).Str("status") == "Idle",
            what: "ПК на связи");
        var view = await owner.GetJsonAsync($"/api/v1/devices/{deviceId}");
        Assert.Equal(Mac, view.Str("hardwareId"));
        Assert.Null(await Candidate(owner, location, Mac));
        Assert.Null(first.Presenter.Shell?.Notice);
        var firstCertificate = first.Identity.Current!.CertificatePem;

        // Команда доходит: Edge принимает подпись ключом бездискового ПК.
        await SendMessageAsync(owner, deviceId, "boot-1");

        // 3. «Двойник» с тем же MAC, пока оригинал на связи, — отказ.
        await using (var twin = new AgentHost(edge, AuthAndTenancyTests.TempPath(), null, disklessMac: Mac))
        {
            await Wait.UntilAsync(async () => twin.Presenter.Shell?.Notice?.Contains("уже на связи") == true, what: "двойник отклонён");
            Assert.Null(twin.Runtime.DeviceId);
        }

        // 4. Перезагрузка: диск сброшен (новый каталог данных), новый ключ, тот же ПК.
        await first.DisposeAsync();
        await using var rebooted = new AgentHost(edge, AuthAndTenancyTests.TempPath(), null, disklessMac: Mac);
        await Wait.UntilAsync(async () => rebooted.Runtime.DeviceId == deviceId, TimeSpan.FromSeconds(30), "перезагрузка");
        Assert.NotEqual(firstCertificate, rebooted.Identity.Current!.CertificatePem);
        await SendMessageAsync(owner, deviceId, "boot-2");

        var audit = await owner.GetJsonAsync($"/api/v1/audit?target=device:{deviceId}");
        Assert.Contains(audit.EnumerateArray(), a => a.Str("action") == "device.diskless_approved");

        // 5. Без интернета клуба ПК всё равно загружается: сертификат выдаёт Edge.
        edge.Wan.Offline = true;
        await rebooted.DisposeAsync();
        await using (var offline = new AgentHost(edge, AuthAndTenancyTests.TempPath(), null, disklessMac: Mac))
        {
            await Wait.UntilAsync(async () => offline.Runtime.DeviceId == deviceId, TimeSpan.FromSeconds(30), "загрузка без WAN");
            await Wait.UntilAsync(async () => offline.Presenter.Shell?.EdgeOnline == true, what: "связь с Edge");
        }

        edge.Wan.Offline = false;

        // 6. ПК удалён в Admin Web: при следующей загрузке снова «Ожидают подтверждения».
        var revoke = await owner.PostAsync($"/api/v1/devices/{deviceId}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        await using var afterRevoke = new AgentHost(edge, AuthAndTenancyTests.TempPath(), null, disklessMac: Mac);
        var again = await WaitCandidate(owner, location, Mac);
        Assert.Null(afterRevoke.Runtime.DeviceId);

        // Отклонить: запись исчезает (ПК вернётся при следующей загрузке).
        var dismiss = await owner.PostAsync($"/api/v1/diskless-candidates/{again.Str("candidateId")}/dismiss", null);
        Assert.Equal(HttpStatusCode.NoContent, dismiss.StatusCode);
    }

    [Fact]
    public async Task Approval_requires_permission_valid_zone_and_own_tenant()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        await using var edge = new EdgeHost(cloud, AuthAndTenancyTests.TempPath(), await owner.EdgeTokenAsync(location));
        await using var agent = new AgentHost(edge, AuthAndTenancyTests.TempPath(), null, disklessMac: "02:AA:00:00:00:02");
        var candidate = await WaitCandidate(owner, location, "02:AA:00:00:00:02");
        var url = $"/api/v1/diskless-candidates/{candidate.Str("candidateId")}/approve";

        var badZone = await owner.PostAsJsonAsync(url, new { displayName = "DL-02", zoneId = "zone_other" });
        Assert.Equal(HttpStatusCode.BadRequest, badZone.StatusCode);
        var noName = await owner.PostAsJsonAsync(url, new { displayName = " ", zoneId = location.ZoneId });
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);

        var (otherEmail, otherPassword) = await cloud.CreateOrganizationWithOwnerAsync();
        var other = await cloud.LoginAsync(otherEmail, otherPassword);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(url, new { displayName = "X", zoneId = location.ZoneId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/locations/{location.LocationId}/diskless-candidates")).StatusCode);
    }

    private static async Task SendMessageAsync(HttpClient owner, string deviceId, string title)
    {
        var command = await owner.PostJsonAsync($"/api/v1/devices/{deviceId}/commands",
            new { commandType = "ShowMessage", title, message = "Бездисковый ПК" });
        var commandId = command.Str("commandId");
        await Wait.UntilAsync(async () => (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}/commands"))
            .EnumerateArray().Any(c => c.Str("commandId") == commandId && c.Str("state") == "Succeeded"), what: $"команда {title}");
    }
}
