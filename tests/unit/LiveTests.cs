using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Live;
using ClubOS.CloudApi.Security;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>Live-подсказки Admin Web: изоляция tenant, переполнение, какие изменения публикуются.</summary>
public class LiveBrokerTests
{
    private static Device NewDevice() => new()
    {
        Id = "dev_1",
        TenantId = "org_a",
        LocationId = "loc_1",
        ZoneId = "zone_1",
        DisplayName = "PC-01",
        CertificatePem = "cert"
    };

    [Fact]
    public async Task Events_reach_only_subscribers_of_the_same_tenant()
    {
        var broker = new LiveBroker();
        using var a = broker.Subscribe("org_a");
        using var b = broker.Subscribe("org_b");

        broker.Publish(new LiveEvent(LiveTopics.Commands, "org_a", "loc_1", "dev_1", "cmd_1"));

        Assert.True(a.Reader.TryRead(out var evt));
        Assert.Equal("cmd_1", evt!.Id);
        Assert.False(b.Reader.TryRead(out _));
        Assert.False(await b.Reader.WaitToReadAsync(new CancellationTokenSource(50).Token).AsTask()
            .ContinueWith(t => t.IsCompletedSuccessfully && t.Result));
    }

    [Fact]
    public void Duplicates_in_one_batch_are_collapsed()
    {
        var broker = new LiveBroker();
        using var a = broker.Subscribe("org_a");
        var evt = new LiveEvent(LiveTopics.Devices, "org_a", "loc_1", "dev_1");

        broker.Publish([evt, evt, evt with { }]);

        Assert.True(a.Reader.TryRead(out _));
        Assert.False(a.Reader.TryRead(out _));
    }

    [Fact]
    public void Slow_subscriber_is_told_to_resync_instead_of_growing_memory()
    {
        var broker = new LiveBroker();
        using var a = broker.Subscribe("org_a");
        for (var i = 0; i < LiveBroker.BufferSize + 10; i++)
        {
            broker.Publish(new LiveEvent(LiveTopics.Commands, "org_a", "loc_1", "dev_1", $"cmd_{i}"));
        }

        Assert.True(a.TakeOverflow());
        Assert.False(a.TakeOverflow()); // флаг сбрасывается
        var count = 0;
        while (a.Reader.TryRead(out _))
        {
            count++;
        }

        Assert.Equal(LiveBroker.BufferSize, count);
    }

    [Fact]
    public void Unsubscribe_on_dispose()
    {
        var broker = new LiveBroker();
        var a = broker.Subscribe("org_a");
        Assert.Equal(1, broker.SubscriberCount);
        a.Dispose();
        Assert.Equal(0, broker.SubscriberCount);
    }

    [Fact]
    public void Heartbeat_only_changes_are_not_published()
    {
        var device = NewDevice();
        Assert.Empty(LiveChangeInterceptor.EventsFor(device, EntityState.Modified, [nameof(Device.LastHeartbeatUtc)]));

        var statusChange = Assert.Single(LiveChangeInterceptor.EventsFor(device, EntityState.Modified,
            [nameof(Device.LastHeartbeatUtc), nameof(Device.Status)]));
        Assert.Equal(new LiveEvent(LiveTopics.Devices, "org_a", "loc_1", "dev_1"), statusChange);

        Assert.Single(LiveChangeInterceptor.EventsFor(device, EntityState.Added, []));
    }

    [Fact]
    public void Edge_last_seen_is_noise_but_outbox_size_is_not()
    {
        var edge = new Edge { Id = "edg_1", TenantId = "org_a", LocationId = "loc_1", Name = "Edge", CertificatePem = "c" };
        Assert.Empty(LiveChangeInterceptor.EventsFor(edge, EntityState.Modified,
            [nameof(Edge.LastSeenAtUtc), nameof(Edge.LastEdgeClockUtc)]));
        Assert.Single(LiveChangeInterceptor.EventsFor(edge, EntityState.Modified,
            [nameof(Edge.LastSeenAtUtc), nameof(Edge.PendingOutboxEvents)]));
    }

    [Fact]
    public void Session_change_also_refreshes_the_device_tile()
    {
        var session = new Session
        {
            Id = "ses_1",
            TenantId = "org_a",
            DeviceId = "dev_1",
            LocationId = "loc_1",
            Origin = "cloud",
            Currency = "TJS",
            StartedBy = "user:x",
            CorrelationId = "cor"
        };
        var events = LiveChangeInterceptor.EventsFor(session, EntityState.Modified, [nameof(Session.State)]).ToList();
        Assert.Contains(events, e => e.Topic == LiveTopics.Sessions && e.Id == "ses_1");
        Assert.Contains(events, e => e.Topic == LiveTopics.Devices && e.DeviceId == "dev_1");
    }

    [Fact]
    public void Audit_event_points_to_device_target()
    {
        var audit = new AuditEvent
        {
            Id = "aud_1",
            TenantId = "org_a",
            LocationId = "loc_1",
            Actor = "user:x",
            Action = "session.start",
            Target = "device:dev_1",
            Result = "requested"
        };
        var evt = Assert.Single(LiveChangeInterceptor.EventsFor(audit, EntityState.Added, []));
        Assert.Equal("dev_1", evt.DeviceId);
        Assert.Equal(LiveTopics.Audit, evt.Topic);
    }

    private static readonly ClubOS.CloudApi.Auth.LocationAccess Everything = new(true, new HashSet<string> { "loc_1", "loc_2" });

    [Theory]
    [InlineData(Roles.Owner, LiveTopics.Staff, true)]
    [InlineData(Roles.Admin, LiveTopics.Staff, false)]
    [InlineData(Roles.Operator, LiveTopics.Staff, false)]
    [InlineData(Roles.Operator, LiveTopics.Audit, true)]
    [InlineData(Roles.Operator, LiveTopics.Devices, true)]
    [InlineData(Roles.Operator, LiveTopics.Cash, true)]
    public void Hints_respect_permissions(string role, string topic, bool allowed) =>
        Assert.Equal(allowed, LiveEndpoints.Allowed(role, Everything, new LiveEvent(topic, "org_a", "loc_1")));

    [Fact]
    public void Hints_respect_location_access()
    {
        var onlyFirst = new ClubOS.CloudApi.Auth.LocationAccess(false, new HashSet<string> { "loc_1" });
        Assert.True(LiveEndpoints.Allowed(Roles.Operator, onlyFirst, new LiveEvent(LiveTopics.Devices, "org_a", "loc_1", "dev_1")));
        Assert.False(LiveEndpoints.Allowed(Roles.Operator, onlyFirst, new LiveEvent(LiveTopics.Devices, "org_a", "loc_2", "dev_2")));
        // События без локации (вход сотрудника в аудите) — только при доступе ко всей организации.
        Assert.False(LiveEndpoints.Allowed(Roles.Operator, onlyFirst, new LiveEvent(LiveTopics.Audit, "org_a", null)));
        Assert.True(LiveEndpoints.Allowed(Roles.Operator, Everything, new LiveEvent(LiveTopics.Audit, "org_a", null)));
    }
}
