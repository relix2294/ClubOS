using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.Contracts;
using ClubOS.Security;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>Протокол Edge ↔ Cloud на уровне HTTP: подпись, anti-replay, идемпотентный sync.</summary>
[Collection(CloudCollection.Name)]
public class EdgeProtocolTests(CloudFixture cloud)
{
    private sealed record RawEdge(string EdgeId, string TenantId, string LocationId, DeviceKey Key);

    private async Task<RawEdge> EnrollRawEdgeAsync(TestLocation location)
    {
        var owner = await cloud.LoginAsync();
        var key = DeviceKey.Generate();
        var enrolled = await cloud.Anonymous().PostJsonAsync("/api/v1/edge/enroll", new
        {
            enrollmentToken = await owner.EdgeTokenAsync(location),
            certificateSigningRequestPem = key.CreateSigningRequestPem("edge")
        });
        return new RawEdge(enrolled.Str("edgeId"), enrolled.Str("tenantId"), enrolled.Str("locationId"), key);
    }

    private HttpRequestMessage Signed(RawEdge edge, HttpMethod method, string url, object? body = null, string? token = null)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = body is null ? null : JsonContent.Create(body, options: ContractJson.Options)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(SignedToken.Scheme,
            token ?? SignedToken.Create(edge.EdgeId, edge.Key.Key, SignedToken.AudienceCloud, TimeProvider.System));
        return request;
    }

    [Fact]
    public async Task Signed_request_is_accepted_and_replayed_signature_is_rejected()
    {
        var edge = await EnrollRawEdgeAsync(await cloud.CreateLocationAsync());
        var client = cloud.Anonymous();
        var token = SignedToken.Create(edge.EdgeId, edge.Key.Key, SignedToken.AudienceCloud, TimeProvider.System);

        var ok = await client.SendAsync(Signed(edge, HttpMethod.Get, "/api/v1/edge/config", token: token));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(12_000, (await ok.JsonAsync()).GetProperty("zones")[0].GetProperty("pricePerHourMinorUnits").GetInt64());

        var replay = await client.SendAsync(Signed(edge, HttpMethod.Get, "/api/v1/edge/config", token: token));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        using var otherKey = DeviceKey.Generate();
        var forged = SignedToken.Create(edge.EdgeId, otherKey.Key, SignedToken.AudienceCloud, TimeProvider.System);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Signed(edge, HttpMethod.Get, "/api/v1/edge/config", token: forged))).StatusCode);
    }

    [Fact]
    public async Task Repeated_sync_batch_does_not_duplicate_session()
    {
        var location = await cloud.CreateLocationAsync();
        var owner = await cloud.LoginAsync();
        await using var edgeHost = new EdgeHost(cloud, AuthAndTenancyTests.TempPath(), await owner.EdgeTokenAsync(location));
        await using var agent = new AgentHost(edgeHost, AuthAndTenancyTests.TempPath(), await owner.DeviceTokenAsync(location));
        var deviceId = await Wait.ForAsync(async () => agent.Runtime.DeviceId);

        // Второй «Edge» той же локации отправляет вручную собранный batch.
        var edge = await EnrollRawEdgeAsync(location);
        var sessionId = $"ses_{Guid.NewGuid():N}";
        var snapshot = new PriceSnapshot { PricePerHourMinorUnits = 12_000, Currency = "TJS", Rounding = RoundingRule.CeilingPerMinute, RuleVersion = 1 };
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var batch = new SyncBatchRequest
        {
            Events =
            [
                Envelope(edge, 1, EventTypes.SessionStarted, sessionId, new SessionStartedPayload
                {
                    SessionId = sessionId, DeviceId = deviceId, StartedAtUtc = start, PriceSnapshot = snapshot, Actor = "edge-cli:it", Origin = "edge"
                }),
                Envelope(edge, 2, EventTypes.SessionEnded, sessionId, new SessionEndedPayload
                {
                    SessionId = sessionId, DeviceId = deviceId, StartedAtUtc = start, EndedAtUtc = start.AddSeconds(60),
                    PriceSnapshot = snapshot, TotalMinorUnits = 200, Actor = "edge-cli:it", Origin = "edge"
                })
            ]
        };

        var client = cloud.Anonymous();
        var first = await (await client.SendAsync(Signed(edge, HttpMethod.Post, "/api/v1/edge/sync", batch))).JsonAsync();
        Assert.Equal(2, first.GetProperty("accepted").GetArrayLength());

        var second = await (await client.SendAsync(Signed(edge, HttpMethod.Post, "/api/v1/edge/sync", batch))).JsonAsync();
        Assert.Equal(0, second.GetProperty("accepted").GetArrayLength());
        Assert.Equal(2, second.GetProperty("duplicates").GetArrayLength());

        var sessions = await owner.GetJsonAsync($"/api/v1/devices/{deviceId}/sessions");
        var session = Assert.Single(sessions.EnumerateArray(), s => s.Str("sessionId") == sessionId);
        Assert.Equal("Ended", session.Str("state"));
        Assert.Equal(200, session.GetProperty("totalMinorUnits").GetInt64());

        var endedAudit = await cloud.WithDb(db => Task.FromResult(db.AuditEvents.Where(a => a.Action == "session.ended")
            .Select(a => a.DetailsJson).AsEnumerable().Count(d => d!.Contains(sessionId))));
        Assert.Equal(1, endedAudit);
    }

    [Fact]
    public async Task Event_for_foreign_location_is_rejected()
    {
        var edge = await EnrollRawEdgeAsync(await cloud.CreateLocationAsync());
        var other = await cloud.CreateLocationAsync();
        var batch = new SyncBatchRequest
        {
            Events = [Envelope(edge with { LocationId = other.LocationId }, 1, EventTypes.DeviceConnectivityChanged, "dev_x",
                new DeviceConnectivityChangedPayload { DeviceId = "dev_x", Online = true, AtUtc = DateTimeOffset.UtcNow })]
        };

        var result = await (await cloud.Anonymous().SendAsync(Signed(edge, HttpMethod.Post, "/api/v1/edge/sync", batch))).JsonAsync();
        Assert.Equal(1, result.GetProperty("rejected").GetArrayLength());
    }

    private static EventEnvelope Envelope<T>(RawEdge edge, long seq, string type, string aggregate, T payload) => new()
    {
        EventId = $"evt_{Guid.NewGuid():N}",
        EventType = type,
        TenantId = edge.TenantId,
        LocationId = edge.LocationId,
        AggregateId = aggregate,
        Sequence = seq,
        OccurredAtUtc = DateTimeOffset.UtcNow,
        RecordedAtUtc = DateTimeOffset.UtcNow,
        Payload = JsonSerializer.SerializeToElement(payload, ContractJson.Options)
    };
}
