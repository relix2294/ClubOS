using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>Сквозной контур M0: Admin API → Cloud → Edge → Agent → результат → audit (ТЗ §25.3).</summary>
[Collection(CloudCollection.Name)]
public class EndToEndTests(CloudFixture cloud)
{
    private async Task<(HttpClient Owner, EdgeHost Edge, AgentHost Agent, string DeviceId, TestLocation Location)> StackAsync()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var edge = new EdgeHost(cloud, AuthAndTenancyTests.TempPath(), await owner.EdgeTokenAsync(location));
        var agent = new AgentHost(edge, AuthAndTenancyTests.TempPath(), await owner.DeviceTokenAsync(location));
        var deviceId = await Wait.ForAsync(async () => agent.Runtime.DeviceId, what: "enrollment агента");
        await Wait.UntilAsync(async () =>
            (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}")).Str("status") == "Idle", what: "устройство online");
        return (owner, edge, agent, deviceId, location);
    }

    [Fact]
    public async Task ShowMessage_goes_through_full_lifecycle_and_audit()
    {
        var (owner, edge, agent, deviceId, _) = await StackAsync();
        await using var _e = edge;
        await using var _a = agent;

        var device = await owner.GetJsonAsync($"/api/v1/devices/{deviceId}");
        Assert.True(device.GetProperty("simulated").GetBoolean());
        Assert.False(string.IsNullOrEmpty(device.GetProperty("inventory").Str("hostname")));

        var issued = await owner.PostJsonAsync($"/api/v1/devices/{deviceId}/commands",
            new { commandType = "ShowMessage", title = "Привет", message = "Интеграционный тест" });
        var commandId = issued.Str("commandId");
        Assert.Equal("Queued", issued.Str("state"));

        await Wait.UntilAsync(async () =>
            (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}/commands"))[0].Str("state") == "Succeeded", what: "Succeeded");
        Assert.Equal(1, agent.Presenter.Shown);

        var audit = await owner.GetJsonAsync($"/api/v1/audit?target=device:{deviceId}");
        var results = audit.EnumerateArray().Where(a => a.Str("action") == "command.ShowMessage").Select(a => a.Str("result")).ToList();
        Assert.Contains("requested", results);
        Assert.Contains("delivered", results);
        Assert.Contains("success", results);

        // Повтор запроса с тем же commandId не создаёт вторую команду (CMD-002).
        var again = await owner.PostAsJsonAsync($"/api/v1/devices/{deviceId}/commands",
            new { commandType = "ShowMessage", title = "Привет", message = "Интеграционный тест", commandId });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        await Task.Delay(1500);
        Assert.Equal(1, agent.Presenter.Shown);
    }

    [Fact]
    public async Task Invalid_or_unsupported_commands_are_rejected()
    {
        var (owner, edge, agent, deviceId, _) = await StackAsync();
        await using var _e = edge;
        await using var _a = agent;

        var tooLong = await owner.PostAsJsonAsync($"/api/v1/devices/{deviceId}/commands",
            new { commandType = "ShowMessage", title = "t", message = new string('x', 501) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var unknown = await owner.PostAsJsonAsync($"/api/v1/devices/{deviceId}/commands",
            new { commandType = "RunPowerShell", title = "t", message = "m" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Session_from_admin_is_started_and_ended_by_edge_idempotently()
    {
        var (owner, edge, agent, deviceId, _) = await StackAsync();
        await using var _e = edge;
        await using var _a = agent;

        var requested = await (await owner.PostAsync($"/api/v1/devices/{deviceId}/sessions", null)).JsonAsync();
        var sessionId = requested.Str("sessionId");
        Assert.Equal("Created", requested.Str("state"));
        Assert.Equal(12_000, requested.GetProperty("pricePerHourMinorUnits").GetInt64());

        var conflict = await owner.PostAsync($"/api/v1/devices/{deviceId}/sessions", null);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        await Wait.UntilAsync(async () => (await Session(owner, deviceId, sessionId)).Str("state") == "Active", what: "Active");
        Assert.Equal("Active", (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}")).Str("status") is var s && s == "Active"
            ? "Active"
            : await WaitStatus(owner, deviceId, "Active"));

        await owner.PostAsync($"/api/v1/sessions/{sessionId}/end", null);
        await owner.PostAsync($"/api/v1/sessions/{sessionId}/end", null); // повтор — без второго EndSession

        var ended = await Wait.ForAsync(async () =>
        {
            var x = await Session(owner, deviceId, sessionId);
            return x.Str("state") == "Ended" ? (object)x : null;
        }, what: "Ended");
        var endedSession = (JsonElement)ended;
        Assert.Equal(200, endedSession.GetProperty("totalMinorUnits").GetInt64()); // < 1 мин → 2,00 TJS

        var endCommands = await cloud.WithDb(async db => (await db.EdgeOutbox
            .Where(x => x.Kind == EdgeCommandKind.EndSession).Select(x => x.PayloadJson).ToListAsync())
            .Count(p => p.Contains(sessionId)));
        Assert.Equal(1, endCommands);
    }

    [Fact]
    public async Task Edge_keeps_working_offline_and_syncs_after_wan_returns()
    {
        var (owner, edge, agent, deviceId, _) = await StackAsync();
        await using var _a = agent;
        await using var _e = edge;

        edge.Wan.Offline = true;
        var local = await edge.StartLocalSessionAsync(deviceId);
        var sessionId = local.Str("sessionId");
        await Task.Delay(1500);

        var end = await edge.LocalAdmin().PostAsJsonAsync($"/local/v1/sessions/{sessionId}/end", new { actor = "it" });
        end.EnsureSuccessStatusCode();
        var status = await edge.LocalAdmin().GetJsonAsync("/local/v1/status");
        Assert.True(status.GetProperty("pendingOutboxEvents").GetInt32() >= 2);
        Assert.DoesNotContain((await owner.GetJsonAsync($"/api/v1/devices/{deviceId}/sessions")).EnumerateArray(),
            x => x.Str("sessionId") == sessionId);

        edge.Wan.Offline = false;
        var synced = await Wait.ForAsync(async () =>
        {
            var x = (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}/sessions")).EnumerateArray()
                .FirstOrDefault(s => s.Str("sessionId") == sessionId);
            return x.ValueKind == JsonValueKind.Object && x.Str("state") == "Ended" ? (object)x : null;
        }, what: "синхронизация offline-сессии");

        var session = (JsonElement)synced;
        Assert.Equal("edge", session.Str("origin"));
        Assert.Equal(200, session.GetProperty("totalMinorUnits").GetInt64());
        await Wait.UntilAsync(async () =>
            (await edge.LocalAdmin().GetJsonAsync("/local/v1/status")).GetProperty("pendingOutboxEvents").GetInt32() == 0);
    }

    [Fact]
    public async Task Edge_restart_does_not_lose_active_session()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var edgePath = AuthAndTenancyTests.TempPath();
        var edge = new EdgeHost(cloud, edgePath, await owner.EdgeTokenAsync(location));
        var agent = new AgentHost(edge, AuthAndTenancyTests.TempPath(), await owner.DeviceTokenAsync(location));
        var deviceId = await Wait.ForAsync(async () => agent.Runtime.DeviceId);
        await Wait.UntilAsync(() => Task.FromResult(File.Exists(Path.Combine(edgePath, "identity.json"))));
        await Wait.UntilAsync(async () => (await edge.LocalAdmin().GetJsonAsync("/local/v1/devices")).GetArrayLength() == 1);
        await Task.Delay(1500); // конфигурация тарифов закэширована

        var sessionId = (await edge.StartLocalSessionAsync(deviceId)).Str("sessionId");
        await agent.DisposeAsync();
        await edge.DisposeAsync();

        // Новый процесс Edge с тем же каталогом данных — без enrollment-токена.
        await using var restarted = new EdgeHost(cloud, edgePath, enrollmentToken: null);
        var active = await restarted.LocalAdmin().GetJsonAsync("/local/v1/sessions?active=true");
        Assert.Equal(sessionId, Assert.Single(active.EnumerateArray()).Str("sessionId"));

        var ended = await restarted.LocalAdmin().PostJsonAsync($"/local/v1/sessions/{sessionId}/end", new { actor = "it" });
        Assert.Equal("Ended", ended.Str("outcome"));
        await Wait.UntilAsync(async () => (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}/sessions"))
            .EnumerateArray().Any(s => s.Str("sessionId") == sessionId && s.Str("state") == "Ended"), what: "sync после рестарта");
    }

    [Fact]
    public async Task Expired_command_is_marked_expired_and_never_executed()
    {
        var (owner, edge, agent, deviceId, _) = await StackAsync();
        await using var _e = edge;
        agent.Runtime.Paused = true; // агент не забирает команды
        await Task.Delay(TimeSpan.FromSeconds(3)); // дождаться завершения текущего long-poll (2 с)

        var issued = await owner.PostJsonAsync($"/api/v1/devices/{deviceId}/commands",
            new { commandType = "ShowMessage", title = "t", message = "m", ttlSeconds = 10 });
        var commandId = issued.Str("commandId");

        await cloud.WithDb(async db =>
        {
            await db.DeviceCommands.Where(c => c.Id == commandId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAtUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
            return 0;
        });
        await cloud.Factory.Services.GetRequiredService<CommandExpiryService>().ExpireOnceAsync(CancellationToken.None);

        var command = (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}/commands"))[0];
        Assert.Equal("Expired", command.Str("state"));

        // Даже если Edge получил её раньше, по истечении срока агенту она не выдаётся.
        await Task.Delay(TimeSpan.FromSeconds(11));
        agent.Runtime.Paused = false;
        await Task.Delay(3000);
        Assert.Equal(0, agent.Presenter.Shown);
        await agent.DisposeAsync();
    }

    private static async Task<JsonElement> Session(HttpClient owner, string deviceId, string sessionId) =>
        (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}/sessions")).EnumerateArray().First(s => s.Str("sessionId") == sessionId);

    private static async Task<string> WaitStatus(HttpClient owner, string deviceId, string status)
    {
        await Wait.UntilAsync(async () => (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}")).Str("status") == status,
            what: $"статус {status}");
        return status;
    }
}
