using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;

namespace ClubOS.Integration.Tests;

[Collection("cloud")]
public sealed class CloudApiTests(CloudApiFixture fx)
{
    private sealed record TokenDto(string AccessToken, string RefreshToken, string Role);

    private sealed record SessionDto(string SessionId, string DeviceId, string State, long? TotalMinorUnits);

    private sealed record SyncResultDto(int Accepted, int Duplicates);

    private sealed record CommandDto(string Id, string DeviceId, string State);

    private sealed record DeviceDto(string Id);

    [Fact]
    public async Task Login_WithSeededOwner_ReturnsToken()
    {
        var token = await LoginAsync();
        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsUnauthorized()
    {
        var response = await fx.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = "owner@demo.clubos", password = "definitely-wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EndSession_CalledTwice_IsIdempotent()
    {
        var (_, _, deviceId) = await fx.CreateDeviceAsync();
        var token = await LoginAsync();

        var startResponse = await fx.Client.SendAsync(Post(
            "/api/v1/sessions/start",
            token,
            new { deviceId, actor = "it", correlationId = Guid.NewGuid().ToString("N") }));
        startResponse.EnsureSuccessStatusCode();
        var started = await startResponse.Content.ReadFromJsonAsync<SessionDto>();

        var firstEnd = await fx.Client.SendAsync(Post($"/api/v1/sessions/{started!.SessionId}/end", token));
        firstEnd.EnsureSuccessStatusCode();
        var ended1 = await firstEnd.Content.ReadFromJsonAsync<SessionDto>();

        var secondEnd = await fx.Client.SendAsync(Post($"/api/v1/sessions/{started.SessionId}/end", token));
        secondEnd.EnsureSuccessStatusCode();
        var ended2 = await secondEnd.Content.ReadFromJsonAsync<SessionDto>();

        Assert.Equal("Ended", ended1!.State);
        Assert.Equal("Ended", ended2!.State);
        Assert.NotNull(ended1.TotalMinorUnits);
        Assert.Equal(ended1.TotalMinorUnits, ended2.TotalMinorUnits);
    }

    [Fact]
    public async Task Sync_SameEventTwice_IsDeduplicated()
    {
        var (locationId, _, _) = await fx.CreateDeviceAsync();
        var token = await LoginAsync();

        var evt = new
        {
            eventId = Guid.NewGuid().ToString("N"),
            eventType = "TestPing",
            schemaVersion = 1,
            tenantId = "ignored",
            locationId,
            aggregateId = "agg-1",
            sequence = 1L,
            occurredAtUtc = DateTimeOffset.UtcNow,
            recordedAtUtc = DateTimeOffset.UtcNow,
            payload = new { note = "x" },
        };

        var first = await fx.Client.SendAsync(Post("/api/v1/sync/events", token, new { events = new[] { evt } }));
        first.EnsureSuccessStatusCode();
        var firstResult = await first.Content.ReadFromJsonAsync<SyncResultDto>();

        var second = await fx.Client.SendAsync(Post("/api/v1/sync/events", token, new { events = new[] { evt } }));
        second.EnsureSuccessStatusCode();
        var secondResult = await second.Content.ReadFromJsonAsync<SyncResultDto>();

        Assert.Equal(1, firstResult!.Accepted);
        Assert.Equal(0, firstResult.Duplicates);
        Assert.Equal(0, secondResult!.Accepted);
        Assert.Equal(1, secondResult.Duplicates);
    }

    [Fact]
    public async Task CommandResult_IsForwardOnly()
    {
        var (_, _, deviceId) = await fx.CreateDeviceAsync();
        var token = await LoginAsync();

        var issue = await fx.Client.SendAsync(Post(
            $"/api/v1/devices/{deviceId}/commands",
            token,
            new { title = "Тест", message = "Привет" }));
        issue.EnsureSuccessStatusCode();
        var command = await issue.Content.ReadFromJsonAsync<CommandDto>();

        var toSucceeded = await fx.Client.PostAsJsonAsync(
            $"/api/v1/devices/{deviceId}/commands/{command!.Id}/result",
            new { commandId = command.Id, deviceId, state = "Succeeded", reportedAtUtc = DateTimeOffset.UtcNow });
        toSucceeded.EnsureSuccessStatusCode();

        var backwards = await fx.Client.PostAsJsonAsync(
            $"/api/v1/devices/{deviceId}/commands/{command.Id}/result",
            new { commandId = command.Id, deviceId, state = "Acknowledged", reportedAtUtc = DateTimeOffset.UtcNow });
        backwards.EnsureSuccessStatusCode();
        var afterBackwards = await backwards.Content.ReadFromJsonAsync<CommandDto>();

        Assert.Equal("Succeeded", afterBackwards!.State);
    }

    [Fact]
    public async Task Devices_AreIsolatedByOrganization()
    {
        var (_, _, myDevice) = await fx.CreateDeviceAsync();
        var (_, _, foreignDevice) = await fx.CreateForeignDeviceAsync();
        var token = await LoginAsync();

        var listResponse = await fx.Client.SendAsync(Get("/api/v1/devices", token));
        listResponse.EnsureSuccessStatusCode();
        var devices = await listResponse.Content.ReadFromJsonAsync<List<DeviceDto>>();

        Assert.Contains(devices!, d => d.Id == myDevice);
        Assert.DoesNotContain(devices!, d => d.Id == foreignDevice);

        var foreignById = await fx.Client.SendAsync(Get($"/api/v1/devices/{foreignDevice}", token));
        Assert.Equal(HttpStatusCode.NotFound, foreignById.StatusCode);
    }

    private async Task<string> LoginAsync(string email = "owner@demo.clubos", string password = "Test1234!")
    {
        var response = await fx.Client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenDto>();
        return token!.AccessToken;
    }

    private static HttpRequestMessage Get(string url, string token) => Build(HttpMethod.Get, url, token, null);

    private static HttpRequestMessage Post(string url, string token, object? body = null) =>
        Build(HttpMethod.Post, url, token, body);

    private static HttpRequestMessage Build(HttpMethod method, string url, string token, object? body)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
        };
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }
}
