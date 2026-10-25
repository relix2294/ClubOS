using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.CloudApi.Domain;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>Права по локациям (ТЗ §8, D-013) и управление локациями/тарифами владельцем.</summary>
[Collection(CloudCollection.Name)]
public class LocationAccessTests(CloudFixture cloud)
{
    private async Task<string> AddDeviceAsync(TestLocation location, string name)
    {
        var id = $"dev_{Guid.NewGuid():N}";
        await cloud.WithDb(async db =>
        {
            db.Devices.Add(new Device
            {
                Id = id,
                TenantId = location.OrganizationId,
                LocationId = location.LocationId,
                ZoneId = location.ZoneId,
                DisplayName = name,
                CertificatePem = "test",
                Status = ClubOS.Contracts.DeviceStatus.Idle,
                LastHeartbeatUtc = DateTimeOffset.UtcNow,
                EnrolledAtUtc = DateTimeOffset.UtcNow
            });
            return await db.SaveChangesAsync();
        });
        return id;
    }

    private async Task<(string UserId, HttpClient Client)> OperatorAsync(HttpClient owner, object locationIds)
    {
        var email = $"loc-op-{Guid.NewGuid():N}@club.test";
        var created = await owner.PostJsonAsync("/api/v1/staff",
            new { email, displayName = "Оператор локации", role = "Operator", locationIds });
        var temp = created.Str("temporaryPassword");
        var client = cloud.Factory.CreateClient();
        var login = await client.PostJsonAsync("/api/v1/auth/login", new { email, password = temp });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Str("accessToken"));
        var changed = await client.PostJsonAsync("/api/v1/me/password", new { currentPassword = temp, newPassword = "Location-Op-2026!" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", changed.Str("accessToken"));
        return (created.GetProperty("user").Str("userId"), client);
    }

    [Fact]
    public async Task Operator_sees_only_assigned_locations_and_changes_apply_immediately()
    {
        var owner = await cloud.LoginAsync();
        var a = await cloud.CreateLocationAsync();
        var b = await cloud.CreateLocationAsync();
        var deviceA = await AddDeviceAsync(a, "PC-A");
        var deviceB = await AddDeviceAsync(b, "PC-B");
        var (userId, op) = await OperatorAsync(owner, new[] { a.LocationId });

        var staff = (await owner.GetJsonAsync("/api/v1/staff")).EnumerateArray().Single(x => x.Str("userId") == userId);
        Assert.False(staff.GetProperty("allLocations").GetBoolean());
        Assert.Equal([a.LocationId], staff.GetProperty("locationIds").EnumerateArray().Select(x => x.GetString()));

        var me = await op.GetJsonAsync("/api/v1/me");
        Assert.Equal([a.LocationId], me.GetProperty("locations").EnumerateArray().Select(l => l.Str("locationId")));

        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync($"/api/v1/locations/{a.LocationId}/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync($"/api/v1/devices/{deviceA}")).StatusCode);

        // Чужая локация той же организации выглядит как несуществующая.
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/v1/locations/{b.LocationId}/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/v1/devices/{deviceB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/v1/devices/{deviceB}/commands")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/v1/devices/{deviceB}/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.PostAsJsonAsync($"/api/v1/devices/{deviceB}/commands",
            new { commandType = "ShowMessage", title = "t", message = "m" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.PostAsync($"/api/v1/devices/{deviceB}/sessions", null)).StatusCode);

        // Сессия в чужой локации (запущена владельцем) недоступна и по id.
        var sessionB = (await (await owner.PostAsync($"/api/v1/devices/{deviceB}/sessions", null)).JsonAsync()).Str("sessionId");
        Assert.Equal(HttpStatusCode.NotFound, (await op.PostAsync($"/api/v1/sessions/{sessionB}/end", null)).StatusCode);

        // Аудит: только события своей локации, без событий организации.
        await owner.PostJsonAsync($"/api/v1/devices/{deviceA}/commands", new { commandType = "ShowMessage", title = "A", message = "a" });
        var audit = (await op.GetJsonAsync("/api/v1/audit?limit=500")).EnumerateArray().ToList();
        Assert.NotEmpty(audit);
        Assert.DoesNotContain(audit, x => x.Str("target") == $"device:{deviceB}");
        Assert.DoesNotContain(audit, x => x.Str("action").StartsWith("staff.", StringComparison.Ordinal));

        // Владелец открывает все локации — действует со следующего запроса, без повторного входа.
        var updated = await owner.PostJsonAsync($"/api/v1/staff/{userId}/locations", new { allLocations = true });
        Assert.True(updated.GetProperty("allLocations").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync($"/api/v1/devices/{deviceB}")).StatusCode);

        // И снова ограничивает — уже только B.
        await owner.PostJsonAsync($"/api/v1/staff/{userId}/locations", new { allLocations = false, locationIds = new[] { b.LocationId } });
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/v1/devices/{deviceA}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync($"/api/v1/devices/{deviceB}")).StatusCode);
        await owner.PostAsync($"/api/v1/sessions/{sessionB}/end", null);
    }

    [Fact]
    public async Task Location_access_validation()
    {
        var owner = await cloud.LoginAsync();
        var a = await cloud.CreateLocationAsync();
        var (userId, _) = await OperatorAsync(owner, new[] { a.LocationId });

        var empty = await owner.PostAsJsonAsync($"/api/v1/staff/{userId}/locations", new { allLocations = false, locationIds = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var foreign = await cloud.CreateLocationAsync(await ForeignOrganizationAsync());
        var other = await owner.PostAsJsonAsync($"/api/v1/staff/{userId}/locations",
            new { allLocations = false, locationIds = new[] { foreign.LocationId } });
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode); // чужая организация — как несуществующая

        var ownerId = (await owner.GetJsonAsync("/api/v1/me")).GetProperty("user").Str("userId");
        var self = await owner.PostAsJsonAsync($"/api/v1/staff/{ownerId}/locations", new { allLocations = false, locationIds = new[] { a.LocationId } });
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
    }

    private async Task<string> ForeignOrganizationAsync()
    {
        var (email, password) = await cloud.CreateOrganizationWithOwnerAsync();
        var stranger = await cloud.LoginAsync(email, password);
        return (await stranger.GetJsonAsync("/api/v1/me")).GetProperty("user").Str("organizationId");
    }

    [Fact]
    public async Task Owner_creates_location_and_changes_tariff_with_new_rule_version()
    {
        var owner = await cloud.LoginAsync();
        var name = $"Клуб {Guid.NewGuid():N}"[..20];
        var created = await owner.PostJsonAsync("/api/v1/locations", new
        {
            name,
            timezone = "Asia/Dushanbe",
            currency = "TJS",
            zones = new[] { new { name = "Standard", pricePerHourMinorUnits = 10_000L }, new { name = "VIP", pricePerHourMinorUnits = 18_000L } }
        });
        var locationId = created.Str("locationId");

        var location = (await owner.GetJsonAsync("/api/v1/me")).GetProperty("locations").EnumerateArray()
            .Single(l => l.Str("locationId") == locationId);
        var vip = location.GetProperty("zones").EnumerateArray().Single(z => z.Str("name") == "VIP");
        Assert.Equal(18_000, vip.GetProperty("pricePerHourMinorUnits").GetInt64());

        var changed = await owner.PostJsonAsync($"/api/v1/zones/{vip.Str("zoneId")}", new { name = "VIP", pricePerHourMinorUnits = 20_000L });
        Assert.Equal(20_000, changed.GetProperty("pricePerHourMinorUnits").GetInt64());
        var version = await cloud.WithDb(async db => (await db.Zones.FindAsync(vip.Str("zoneId")))!.RuleVersion);
        Assert.Equal(2, version);

        var added = await owner.PostJsonAsync($"/api/v1/locations/{locationId}/zones", new { name = "Bootcamp", pricePerHourMinorUnits = 8_000L });
        Assert.Equal("Bootcamp", added.Str("name"));
        Assert.Equal(HttpStatusCode.Conflict,
            (await owner.PostAsJsonAsync($"/api/v1/locations/{locationId}/zones", new { name = "vip", pricePerHourMinorUnits = 1L })).StatusCode);

        foreach (var bad in new object[]
                 {
                     new { name = "", timezone = "Asia/Dushanbe", currency = "TJS", zones = new[] { new { name = "S", pricePerHourMinorUnits = 1L } } },
                     new { name = "X1", timezone = "Dushanbe time", currency = "TJS", zones = new[] { new { name = "S", pricePerHourMinorUnits = 1L } } },
                     new { name = "X2", timezone = "Asia/Dushanbe", currency = "tjs", zones = new[] { new { name = "S", pricePerHourMinorUnits = 1L } } },
                     new { name = "X3", timezone = "Asia/Dushanbe", currency = "TJS", zones = Array.Empty<object>() },
                     new { name = "X4", timezone = "Asia/Dushanbe", currency = "TJS", zones = new[] { new { name = "S", pricePerHourMinorUnits = 0L } } },
                     new { name = "X5", timezone = "Asia/Dushanbe", currency = "TJS", zones = new[] { new { name = "S", pricePerHourMinorUnits = 1L }, new { name = "s", pricePerHourMinorUnits = 1L } } }
                 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/locations", bad)).StatusCode);
        }

        // Тарифы — только владелец.
        var (_, op) = await OperatorAsync(owner, JsonDocument.Parse("null").RootElement);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await op.PostAsJsonAsync($"/api/v1/zones/{vip.Str("zoneId")}", new { name = "VIP", pricePerHourMinorUnits = 1L })).StatusCode);
    }
}
