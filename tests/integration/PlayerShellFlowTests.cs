using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.Agent.Core.PlayerShell;
using ClubOS.Contracts;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>
/// Player Shell (M1): сессия с лимитом из Admin API → Edge (источник истины, таймер) → экран агента;
/// продление, завершение сотрудником и по лимиту, итог на экране и в Cloud.
/// </summary>
[Collection(CloudCollection.Name)]
public class PlayerShellFlowTests(CloudFixture cloud)
{
    private async Task<(HttpClient Owner, EdgeHost Edge, AgentHost Agent, string DeviceId)> StackAsync()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var edge = new EdgeHost(cloud, AuthAndTenancyTests.TempPath(), await owner.EdgeTokenAsync(location));
        var agent = new AgentHost(edge, AuthAndTenancyTests.TempPath(), await owner.DeviceTokenAsync(location));
        var deviceId = await Wait.ForAsync(async () => agent.Runtime.DeviceId, what: "enrollment агента");
        await Wait.UntilAsync(async () =>
            (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}")).Str("status") == "Idle", what: "устройство online");
        return (owner, edge, agent, deviceId);
    }

    private static async Task<JsonElement> Session(HttpClient owner, string deviceId, string sessionId) =>
        (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}/sessions")).EnumerateArray().First(s => s.Str("sessionId") == sessionId);

    private static async Task<JsonElement> WaitSession(HttpClient owner, string deviceId, string sessionId,
        Func<JsonElement, bool> condition, string what, TimeSpan? timeout = null) =>
        (JsonElement)await Wait.ForAsync(async () =>
        {
            var s = await Session(owner, deviceId, sessionId);
            return condition(s) ? (object)s : null;
        }, timeout, what);

    private static bool Close(DateTimeOffset? a, DateTimeOffset? b) =>
        a is { } x && b is { } y && (x - y).Duration() < TimeSpan.FromMilliseconds(1);

    private static Task<ShellState> WaitShell(AgentHost agent, Func<ShellState, bool> condition, string what,
        TimeSpan? timeout = null) =>
        Wait.ForAsync(async () => agent.Presenter.Shell is { } s && condition(s) ? s : null, timeout, what);

    [Fact]
    public async Task Limited_session_opens_pc_extends_and_ends_with_summary()
    {
        var (owner, edge, agent, deviceId) = await StackAsync();
        await using var _e = edge;
        await using var _a = agent;

        // Свободный ПК в режиме Enforced закрыт экраном клуба.
        var free = await WaitShell(agent, s => s.View == ShellView.Free && s.EdgeOnline, "экран «свободен»");
        Assert.Equal("PC-IT", free.DeviceName);

        var requested = await owner.PostJsonAsync($"/api/v1/devices/{deviceId}/sessions", new { durationMinutes = 30 });
        var sessionId = requested.Str("sessionId");
        Assert.Equal(30, requested.GetProperty("durationMinutes").GetInt32());

        var active = await WaitSession(owner, deviceId, sessionId,
            s => s.Str("state") == "Active" && s.GetProperty("plannedEndAtUtc").ValueKind == JsonValueKind.String, "Active");
        var startedAt = active.GetProperty("startedAtUtc").GetDateTimeOffset();
        var plannedEnd = active.GetProperty("plannedEndAtUtc").GetDateTimeOffset();
        Assert.Equal(TimeSpan.FromMinutes(30), plannedEnd - startedAt);

        var shell = await WaitShell(agent, s => s.View == ShellView.Session, "экран сессии");
        Assert.Equal(sessionId, shell.Session!.SessionId);
        Assert.True(Close(plannedEnd, shell.Session.PlannedEndAtUtc)); // PostgreSQL хранит микросекунды, Edge — такты

        // Продление: Cloud ставит команду, Edge двигает окончание, Cloud и агент узнают новое значение.
        var extend = await owner.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/extend", new { minutes = 15 });
        Assert.Equal(HttpStatusCode.Accepted, extend.StatusCode);
        await WaitSession(owner, deviceId, sessionId,
            s => Close(s.GetProperty("plannedEndAtUtc").GetDateTimeOffset(), plannedEnd.AddMinutes(15)), "продление в Cloud");
        await WaitShell(agent, s => Close(s.Session?.PlannedEndAtUtc, plannedEnd.AddMinutes(15)), "продление на экране");

        var audit = await owner.GetJsonAsync($"/api/v1/audit?target=device:{deviceId}");
        Assert.Contains(audit.EnumerateArray(), a => a.Str("action") == "session.extend" && a.Str("result") == "requested");
        Assert.Contains(audit.EnumerateArray(), a => a.Str("action") == "session.extended" && a.Str("result") == "success");

        await owner.PostAsync($"/api/v1/sessions/{sessionId}/end", null);
        var ended = await WaitSession(owner, deviceId, sessionId, s => s.Str("state") == "Ended", "Ended");
        Assert.Equal(SessionEndReasons.Staff, ended.Str("endReason"));
        Assert.Equal(200, ended.GetProperty("totalMinorUnits").GetInt64());

        var summary = await WaitShell(agent, s => s.View == ShellView.Ended, "итог на экране");
        Assert.Equal(sessionId, summary.Ended!.SessionId);
        Assert.Equal(200, summary.Ended.TotalMinorUnits);
        Assert.False(summary.Ended.Estimated);
        Assert.Equal(SessionEndReasons.Staff, summary.Ended.Reason);

        // Продлить завершённую нельзя.
        var late = await owner.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/extend", new { minutes = 15 });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task Edge_timer_ends_session_at_limit_even_without_wan()
    {
        var (owner, edge, agent, deviceId) = await StackAsync();
        await using var _e = edge;
        await using var _a = agent;

        var sessionId = (await owner.PostJsonAsync($"/api/v1/devices/{deviceId}/sessions", new { durationMinutes = 1 }))
            .Str("sessionId");
        await WaitSession(owner, deviceId, sessionId, s => s.Str("state") == "Active", "Active");
        await WaitShell(agent, s => s.View == ShellView.Session, "экран сессии");

        // Интернет клуба пропал: лимит всё равно отрабатывает на Edge, ПК закрывается.
        edge.Wan.Offline = true;
        var summary = await WaitShell(agent, s => s is { View: ShellView.Ended, Ended.Estimated: false }, "завершение по лимиту",
            TimeSpan.FromSeconds(90));
        Assert.Equal(SessionEndReasons.TimeLimit, summary.Ended!.Reason);
        Assert.Equal(200, summary.Ended.TotalMinorUnits); // ровно 1 минута
        Assert.Equal(TimeSpan.FromMinutes(1), summary.Ended.EndedAtUtc - summary.Ended.StartedAtUtc);
        Assert.Equal("Active", (await Session(owner, deviceId, sessionId)).Str("state")); // Cloud ещё не знает

        edge.Wan.Offline = false;
        var ended = await WaitSession(owner, deviceId, sessionId, s => s.Str("state") == "Ended", "синхронизация итога",
            TimeSpan.FromSeconds(60));
        Assert.Equal(SessionEndReasons.TimeLimit, ended.Str("endReason"));
        Assert.Equal(SessionEndReasons.TimerActor, ended.Str("endedBy"));
        Assert.Equal(200, ended.GetProperty("totalMinorUnits").GetInt64());
    }

    [Fact]
    public async Task Session_limit_validation()
    {
        var (owner, edge, agent, deviceId) = await StackAsync();
        await using var _e = edge;
        await using var _a = agent;

        foreach (var bad in new[] { 0, -1, SessionLimits.MaxDurationMinutes + 1 })
        {
            var response = await owner.PostAsJsonAsync($"/api/v1/devices/{deviceId}/sessions", new { durationMinutes = bad });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // Открытая сессия (без тела, как в M0) продлению не подлежит.
        var open = await (await owner.PostAsync($"/api/v1/devices/{deviceId}/sessions", null)).JsonAsync();
        var sessionId = open.Str("sessionId");
        Assert.Equal(JsonValueKind.Null, open.GetProperty("durationMinutes").ValueKind);
        await WaitSession(owner, deviceId, sessionId, s => s.Str("state") == "Active", "Active");

        var unlimited = await owner.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/extend", new { minutes = 30 });
        Assert.Equal(HttpStatusCode.Conflict, unlimited.StatusCode);
        var badMinutes = await owner.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/extend", new { minutes = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, badMinutes.StatusCode);
        var missing = await owner.PostAsJsonAsync("/api/v1/sessions/ses_missing/extend", new { minutes = 10 });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var shell = await WaitShell(agent, s => s.View == ShellView.Session, "экран открытой сессии");
        Assert.Null(shell.Session!.PlannedEndAtUtc);
        await owner.PostAsync($"/api/v1/sessions/{sessionId}/end", null);
    }
}
