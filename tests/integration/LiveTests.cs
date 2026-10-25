using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>Live-поток Admin Web (SSE, DEVIATIONS D-008): подсказки об изменениях, изоляция tenant, отзыв доступа.</summary>
[Collection(CloudCollection.Name)]
public class LiveTests(CloudFixture cloud)
{
    /// <summary>Читает SSE-поток в фоне и копит события.</summary>
    private sealed class LiveReader : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;
        private readonly HttpResponseMessage _response;

        private LiveReader(HttpResponseMessage response)
        {
            _response = response;
            _run = Task.Run(ReadAsync);
        }

        public ConcurrentQueue<(string Event, JsonElement Data)> Events { get; } = new();

        public bool Closed { get; private set; }

        public static async Task<LiveReader> OpenAsync(HttpClient client)
        {
            var response = await client.GetAsync("/api/v1/live", HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            var reader = new LiveReader(response);
            await Wait.UntilAsync(async () => reader.Events.Any(e => e.Event == "ready"), what: "ready");
            return reader;
        }

        public Task<(string Event, JsonElement Data)> WaitForAsync(Func<string, JsonElement, bool> match, string what,
            TimeSpan? timeout = null) =>
            Wait.ForAsync<Tuple<string, JsonElement>>(async () =>
                Events.FirstOrDefault(e => match(e.Event, e.Data)) is { Event: not null } found
                    ? Tuple.Create(found.Event, found.Data)
                    : null, timeout, what).ContinueWith(t => (t.Result.Item1, t.Result.Item2), TaskScheduler.Default);

        public bool Has(Func<string, JsonElement, bool> match) => Events.Any(e => match(e.Event, e.Data));

        private async Task ReadAsync()
        {
            try
            {
                await using var stream = await _response.Content.ReadAsStreamAsync(_cts.Token);
                using var reader = new StreamReader(stream);
                string? eventName = null;
                while (await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    if (line.StartsWith("event: ", StringComparison.Ordinal))
                    {
                        eventName = line["event: ".Length..];
                    }
                    else if (line.StartsWith("data: ", StringComparison.Ordinal))
                    {
                        Events.Enqueue((eventName ?? "message", JsonDocument.Parse(line["data: ".Length..]).RootElement.Clone()));
                    }
                    else if (line.Length == 0)
                    {
                        eventName = null;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
            {
            }

            Closed = true;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _response.Dispose();
            await _run;
        }
    }

    private static bool Change(string evt, JsonElement data, string topic, string? deviceId = null) =>
        evt == "change" && data.Str("topic") == topic &&
        (deviceId is null || (data.TryGetProperty("deviceId", out var d) && d.GetString() == deviceId));

    [Fact]
    public async Task Changes_are_pushed_to_staff_of_the_same_tenant_only()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        await using var edge = new EdgeHost(cloud, AuthAndTenancyTests.TempPath(), await owner.EdgeTokenAsync(location));
        await using var agent = new AgentHost(edge, AuthAndTenancyTests.TempPath(), await owner.DeviceTokenAsync(location));
        var deviceId = await Wait.ForAsync(async () => agent.Runtime.DeviceId, what: "enrollment");

        var (otherEmail, otherPassword) = await cloud.CreateOrganizationWithOwnerAsync();
        var stranger = await cloud.LoginAsync(otherEmail, otherPassword);

        await using var live = await LiveReader.OpenAsync(owner);
        await using var foreign = await LiveReader.OpenAsync(stranger);

        // Устройство вышло на связь — событие устройства (через отчёт Edge или монитор присутствия).
        await live.WaitForAsync((e, d) => Change(e, d, "devices", deviceId), "devices");

        // Команда: создание и жизненный цикл.
        var command = await owner.PostJsonAsync($"/api/v1/devices/{deviceId}/commands",
            new { commandType = "ShowMessage", title = "Live", message = "push" });
        var commandId = command.Str("commandId");
        await live.WaitForAsync((e, d) => Change(e, d, "commands", deviceId) && d.Str("id") == commandId, "commands");

        // Сессия и аудит (Owner видит аудит).
        await owner.PostAsync($"/api/v1/devices/{deviceId}/sessions", null);
        await live.WaitForAsync((e, d) => Change(e, d, "sessions", deviceId), "sessions");
        await live.WaitForAsync((e, d) => Change(e, d, "audit", deviceId), "audit");
        Assert.All(live.Events.Where(e => e.Event == "change"),
            e => Assert.Equal(location.LocationId, e.Data.TryGetProperty("locationId", out var l) ? l.GetString() : location.LocationId));

        // Чужой tenant (в нём ничего не происходило) не получил ни одной подсказки.
        await Task.Delay(500);
        Assert.False(foreign.Has((e, _) => e == "change"));
    }

    [Fact]
    public async Task Edge_going_offline_is_pushed_by_presence_monitor()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var edge = new EdgeHost(cloud, AuthAndTenancyTests.TempPath(), await owner.EdgeTokenAsync(location));
        await Wait.UntilAsync(async () => (await owner.GetJsonAsync("/api/v1/me")).GetProperty("locations").EnumerateArray()
            .Any(l => l.Str("locationId") == location.LocationId &&
                      l.GetProperty("edges").EnumerateArray().Any(x => x.GetProperty("online").GetBoolean())), what: "Edge online");

        await using var live = await LiveReader.OpenAsync(owner);
        await edge.DisposeAsync(); // Edge пропал: отчётов больше нет

        await live.WaitForAsync((e, d) => Change(e, d, "edges") && d.Str("locationId") == location.LocationId,
            "Edge offline", TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Operator_does_not_get_staff_hints_and_loses_stream_when_disabled()
    {
        var owner = await cloud.LoginAsync();
        var email = $"live-op-{Guid.NewGuid():N}@club.test";
        var created = await owner.PostJsonAsync("/api/v1/staff", new { email, displayName = "Live Op", role = "Operator" });
        var temp = created.Str("temporaryPassword");
        var operatorClient = await cloud.LoginAsync(email, temp);
        var changed = await (await operatorClient.PostAsJsonAsync("/api/v1/me/password",
            new { currentPassword = temp, newPassword = "Operator-Live-2026!" })).JsonAsync();
        operatorClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", changed.Str("accessToken"));

        await using var live = await LiveReader.OpenAsync(operatorClient);

        // Владелец меняет персонал: у оператора нет staff.manage — подсказка ему не приходит,
        // а подсказка аудита (audit.view у оператора есть) приходит.
        var other = await owner.PostJsonAsync("/api/v1/staff",
            new { email = $"x-{Guid.NewGuid():N}@club.test", displayName = "X", role = "Operator" });
        await live.WaitForAsync((e, d) => Change(e, d, "audit"), "audit");
        Assert.False(live.Has((e, d) => Change(e, d, "staff")));

        // Отключение: поток закрывается при ближайшей перепроверке с событием reauth.
        (await owner.PostAsync($"/api/v1/staff/{created.GetProperty("user").Str("userId")}/deactivate", null)).EnsureSuccessStatusCode();
        await live.WaitForAsync((e, _) => e == "reauth", "reauth", TimeSpan.FromSeconds(60));
        await Wait.UntilAsync(async () => live.Closed, what: "поток закрыт");
        Assert.NotNull(other.GetProperty("user").Str("userId"));
    }

    [Fact]
    public async Task Stream_requires_authentication()
    {
        var response = await cloud.Anonymous().GetAsync("/api/v1/live");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
