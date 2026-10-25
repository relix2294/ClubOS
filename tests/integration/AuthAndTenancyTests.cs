using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace ClubOS.Integration.Tests;

[Collection(CloudCollection.Name)]
public class AuthAndTenancyTests(CloudFixture cloud)
{
    [Fact]
    public async Task Login_with_seed_owner_succeeds_and_wrong_password_fails()
    {
        var client = await cloud.LoginAsync();
        var me = await client.GetJsonAsync("/api/v1/me");
        Assert.Equal("Demo Club Group", me.GetProperty("user").Str("organizationName"));
        var location = me.GetProperty("locations").EnumerateArray().First(l => l.Str("locationId") == "loc_dushanbe_pilot");
        Assert.Equal("Asia/Dushanbe", location.Str("timezone"));
        Assert.Equal("TJS", location.Str("currency"));
        Assert.Equal(["Standard", "VIP"], location.GetProperty("zones").EnumerateArray().Select(z => z.Str("name")).Order());

        var bad = await cloud.Anonymous().PostAsJsonAsync("/api/v1/auth/login",
            new { email = CloudFixture.OwnerEmail, password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoints_require_token()
    {
        var anon = cloud.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/edge/config")).StatusCode);
    }

    [Fact]
    public async Task Refresh_token_rotates_and_reuse_is_rejected()
    {
        var anon = cloud.Anonymous();
        var login = await anon.PostJsonAsync("/api/v1/auth/login",
            new { email = CloudFixture.OwnerEmail, password = CloudFixture.OwnerPassword });
        var refresh = login.Str("refreshToken");

        var rotated = await anon.PostJsonAsync("/api/v1/auth/refresh", new { refreshToken = refresh });
        Assert.NotEqual(refresh, rotated.Str("refreshToken"));

        // Повтор сразу после ротации — гонка двух вкладок: 401, но новая цепочка жива.
        var race = await anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, race.StatusCode);
        var next = await anon.PostJsonAsync("/api/v1/auth/refresh", new { refreshToken = rotated.Str("refreshToken") });

        // Повтор давно ротированного токена — признак кражи: отзывается вся цепочка.
        var oldHash = ClubOS.CloudApi.Infrastructure.Ids.HashSecret(refresh);
        await cloud.WithDb(async db =>
        {
            var stored = await db.RefreshTokens.SingleAsync(x => x.TokenHash == oldHash);
            stored.RevokedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
            return await db.SaveChangesAsync();
        });
        var theft = await anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, theft.StatusCode);
        var chained = await anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = next.Str("refreshToken") });
        Assert.Equal(HttpStatusCode.Unauthorized, chained.StatusCode);
    }

    [Fact]
    public async Task Stale_refresh_after_password_change_does_not_kill_the_new_session()
    {
        var owner = await cloud.LoginAsync();
        var email = $"stale-{Guid.NewGuid():N}@club.test";
        var created = await owner.PostJsonAsync("/api/v1/staff", new { email, displayName = "Stale", role = "Operator" });
        var temp = created.Str("temporaryPassword");
        var anon = cloud.Anonymous();
        var login = await anon.PostJsonAsync("/api/v1/auth/login", new { email, password = temp });

        var client = cloud.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.Str("accessToken"));
        var changed = await client.PostJsonAsync("/api/v1/me/password", new { currentPassword = temp, newPassword = "Stale-Session-2026!" });

        // Запрос, ушедший со старыми cookie до смены пароля: старый refresh отклонён…
        var stale = await anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = login.Str("refreshToken") });
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        // …а новая сессия продолжает работать.
        var fresh = await anon.PostJsonAsync("/api/v1/auth/refresh", new { refreshToken = changed.Str("refreshToken") });
        Assert.False(string.IsNullOrEmpty(fresh.Str("accessToken")));
    }

    [Fact]
    public async Task Other_tenant_cannot_see_or_command_devices()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        await using var edge = new EdgeHost(cloud, TempPath(), await owner.EdgeTokenAsync(location));
        await using var agent = new AgentHost(edge, TempPath(), await owner.DeviceTokenAsync(location));
        var deviceId = await Wait.ForAsync(async () => agent.Runtime.DeviceId, what: "enrollment агента");

        var (email, password) = await cloud.CreateOrganizationWithOwnerAsync();
        var stranger = await cloud.LoginAsync(email, password);

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/devices/{deviceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/locations/{location.LocationId}/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/api/v1/devices/{deviceId}/commands",
            new { commandType = "ShowMessage", title = "x", message = "y" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/v1/devices/{deviceId}/sessions", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync("/api/v1/enrollment-tokens/device",
            new { locationId = location.LocationId, zoneId = location.ZoneId, displayName = "hack" })).StatusCode);

        var audit = await stranger.GetJsonAsync($"/api/v1/audit?target=device:{deviceId}");
        Assert.Equal(0, audit.GetArrayLength());

        // Владелец видит устройство.
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/v1/devices/{deviceId}")).StatusCode);
    }

    [Fact]
    public async Task Enrollment_token_is_single_use()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var token = await owner.EdgeTokenAsync(location);
        await using var first = new EdgeHost(cloud, TempPath(), token);
        await Wait.UntilAsync(() => Task.FromResult(first.Service<ClubOS.EdgeController.Storage.EdgeIdentityStore>().IsEnrolled));

        var csr = ClubOS.Security.DeviceKey.Generate().CreateSigningRequestPem("x");
        var second = await cloud.Anonymous().PostAsJsonAsync("/api/v1/edge/enroll",
            new { enrollmentToken = token, certificateSigningRequestPem = csr });
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    public static string TempPath() => Path.Combine(Path.GetTempPath(), "clubos-it-" + Guid.NewGuid().ToString("N"));
}
