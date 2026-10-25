using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.CloudApi.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using CloudApiAssembly = ClubOS.CloudApi.Auth.AuthOptions;

namespace ClubOS.Integration.Tests;

/// <summary>MFA (TOTP) персонала: настройка, вход в два шага, повтор кода, коды восстановления, сброс (ТЗ §8).</summary>
[Collection(CloudCollection.Name)]
public class MfaTests(CloudFixture cloud)
{
    private const string Password = "Mfa-Strong-Pass-2026!";

    /// <summary>Сотрудник с постоянным паролем; клиент хоста <paramref name="host"/> с его токеном.</summary>
    private async Task<(string Email, string UserId, HttpClient Client)> StaffAsync(
        WebApplicationFactory<CloudApiAssembly> host, string role)
    {
        var owner = await cloud.LoginAsync();
        var email = $"mfa-{Guid.NewGuid():N}@club.test";
        var created = await owner.PostJsonAsync("/api/v1/staff", new { email, displayName = "MFA", role });
        var temp = created.Str("temporaryPassword");
        var client = host.CreateClient();
        var login = await client.PostJsonAsync("/api/v1/auth/login", new { email, password = temp });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Str("accessToken"));
        var changed = await client.PostJsonAsync("/api/v1/me/password", new { currentPassword = temp, newPassword = Password });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", changed.Str("accessToken"));
        return (email, created.GetProperty("user").Str("userId"), client);
    }

    private HttpClient WithToken(string accessToken)
    {
        var client = cloud.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private static string CodeFor(string secret, int stepOffset = 0) =>
        Totp.Code(Base32.Decode(secret), Totp.StepAt(DateTimeOffset.UtcNow) + stepOffset);

    private static async Task<(string Secret, JsonElement Enabled)> EnableAsync(HttpClient client)
    {
        var setup = await client.PostJsonAsync("/api/v1/me/mfa/setup");
        var secret = setup.Str("secret");
        Assert.StartsWith("otpauth://totp/ClubOS:", setup.Str("otpAuthUri"));
        var enabled = await client.PostJsonAsync("/api/v1/me/mfa/enable", new { code = CodeFor(secret) });
        return (secret, enabled);
    }

    [Fact]
    public async Task Enable_then_login_requires_second_factor_and_rejects_replay()
    {
        var api = cloud.Anonymous();
        var (email, _, client) = await StaffAsync(cloud.Factory, "Operator");

        var wrong = await client.PostAsJsonAsync("/api/v1/me/mfa/enable", new { code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode); // нет setup — нечего подтверждать

        var (secret, enabled) = await EnableAsync(client);
        var codes = enabled.GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Equal(10, codes.Count);
        Assert.All(codes, c => Assert.Matches("^[a-z2-9]{5}-[a-z2-9]{5}$", c));
        Assert.True(enabled.GetProperty("session").GetProperty("user").GetProperty("mfaEnabled").GetBoolean());

        // Старый токен (выдан без второго фактора) отозван.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);

        // Вход: пароль → challenge, без токенов.
        var first = await api.PostJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.True(first.GetProperty("mfaRequired").GetBoolean());
        Assert.False(first.TryGetProperty("accessToken", out _));
        var mfaToken = first.Str("mfaToken");

        // Код текущего шага уже использован при включении — повтор отклоняется.
        var replay = await api.PostAsJsonAsync("/api/v1/auth/mfa", new { mfaToken, code = CodeFor(secret) });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // Код следующего шага (часы телефона спешат на 30 с) принимается.
        var ok = await api.PostJsonAsync("/api/v1/auth/mfa", new { mfaToken, code = CodeFor(secret, +1) });
        Assert.False(string.IsNullOrEmpty(ok.Str("accessToken")));

        // Challenge одноразовый.
        var again = await api.PostAsJsonAsync("/api/v1/auth/mfa", new { mfaToken, code = CodeFor(secret, +1) });
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);

        // Код восстановления работает один раз.
        var second = await api.PostJsonAsync("/api/v1/auth/login", new { email, password = Password });
        var viaRecovery = await api.PostJsonAsync("/api/v1/auth/mfa",
            new { mfaToken = second.Str("mfaToken"), recoveryCode = codes[0].ToUpperInvariant().Replace("-", " ") });
        Assert.False(string.IsNullOrEmpty(viaRecovery.Str("accessToken")));
        var third = await api.PostJsonAsync("/api/v1/auth/login", new { email, password = Password });
        var reused = await api.PostAsJsonAsync("/api/v1/auth/mfa",
            new { mfaToken = third.Str("mfaToken"), recoveryCode = codes[0] });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);

        var status = await WithToken(viaRecovery.Str("accessToken")).GetJsonAsync("/api/v1/me/mfa");
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(9, status.GetProperty("recoveryCodesLeft").GetInt32());

        // Секрет в БД не хранится открытым текстом.
        var stored = await cloud.WithDb(async db => (await db.Users.FindAsync(ok.GetProperty("user").Str("userId")))!.MfaSecretProtected);
        Assert.StartsWith("v1:", stored);
        Assert.DoesNotContain(secret, stored);
    }

    [Fact]
    public async Task Challenge_allows_five_attempts()
    {
        var api = cloud.Anonymous();
        var (email, _, client) = await StaffAsync(cloud.Factory, "Operator");
        var (secret, _) = await EnableAsync(client);

        var login = await api.PostJsonAsync("/api/v1/auth/login", new { email, password = Password });
        var mfaToken = login.Str("mfaToken");
        for (var i = 0; i < 5; i++)
        {
            var wrongCode = ((int.Parse(CodeFor(secret, +1), System.Globalization.CultureInfo.InvariantCulture) + 500_000) % 1_000_000)
                .ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            var bad = await api.PostAsJsonAsync("/api/v1/auth/mfa", new { mfaToken, code = wrongCode });
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }

        var late = await api.PostAsJsonAsync("/api/v1/auth/mfa", new { mfaToken, code = CodeFor(secret, +1) });
        Assert.Equal(HttpStatusCode.Unauthorized, late.StatusCode); // challenge сгорел — нужно войти заново
    }

    [Fact]
    public async Task Owner_resets_mfa_of_staff_member()
    {
        var api = cloud.Anonymous();
        var (email, userId, client) = await StaffAsync(cloud.Factory, "Operator");
        await EnableAsync(client);

        var owner = await cloud.LoginAsync();
        var reset = await owner.PostJsonAsync($"/api/v1/staff/{userId}/reset-mfa");
        Assert.False(reset.GetProperty("mfaEnabled").GetBoolean());

        var login = await api.PostJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.False(string.IsNullOrEmpty(login.Str("accessToken"))); // снова вход по паролю

        var self = await owner.PostAsync($"/api/v1/staff/{(await owner.GetJsonAsync("/api/v1/me")).GetProperty("user").Str("userId")}/reset-mfa", null);
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
    }

    [Fact]
    public async Task Voluntary_mfa_can_be_disabled_with_password_and_code()
    {
        var api = cloud.Anonymous();
        var (email, _, client) = await StaffAsync(cloud.Factory, "Operator");
        var (secret, enabled) = await EnableAsync(client);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", enabled.GetProperty("session").Str("accessToken"));

        var wrongPassword = await client.PostAsJsonAsync("/api/v1/me/mfa/disable",
            new { password = "wrong-password-1", code = CodeFor(secret, +1) });
        Assert.Equal(HttpStatusCode.BadRequest, wrongPassword.StatusCode);

        var disabled = await client.PostJsonAsync("/api/v1/me/mfa/disable", new { password = Password, code = CodeFor(secret, +1) });
        Assert.False(disabled.GetProperty("user").GetProperty("mfaEnabled").GetBoolean());
        var login = await api.PostJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.False(string.IsNullOrEmpty(login.Str("accessToken")));
    }

    [Fact]
    public async Task Required_mfa_restricts_token_until_setup()
    {
        // Отдельный хост Cloud с обязательной MFA для Admin (та же БД).
        await using var strict = cloud.Factory.WithWebHostBuilder(b => b.UseSetting("Auth:MfaRequiredRoles", "Owner,Admin"));
        var (_, _, client) = await StaffAsync(strict, "Admin");

        var me = await client.GetJsonAsync("/api/v1/me");
        Assert.True(me.GetProperty("user").GetProperty("mfaSetupRequired").GetBoolean());
        Assert.Empty(me.GetProperty("user").GetProperty("permissions").EnumerateArray());
        var location = me.GetProperty("locations")[0].Str("locationId");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/locations/{location}/devices")).StatusCode);

        var (_, enabled) = await EnableAsync(client);
        var session = enabled.GetProperty("session");
        Assert.False(session.GetProperty("user").GetProperty("mfaSetupRequired").GetBoolean());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Str("accessToken"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/locations/{location}/devices")).StatusCode);

        // Обязательную MFA нельзя отключить самому.
        var disable = await client.PostAsJsonAsync("/api/v1/me/mfa/disable", new { password = Password, code = "000000" });
        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);
    }
}
