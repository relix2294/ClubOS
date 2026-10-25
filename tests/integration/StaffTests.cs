using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>Персонал и права (ТЗ §8): роли, временные пароли, мгновенный отзыв доступа, изоляция tenant'ов.</summary>
[Collection(CloudCollection.Name)]
public class StaffTests(CloudFixture cloud)
{
    private static string NewEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20] + "@club.test";

    private async Task<(JsonElement User, string TempPassword)> CreateStaffAsync(HttpClient owner, string role)
    {
        var created = await owner.PostJsonAsync("/api/v1/staff",
            new { email = NewEmail(role.ToLowerInvariant()), displayName = $"Test {role}", role });
        return (created.GetProperty("user"), created.Str("temporaryPassword"));
    }

    private async Task<HttpClient> LoginRawAsync(string email, string password)
    {
        var client = cloud.Anonymous();
        var login = await client.PostJsonAsync("/api/v1/auth/login", new { email, password });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Str("accessToken"));
        return client;
    }

    /// <summary>Вход по временному паролю и смена его на постоянный.</summary>
    private async Task<(HttpClient Client, string Password)> ActivateAsync(JsonElement user, string temp)
    {
        var client = await LoginRawAsync(user.Str("email"), temp);
        const string password = "Strong-Pass-2026!";
        var changed = await client.PostJsonAsync("/api/v1/me/password", new { currentPassword = temp, newPassword = password });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", changed.Str("accessToken"));
        return (client, password);
    }

    [Fact]
    public async Task Temporary_password_allows_only_me_and_password_change()
    {
        var owner = await cloud.LoginAsync();
        var (user, temp) = await CreateStaffAsync(owner, "Operator");
        Assert.True(user.GetProperty("mustChangePassword").GetBoolean());

        var client = await LoginRawAsync(user.Str("email"), temp);
        var me = await client.GetJsonAsync("/api/v1/me");
        Assert.True(me.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(0, me.GetProperty("user").GetProperty("permissions").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/v1/locations/loc_dushanbe_pilot/devices")).StatusCode);

        var weak = await client.PostAsJsonAsync("/api/v1/me/password", new { currentPassword = temp, newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var (active, _) = await ActivateAsync(user, temp);
        Assert.Equal(HttpStatusCode.OK, (await active.GetAsync("/api/v1/locations/loc_dushanbe_pilot/devices")).StatusCode);
        var meAfter = await active.GetJsonAsync("/api/v1/me");
        Assert.False(meAfter.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());

        // Старый токен (до смены пароля) больше не принимается.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Operator_and_admin_permissions_are_enforced_by_backend()
    {
        var owner = await cloud.LoginAsync();
        var (opUser, opTemp) = await CreateStaffAsync(owner, "Operator");
        var (adminUser, adminTemp) = await CreateStaffAsync(owner, "Admin");
        var (op, _) = await ActivateAsync(opUser, opTemp);
        var (admin, _) = await ActivateAsync(adminUser, adminTemp);

        var enrollment = new { locationId = "loc_dushanbe_pilot", name = "Edge-X" };
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/v1/enrollment-tokens/edge", enrollment)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/enrollment-tokens/edge", enrollment)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/v1/staff")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/staff")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.PostAsJsonAsync("/api/v1/staff", new { email = NewEmail("x"), displayName = "X", role = "Owner" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/api/v1/audit")).StatusCode);
        var perms = (await op.GetJsonAsync("/api/v1/me")).GetProperty("user").GetProperty("permissions")
            .EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("sessions.manage", perms);
        Assert.DoesNotContain("staff.manage", perms);
    }

    [Fact]
    public async Task Deactivation_and_role_change_take_effect_immediately()
    {
        var owner = await cloud.LoginAsync();
        var (adminUser, temp) = await CreateStaffAsync(owner, "Admin");
        var (admin, password) = await ActivateAsync(adminUser, temp);
        var id = adminUser.Str("userId");
        var enrollment = new { locationId = "loc_dushanbe_pilot", name = "Edge-Y" };
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/enrollment-tokens/edge", enrollment)).StatusCode);

        // Понижение до оператора: прежний токен отклоняется сразу, не через 15 минут.
        await owner.PostJsonAsync($"/api/v1/staff/{id}/role", new { role = "Operator" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/v1/me")).StatusCode);
        var relogged = await LoginRawAsync(adminUser.Str("email"), password);
        Assert.Equal(HttpStatusCode.Forbidden, (await relogged.PostAsJsonAsync("/api/v1/enrollment-tokens/edge", enrollment)).StatusCode);

        // Отключение: токен и вход перестают работать.
        await owner.PostJsonAsync($"/api/v1/staff/{id}/deactivate");
        Assert.Equal(HttpStatusCode.Unauthorized, (await relogged.GetAsync("/api/v1/me")).StatusCode);
        var denied = await cloud.Anonymous().PostAsJsonAsync("/api/v1/auth/login", new { email = adminUser.Str("email"), password });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        await owner.PostJsonAsync($"/api/v1/staff/{id}/activate");
        Assert.Equal(HttpStatusCode.OK, (await LoginRawAsync(adminUser.Str("email"), password).ContinueWith(t => t.Result.GetAsync("/api/v1/me")).Unwrap()).StatusCode);
    }

    [Fact]
    public async Task Reset_password_revokes_sessions_and_requires_change()
    {
        var owner = await cloud.LoginAsync();
        var (opUser, temp) = await CreateStaffAsync(owner, "Operator");
        var (op, oldPassword) = await ActivateAsync(opUser, temp);

        var reset = await owner.PostJsonAsync($"/api/v1/staff/{opUser.Str("userId")}/reset-password");
        var newTemp = reset.Str("temporaryPassword");
        Assert.Equal(HttpStatusCode.Unauthorized, (await op.GetAsync("/api/v1/me")).StatusCode);

        var oldLogin = await cloud.Anonymous().PostAsJsonAsync("/api/v1/auth/login",
            new { email = opUser.Str("email"), password = oldPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var fresh = await LoginRawAsync(opUser.Str("email"), newTemp);
        Assert.True((await fresh.GetJsonAsync("/api/v1/me")).GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
    }

    [Fact]
    public async Task Last_owner_cannot_be_demoted_or_deactivated_and_owner_cannot_deactivate_self()
    {
        var (email, password) = await cloud.CreateOrganizationWithOwnerAsync();
        var owner = await cloud.LoginAsync(email, password);
        var ownerId = (await owner.GetJsonAsync("/api/v1/me")).GetProperty("user").Str("userId");

        Assert.Equal(HttpStatusCode.Conflict,
            (await owner.PostAsJsonAsync($"/api/v1/staff/{ownerId}/role", new { role = "Admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/v1/staff/{ownerId}/deactivate", null)).StatusCode);
    }

    [Fact]
    public async Task Staff_of_other_organization_is_invisible()
    {
        var owner = await cloud.LoginAsync();
        var (demoUser, _) = await CreateStaffAsync(owner, "Operator");

        var (email, password) = await cloud.CreateOrganizationWithOwnerAsync();
        var stranger = await cloud.LoginAsync(email, password);
        var list = await stranger.GetJsonAsync("/api/v1/staff");
        Assert.DoesNotContain(list.EnumerateArray(), u => u.Str("userId") == demoUser.Str("userId"));
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.PostAsync($"/api/v1/staff/{demoUser.Str("userId")}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.PostAsync($"/api/v1/staff/{demoUser.Str("userId")}/reset-password", null)).StatusCode);
    }

    [Fact]
    public async Task Duplicate_email_and_invalid_role_are_rejected()
    {
        var owner = await cloud.LoginAsync();
        var email = NewEmail("dup");
        await owner.PostJsonAsync("/api/v1/staff", new { email, displayName = "Dup", role = "Operator" });
        Assert.Equal(HttpStatusCode.Conflict,
            (await owner.PostAsJsonAsync("/api/v1/staff", new { email, displayName = "Dup2", role = "Operator" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await owner.PostAsJsonAsync("/api/v1/staff", new { email = NewEmail("r"), displayName = "R", role = "Root" })).StatusCode);
    }
}
