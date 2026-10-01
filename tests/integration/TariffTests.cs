using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.CloudApi.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>
/// Тарифы по времени суток и пакеты: настройка владельцем, снимок в сессии, расчёт на Edge, предоплата в кассе.
/// </summary>
[Collection(CloudCollection.Name)]
public class TariffTests(CloudFixture cloud)
{
    private static async Task<string> Code(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString()!;

    private static long Long(JsonElement e, string name) => e.GetProperty(name).GetInt64();

    private static JsonElement Zone(JsonElement me, string zoneId) =>
        me.GetProperty("locations").EnumerateArray().SelectMany(l => l.GetProperty("zones").EnumerateArray())
            .Single(z => z.Str("zoneId") == zoneId);

    /// <summary>Сутки целиком дешевле: две половины (период на весь день запрещён — это цена зоны).</summary>
    private static object[] AllDayCheap(long price) =>
    [
        new { days = 127, startMinute = 0, endMinute = 720, pricePerHourMinorUnits = price },
        new { days = 127, startMinute = 720, endMinute = 1440, pricePerHourMinorUnits = price }
    ];

    [Fact]
    public async Task Owner_configures_periods_and_packages_with_validation_and_audit()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var zoneUrl = $"/api/v1/zones/{location.ZoneId}";

        // Периоды: валидация каждого.
        var bad = await owner.PostAsJsonAsync($"{zoneUrl}/periods",
            new { periods = new[] { new { days = 0, startMinute = 0, endMinute = 60, pricePerHourMinorUnits = 100 } } });
        Assert.Equal("invalid_period", await Code(bad));
        var sameEnds = await owner.PostAsJsonAsync($"{zoneUrl}/periods",
            new { periods = new[] { new { days = 127, startMinute = 600, endMinute = 600, pricePerHourMinorUnits = 100 } } });
        Assert.Contains("совпадают", await sameEnds.Content.ReadAsStringAsync());
        var tooMany = await owner.PostAsJsonAsync($"{zoneUrl}/periods",
            new { periods = Enumerable.Range(0, 13).Select(i => new { days = 127, startMinute = i * 60, endMinute = i * 60 + 30, pricePerHourMinorUnits = 100 }) });
        Assert.Equal("too_many_periods", await Code(tooMany));
        var noPrice = await owner.PostAsJsonAsync($"{zoneUrl}/periods",
            new { periods = new[] { new { days = 127, startMinute = 0, endMinute = 60, pricePerHourMinorUnits = 0 } } });
        Assert.Equal("invalid_period", await Code(noPrice));

        var night = new { days = 127, startMinute = 22 * 60, endMinute = 8 * 60, pricePerHourMinorUnits = 6_000 };
        var weekend = new { days = 32 | 64, startMinute = 10 * 60, endMinute = 22 * 60, pricePerHourMinorUnits = 15_000 };
        var zone = await owner.PostJsonAsync($"{zoneUrl}/periods", new { periods = new object[] { weekend, night } });
        var periods = zone.GetProperty("periods").EnumerateArray().ToList();
        Assert.Equal(2, periods.Count);
        Assert.Equal(15_000, Long(periods[0], "pricePerHourMinorUnits")); // порядок сохранён — первый подходящий
        var version = await cloud.WithDb(db => db.Zones.Where(z => z.Id == location.ZoneId).Select(z => z.RuleVersion).SingleAsync());
        Assert.Equal(2, version);
        // Те же периоды ещё раз — без новой версии.
        await owner.PostJsonAsync($"{zoneUrl}/periods", new { periods = new object[] { weekend, night } });
        Assert.Equal(2, await cloud.WithDb(db => db.Zones.Where(z => z.Id == location.ZoneId).Select(z => z.RuleVersion).SingleAsync()));

        // Пакеты.
        var pkgUrl = $"{zoneUrl}/packages";
        Assert.Equal("invalid_package", await Code(await owner.PostAsJsonAsync(pkgUrl, new { name = "", durationMinutes = 60, priceMinorUnits = 100 })));
        Assert.Equal("invalid_package", await Code(await owner.PostAsJsonAsync(pkgUrl, new { name = "X", durationMinutes = 0, priceMinorUnits = 100 })));
        Assert.Equal("invalid_package", await Code(await owner.PostAsJsonAsync(pkgUrl,
            new { name = "X", durationMinutes = 60, priceMinorUnits = 100, availableFromMinute = 1320 })));
        var threeHours = await owner.PostJsonAsync(pkgUrl, new { name = "3 часа", durationMinutes = 180, priceMinorUnits = 25_000 });
        var nightPkg = await owner.PostJsonAsync(pkgUrl,
            new { name = "Ночь", durationMinutes = 600, priceMinorUnits = 30_000, availableFromMinute = 22 * 60, availableToMinute = 2 * 60 });
        Assert.Equal(1320, nightPkg.GetProperty("availableFromMinute").GetInt32());
        Assert.Equal("duplicate_package", await Code(await owner.PostAsJsonAsync(pkgUrl, new { name = "3 ЧАСА", durationMinutes = 60, priceMinorUnits = 1 })));

        // Отключение: оператор не видит пакет, владелец видит неактивным.
        var updated = await owner.PostJsonAsync($"/api/v1/packages/{nightPkg.Str("packageId")}",
            new { name = "Ночь", durationMinutes = 600, priceMinorUnits = 28_000, availableFromMinute = 1320, availableToMinute = 120, isActive = false });
        Assert.False(updated.GetProperty("isActive").GetBoolean());
        Assert.Equal(28_000, Long(updated, "priceMinorUnits"));

        var ownerZone = Zone(await owner.GetJsonAsync("/api/v1/me"), location.ZoneId);
        Assert.Equal(2, ownerZone.GetProperty("packages").GetArrayLength());
        Assert.Equal(2, ownerZone.GetProperty("periods").GetArrayLength());
        var email = $"tariff-op-{Guid.NewGuid():N}@club.test";
        var created = await owner.PostJsonAsync("/api/v1/staff",
            new { email, displayName = "Оператор", role = "Operator", locationIds = new[] { location.LocationId } });
        var op = cloud.Factory.CreateClient();
        var temp = created.Str("temporaryPassword");
        var login = await op.PostJsonAsync("/api/v1/auth/login", new { email, password = temp });
        op.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Str("accessToken"));
        var changed = await op.PostJsonAsync("/api/v1/me/password", new { currentPassword = temp, newPassword = "Tariff-Op-2026!" });
        op.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", changed.Str("accessToken"));
        var opZone = Zone(await op.GetJsonAsync("/api/v1/me"), location.ZoneId);
        Assert.Equal(threeHours.Str("packageId"), Assert.Single(opZone.GetProperty("packages").EnumerateArray()).Str("packageId"));
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"{zoneUrl}/periods", new { periods = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync(pkgUrl, new { name = "Y", durationMinutes = 60, priceMinorUnits = 1 })).StatusCode);

        // Чужая организация не видит зону.
        var (otherEmail, otherPassword) = await cloud.CreateOrganizationWithOwnerAsync();
        var stranger = await cloud.LoginAsync(otherEmail, otherPassword);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"{zoneUrl}/periods", new { periods = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.PostAsJsonAsync($"/api/v1/packages/{threeHours.Str("packageId")}", new { name = "Z", durationMinutes = 1, priceMinorUnits = 1 })).StatusCode);

        var actions = await cloud.WithDb(db => db.AuditEvents.Where(a => a.Target == $"zone:{location.ZoneId}").Select(a => a.Action).ToListAsync());
        Assert.Contains("tariff.periods_changed", actions);
        Assert.Contains("package.created", actions);
        Assert.Contains("package.updated", actions);
    }

    /// <summary>
    /// Живой сценарий: пакет из Cloud → Edge → предоплата пакета в кассе → ранний конец = цена пакета;
    /// сессия по периоду цены — итог Edge по цене периода и совпадает с пересчётом Cloud.
    /// </summary>
    [Fact]
    public async Task Package_and_period_sessions_are_billed_by_snapshot_on_edge()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var other = await owner.PostJsonAsync($"/api/v1/locations/{location.LocationId}/zones", new { name = "VIP", pricePerHourMinorUnits = 18_000 });
        var package = await owner.PostJsonAsync($"/api/v1/zones/{location.ZoneId}/packages",
            new { name = "2 часа", durationMinutes = 120, priceMinorUnits = 20_000 });
        var vipPackage = await owner.PostJsonAsync($"/api/v1/zones/{other.Str("zoneId")}/packages",
            new { name = "VIP час", durationMinutes = 60, priceMinorUnits = 15_000 });
        // Окно, в которое «сейчас» точно не попадает: час, который начнётся через 3 часа.
        var localNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5));
        var closedFrom = (localNow.Hour + 3) % 24 * 60;
        var closed = await owner.PostJsonAsync($"/api/v1/zones/{location.ZoneId}/packages",
            new { name = "Позже", durationMinutes = 60, priceMinorUnits = 1_000, availableFromMinute = closedFrom, availableToMinute = closedFrom + 60 });
        var inactive = await owner.PostJsonAsync($"/api/v1/zones/{location.ZoneId}/packages",
            new { name = "Архив", durationMinutes = 60, priceMinorUnits = 1_000, isActive = false });

        await using var edge = new EdgeHost(cloud, AuthAndTenancyTests.TempPath(), await owner.EdgeTokenAsync(location));
        await using var agent = new AgentHost(edge, AuthAndTenancyTests.TempPath(), await owner.DeviceTokenAsync(location));
        var deviceId = await Wait.ForAsync(async () => agent.Runtime.DeviceId, what: "enrollment агента");
        await Wait.UntilAsync(async () =>
            (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}")).Str("status") == "Idle", what: "устройство online");
        var sessions = $"/api/v1/devices/{deviceId}/sessions";

        Assert.Equal("package_zone_mismatch", await Code(await owner.PostAsJsonAsync(sessions, new { packageId = vipPackage.Str("packageId") })));
        Assert.Equal("package_not_available", await Code(await owner.PostAsJsonAsync(sessions, new { packageId = closed.Str("packageId") })));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync(sessions, new { packageId = inactive.Str("packageId") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await owner.PostAsJsonAsync(sessions, new { packageId = package.Str("packageId"), durationMinutes = 30 })).StatusCode);

        // Пакет: лимит из пакета, предоплата = цена пакета.
        var started = await owner.PostJsonAsync(sessions, new { packageId = package.Str("packageId") });
        var sessionId = started.Str("sessionId");
        Assert.Equal(120, started.GetProperty("durationMinutes").GetInt32());
        Assert.Equal("2 часа", started.Str("packageName"));
        Assert.Equal(300, started.GetProperty("utcOffsetMinutes").GetInt32()); // Asia/Dushanbe

        await owner.PostJsonAsync($"/api/v1/locations/{location.LocationId}/cash/shifts", new { openingCashMinorUnits = 0 });
        var desk = $"/api/v1/locations/{location.LocationId}/cash";
        var prepay = (JsonElement)await Wait.ForAsync(async () =>
        {
            var row = (await owner.GetJsonAsync(desk)).GetProperty("payable").EnumerateArray()
                .FirstOrDefault(p => p.Str("sessionId") == sessionId);
            return row.ValueKind == JsonValueKind.Object && row.Str("state") == "Active" ? (object)row : null;
        }, what: "предоплата пакета");
        Assert.Equal(20_000, Long(prepay, "dueMinorUnits"));
        await owner.PostJsonAsync($"/api/v1/sessions/{sessionId}/payments", new { amountMinorUnits = 20_000, method = "Cash" });

        // Ранний конец: цена пакета, переплаты нет.
        await owner.PostAsync($"/api/v1/sessions/{sessionId}/end", null);
        var ended = (JsonElement)await Wait.ForAsync(async () =>
        {
            var list = await owner.GetJsonAsync(sessions);
            var s = list.EnumerateArray().First(x => x.Str("sessionId") == sessionId);
            return s.Str("state") == "Ended" ? (object)s : null;
        }, what: "итог пакета");
        Assert.Equal(20_000, Long(ended, "totalMinorUnits"));
        Assert.Equal(120, ended.GetProperty("packageMinutes").GetInt32());
        Assert.DoesNotContain((await owner.GetJsonAsync(desk)).GetProperty("payable").EnumerateArray(), p => p.Str("sessionId") == sessionId);

        // Периоды: сутки по 60/час. Открытая сессия на ~1 минуту стоит 1,00, а не 2,00.
        await owner.PostJsonAsync($"/api/v1/zones/{location.ZoneId}/periods", new { periods = AllDayCheap(6_000) });
        var cheap = (await owner.PostJsonAsync(sessions, new { })).Str("sessionId");
        await Wait.UntilAsync(async () =>
            (await owner.GetJsonAsync(sessions)).EnumerateArray().First(x => x.Str("sessionId") == cheap).Str("state") == "Active",
            what: "сессия по периоду началась");
        await owner.PostAsync($"/api/v1/sessions/{cheap}/end", null);
        var cheapEnded = (JsonElement)await Wait.ForAsync(async () =>
        {
            var s = (await owner.GetJsonAsync(sessions)).EnumerateArray().First(x => x.Str("sessionId") == cheap);
            return s.Str("state") == "Ended" ? (object)s : null;
        }, what: "итог по периоду");
        Assert.Equal(100, Long(cheapEnded, "totalMinorUnits"));
        Assert.Equal(2, cheapEnded.GetProperty("periods").GetArrayLength());

        // Cloud хранит тот же снимок, что считал Edge: пересчёт совпадает.
        var row = await cloud.WithDb(db => db.Sessions.AsNoTracking().SingleAsync(s => s.Id == cheap));
        Assert.Equal(row.TotalMinorUnits,
            ClubOS.Contracts.BillingCalculator.CalculateMinorUnits(ClubOS.CloudApi.Api.Mapping.Snapshot(row), row.StartedAtUtc!.Value,
                row.EndedAtUtc!.Value - row.StartedAtUtc!.Value));
    }
}
