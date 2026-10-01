using System.Text.Json;
using ClubOS.Contracts;
using ClubOS.EdgeController.Storage;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>Edge SQLite store: сессии, outbox, inbox, команды (ТЗ §5.1, §23.3).</summary>
public class EdgeStoreTests : IDisposable
{
    private const string DeviceId = "dev_1";
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));
    private EdgeDatabase _db;
    private EdgeStore _store;

    public EdgeStoreTests()
    {
        (_db, _store) = Open();
        _store.SaveConfigAsync(Config()).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _dir.Dispose();
    }

    private (EdgeDatabase, EdgeStore) Open()
    {
        var db = new EdgeDatabase(_dir.Path);
        return (db, new EdgeStore(db, new EdgeSignals(), _time));
    }

    /// <summary>Имитация перезапуска процесса Edge: закрыть БД и открыть заново тот же файл.</summary>
    private void Restart()
    {
        _db.Dispose();
        (_db, _store) = Open();
    }

    private static EdgeConfigResponse Config() => new()
    {
        LocationId = "loc_1",
        LocationName = "Test",
        Timezone = "Asia/Dushanbe",
        Currency = "TJS",
        Zones = [new EdgeZoneConfig { ZoneId = "zone_std", Name = "Standard", PricePerHourMinorUnits = 12_000, Rounding = RoundingRule.CeilingPerMinute, RuleVersion = 1 }],
        Devices = [new EdgeDeviceConfig { DeviceId = DeviceId, DisplayName = "PC-01", ZoneId = "zone_std", Simulated = false, CertificatePem = "cert" }]
    };

    private List<OutboxEvent> Events(string type) =>
        _store.GetPendingEvents(1000).Where(e => e.EventType == type).ToList();

    [Fact]
    public async Task Local_session_60_seconds_costs_2_TJS()
    {
        var started = await _store.StartLocalSessionAsync(DeviceId, "edge-cli:op");
        Assert.Equal(SessionOutcome.Started, started.Outcome);
        Assert.Equal(12_000, started.Session!.PriceSnapshot.PricePerHourMinorUnits);

        _time.Advance(TimeSpan.FromSeconds(60));
        var ended = await _store.EndSessionAsync(started.Session.SessionId, "edge-cli:op");

        Assert.Equal(SessionOutcome.Ended, ended.Outcome);
        Assert.Equal(200, ended.Session!.TotalMinorUnits); // 60 сек = 2,00 TJS
    }

    [Fact]
    public async Task Repeated_EndSession_is_idempotent()
    {
        var started = await _store.StartLocalSessionAsync(DeviceId, "op");
        _time.Advance(TimeSpan.FromSeconds(61));
        var first = await _store.EndSessionAsync(started.Session!.SessionId, "op");
        _time.Advance(TimeSpan.FromMinutes(10));
        var second = await _store.EndSessionAsync(started.Session.SessionId, "op");

        Assert.Equal(SessionOutcome.AlreadyEnded, second.Outcome);
        Assert.Equal(first.Session!.TotalMinorUnits, second.Session!.TotalMinorUnits);
        Assert.Equal(400, second.Session.TotalMinorUnits); // 61 сек = 4,00 TJS
        Assert.Single(Events(EventTypes.SessionEnded));      // второго события нет
    }

    [Fact]
    public async Task Only_one_active_session_per_device()
    {
        Assert.Equal(SessionOutcome.Started, (await _store.StartLocalSessionAsync(DeviceId, "op")).Outcome);
        var second = await _store.StartLocalSessionAsync(DeviceId, "op");
        Assert.Equal(SessionOutcome.Rejected, second.Outcome);
        Assert.Single(_store.GetActiveSessions());
    }

    [Fact]
    public async Task Restart_does_not_lose_active_session_or_pending_events()
    {
        var started = await _store.StartLocalSessionAsync(DeviceId, "op");
        Restart();

        var active = Assert.Single(_store.GetActiveSessions());
        Assert.Equal(started.Session!.SessionId, active.SessionId);
        Assert.Single(Events(EventTypes.SessionStarted));

        _time.Advance(TimeSpan.FromSeconds(30));
        var ended = await _store.EndSessionAsync(active.SessionId, "op");
        Assert.Equal(200, ended.Session!.TotalMinorUnits);
    }

    [Fact]
    public async Task Outbox_is_cleared_only_after_cloud_ack_and_rejections_are_not_retried()
    {
        var s = await _store.StartLocalSessionAsync(DeviceId, "op");
        await _store.EndSessionAsync(s.Session!.SessionId, "op");
        var pending = _store.GetPendingEvents(100);
        Assert.Equal(2, pending.Count);
        Assert.True(pending[0].Sequence < pending[1].Sequence); // монотонная sequence

        await _store.RecordSendFailureAsync(pending.Select(e => e.EventId), "network down");
        Restart();
        Assert.Equal(2, _store.CountPendingEvents()); // сбой отправки ничего не теряет

        await _store.MarkEventsDeliveredAsync([pending[0].EventId],
            [new RejectedEvent { EventId = pending[1].EventId, Reason = "test" }]);
        Assert.Equal(0, _store.CountPendingEvents());
    }

    [Fact]
    public async Task Cloud_command_duplicate_delivery_is_applied_once()
    {
        var command = DeviceCommand("cmd_1", _time.GetUtcNow().AddMinutes(2));
        Assert.True(await _store.ApplyCloudCommandAsync(command));
        Assert.False(await _store.ApplyCloudCommandAsync(command));

        var delivered = await _store.TakeCommandsForDeviceAsync(DeviceId);
        Assert.Single(delivered);
        Assert.Single(Events(EventTypes.CommandStateChanged)); // Delivered — один раз
    }

    [Fact]
    public async Task Undelivered_expired_command_is_not_given_to_agent_and_becomes_expired()
    {
        await _store.ApplyCloudCommandAsync(DeviceCommand("cmd_2", _time.GetUtcNow().AddSeconds(30)));
        _time.Advance(TimeSpan.FromSeconds(31));

        Assert.Empty(await _store.TakeCommandsForDeviceAsync(DeviceId));
        Assert.Equal(1, await _store.ExpireCommandsAsync());
        Assert.Equal(CommandState.Expired, _store.GetCommandState("cmd_2"));
    }

    [Fact]
    public async Task Command_already_expired_on_arrival_is_reported_expired()
    {
        await _store.ApplyCloudCommandAsync(DeviceCommand("cmd_3", _time.GetUtcNow().AddSeconds(-1)));
        Assert.Equal(CommandState.Expired, _store.GetCommandState("cmd_3"));
        Assert.Empty(await _store.TakeCommandsForDeviceAsync(DeviceId));
    }

    [Fact]
    public async Task Agent_results_only_move_forward()
    {
        await _store.ApplyCloudCommandAsync(DeviceCommand("cmd_4", _time.GetUtcNow().AddMinutes(2)));
        await _store.TakeCommandsForDeviceAsync(DeviceId);

        Assert.True(await _store.ApplyAgentResultAsync(DeviceId, "cmd_4", CommandState.Succeeded, null));
        Assert.False(await _store.ApplyAgentResultAsync(DeviceId, "cmd_4", CommandState.Acknowledged, null));
        Assert.False(await _store.ApplyAgentResultAsync(DeviceId, "cmd_4", CommandState.Failed, "late"));
        Assert.False(await _store.ApplyAgentResultAsync("dev_other", "cmd_4", CommandState.Failed, null));
        Assert.Equal(CommandState.Succeeded, _store.GetCommandState("cmd_4"));
    }

    [Fact]
    public async Task Redelivery_returns_delivered_command_again_for_agent_dedup()
    {
        await _store.ApplyCloudCommandAsync(DeviceCommand("cmd_5", _time.GetUtcNow().AddMinutes(2)));
        Assert.Single(await _store.TakeCommandsForDeviceAsync(DeviceId));
        Assert.Single(await _store.TakeCommandsForDeviceAsync(DeviceId)); // ответ мог потеряться
    }

    [Fact]
    public async Task Cloud_start_session_uses_cloud_price_snapshot_and_rejects_busy_device()
    {
        var first = StartCommand("ecm_1", "ses_cloud_1", 6_000);
        await _store.ApplyCloudCommandAsync(first);
        var session = _store.GetSession("ses_cloud_1");
        Assert.Equal(6_000, session!.PriceSnapshot.PricePerHourMinorUnits);

        await _store.ApplyCloudCommandAsync(StartCommand("ecm_2", "ses_cloud_2", 12_000));
        Assert.Null(_store.GetSession("ses_cloud_2"));
        var rejected = Assert.Single(Events(EventTypes.SessionStartRejected));
        Assert.Contains("ses_cloud_2", rejected.PayloadJson);
    }

    [Fact]
    public async Task Heartbeat_timeout_marks_device_offline_with_event()
    {
        await _store.RecordHeartbeatAsync(new HeartbeatMessage { DeviceId = DeviceId, Status = DeviceStatus.Idle, ClockUtc = _time.GetUtcNow() });
        Assert.True(_store.GetDevice(DeviceId)!.Online);

        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await _store.MarkStaleDevicesOfflineAsync(TimeSpan.FromSeconds(30)));
        Assert.False(_store.GetDevice(DeviceId)!.Online);

        var events = Events(EventTypes.DeviceConnectivityChanged)
            .Select(e => JsonSerializer.Deserialize<DeviceConnectivityChangedPayload>(e.PayloadJson, ContractJson.Options)!.Online)
            .ToList();
        Assert.Equal([true, false], events);
    }

    [Fact]
    public async Task Local_start_without_cached_tariff_is_rejected()
    {
        // Устройство зарегистрировано через Edge, но конфигурация зон из Cloud ещё не получена.
        using var fresh = new TempDir();
        using var db = new EdgeDatabase(fresh.Path);
        var store = new EdgeStore(db, new EdgeSignals(), _time);
        await store.AddEnrolledDeviceAsync(new DeviceEnrollResponse
        {
            DeviceId = "dev_new",
            DeviceCertificatePem = "cert",
            CertificateExpiresAtUtc = _time.GetUtcNow().AddDays(1),
            DisplayName = "PC-NEW",
            ZoneId = "zone_std",
            Simulated = false
        }, new DeviceInventory { Hostname = "h", WindowsVersion = "w", Cpu = "c", RamMegabytes = 1, Ipv4 = "1.1.1.1", AgentVersion = "1" });

        var result = await store.StartLocalSessionAsync("dev_new", "op");
        Assert.Equal(SessionOutcome.Rejected, result.Outcome);
        Assert.Contains("Тариф", result.Error);
        Assert.Equal(SessionOutcome.NotFound, (await store.StartLocalSessionAsync("dev_unknown", "op")).Outcome);
    }

    private EdgeCommand DeviceCommand(string id, DateTimeOffset expires) => new()
    {
        Id = id,
        Kind = EdgeCommandKind.DeviceCommand,
        IssuedAtUtc = _time.GetUtcNow(),
        ExpiresAtUtc = expires,
        DeviceCommand = new CommandEnvelope
        {
            CommandId = id,
            CommandType = CommandType.ShowMessage,
            TargetDeviceIds = [DeviceId],
            IssuedBy = "user:1",
            IssuedAtUtc = _time.GetUtcNow(),
            ExpiresAtUtc = expires,
            CorrelationId = "cor_1",
            Payload = ContractJson.ToElement(new ShowMessagePayload { Title = "t", Message = "m" })
        }
    };

    private EdgeCommand StartCommand(string id, string sessionId, long price) => new()
    {
        Id = id,
        Kind = EdgeCommandKind.StartSession,
        IssuedAtUtc = _time.GetUtcNow(),
        ExpiresAtUtc = _time.GetUtcNow().AddMinutes(10),
        StartSession = new StartSessionCommand
        {
            SessionId = sessionId,
            DeviceId = DeviceId,
            PriceSnapshot = new PriceSnapshot { PricePerHourMinorUnits = price, Currency = "TJS", Rounding = RoundingRule.CeilingPerMinute, RuleVersion = 1 },
            Actor = "user:1",
            CorrelationId = "cor_s"
        }
    };
}

/// <summary>Пакет outbox ограничен по объёму (снимки экрана D-022), но хотя бы одно событие уходит.</summary>
public class OutboxBatchSizeTests
{
    private static ClubOS.EdgeController.Storage.OutboxEvent Event(long seq, int size) =>
        new(seq, $"evt_{seq}", "T", "agg", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, new string('x', size), 0);

    [Fact]
    public void Batch_stops_before_the_size_limit_but_keeps_at_least_one()
    {
        var events = new[] { Event(1, 400), Event(2, 400), Event(3, 400) };
        Assert.Equal(2, ClubOS.EdgeController.Workers.OutboxPublisher.LimitBySize(events, maxChars: 1000).Count);
        Assert.Single(ClubOS.EdgeController.Workers.OutboxPublisher.LimitBySize([Event(1, 5000), Event(2, 1)], maxChars: 1000));
        Assert.Equal(3, ClubOS.EdgeController.Workers.OutboxPublisher.LimitBySize(events).Count);
    }
}
