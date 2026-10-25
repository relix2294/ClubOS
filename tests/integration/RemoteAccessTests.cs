using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.Contracts;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>
/// Удалённый доступ (D-022) по живому контуру Cloud → Edge → агент: снимок экрана и список процессов
/// возвращаются результатом команды, завершение процесса, перезагрузка с защитой идущей сессии, права.
/// </summary>
[Collection(CloudCollection.Name)]
public class RemoteAccessTests(CloudFixture cloud)
{
    private static async Task<string> Code(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString()!;

    private static async Task<JsonElement> Finished(HttpClient owner, string commandId) =>
        (JsonElement)await Wait.ForAsync(async () =>
        {
            var c = await owner.GetJsonAsync($"/api/v1/commands/{commandId}");
            return c.Str("state") is "Succeeded" or "Failed" ? (object)c : null;
        }, what: $"итог команды {commandId}");

    [Fact]
    public async Task Screenshot_processes_kill_and_power_through_edge()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        await using var edge = new EdgeHost(cloud, AuthAndTenancyTests.TempPath(), await owner.EdgeTokenAsync(location));
        await using var agent = new AgentHost(edge, AuthAndTenancyTests.TempPath(), await owner.DeviceTokenAsync(location));
        var deviceId = await Wait.ForAsync(async () => agent.Runtime.DeviceId, what: "enrollment агента");
        await Wait.UntilAsync(async () =>
            (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}")).Str("status") == "Idle", what: "устройство online");
        var commands = $"/api/v1/devices/{deviceId}/commands";

        // Снимок экрана: результат в команде, в списке — только признак.
        var shot = await owner.PostJsonAsync(commands, new { commandType = "Screenshot" });
        var done = await Finished(owner, shot.Str("commandId"));
        Assert.Equal("Succeeded", done.Str("state"));
        var image = done.GetProperty("result");
        Assert.Equal("image/svg+xml", image.Str("mime"));
        Assert.Contains("PC-IT", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(image.Str("dataBase64"))));
        var listed = (await owner.GetJsonAsync(commands)).EnumerateArray().Single(c => c.Str("commandId") == shot.Str("commandId"));
        Assert.True(listed.GetProperty("hasResult").GetBoolean());
        Assert.False(listed.TryGetProperty("result", out var r) && r.ValueKind != JsonValueKind.Null);

        // Процессы → завершить игру.
        var list = await Finished(owner, (await owner.PostJsonAsync(commands, new { commandType = "ListProcesses" })).Str("commandId"));
        var game = list.GetProperty("result").GetProperty("processes").EnumerateArray().Single(p => p.Str("name") == "cs2");
        Assert.Equal("protected_process", await Code(await owner.PostAsJsonAsync(commands,
            new { commandType = "KillProcess", processId = 4, processName = "csrss" })));
        var kill = await Finished(owner, (await owner.PostJsonAsync(commands,
            new { commandType = "KillProcess", processId = game.GetProperty("processId").GetInt32(), processName = "cs2" })).Str("commandId"));
        Assert.Equal("Succeeded", kill.Str("state"));
        var again = await Finished(owner, (await owner.PostJsonAsync(commands, new { commandType = "ListProcesses" })).Str("commandId"));
        Assert.DoesNotContain(again.GetProperty("result").GetProperty("processes").EnumerateArray(), p => p.Str("name") == "cs2");

        // Перезагрузка: при идущей сессии — только принудительно.
        await owner.PostJsonAsync($"/api/v1/devices/{deviceId}/sessions", new { });
        Assert.Equal("session_active", await Code(await owner.PostAsJsonAsync(commands, new { commandType = "Reboot", delaySeconds = 30 })));
        Assert.Equal("invalid_payload", await Code(await owner.PostAsJsonAsync(commands, new { commandType = "Reboot", delaySeconds = 5000, force = true })));
        var reboot = await Finished(owner, (await owner.PostJsonAsync(commands, new { commandType = "Reboot", delaySeconds = 30, force = true })).Str("commandId"));
        Assert.Equal("Succeeded", reboot.Str("state"));

        // Оператор: удалённый доступ запрещён (обычные команды — можно).
        var email = $"remote-op-{Guid.NewGuid():N}@club.test";
        var created = await owner.PostJsonAsync("/api/v1/staff",
            new { email, displayName = "Оператор", role = "Operator", locationIds = new[] { location.LocationId } });
        var op = cloud.Factory.CreateClient();
        var temp = created.Str("temporaryPassword");
        var login = await op.PostJsonAsync("/api/v1/auth/login", new { email, password = temp });
        op.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Str("accessToken"));
        var changed = await op.PostJsonAsync("/api/v1/me/password", new { currentPassword = temp, newPassword = "Remote-Op-2026!" });
        op.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", changed.Str("accessToken"));
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync(commands, new { commandType = "Screenshot" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync($"/api/v1/commands/{shot.Str("commandId")}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await op.PostAsJsonAsync(commands, new { commandType = "ShowMessage", title = "Привет", message = "Тест" })).StatusCode);

        // Чужая организация не видит результат.
        var (otherEmail, otherPassword) = await cloud.CreateOrganizationWithOwnerAsync();
        var stranger = await cloud.LoginAsync(otherEmail, otherPassword);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/commands/{shot.Str("commandId")}")).StatusCode);
    }
}
