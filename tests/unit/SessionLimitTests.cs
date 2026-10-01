using System.Text.Json;
using ClubOS.Contracts;
using ClubOS.EdgeController.Storage;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>Сессии с лимитом времени на Edge: таймер, продление, состояние для Player Shell (M1).</summary>
public class SessionLimitTests : IDisposable
{
    private const string DeviceId = "dev_1";
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
    private readonly EdgeDatabase _db;
    private readonly EdgeStore _store;

    public SessionLimitTests()
    {
        _db = new EdgeDatabase(_dir.Path);
        _store = new EdgeStore(_db, new EdgeSignals(), _time);
        _store.SaveConfigAsync(new EdgeConfigResponse
        {
            LocationId = "loc_1",
            LocationName = "Dushanbe Pilot",
            Timezone = "Asia/Dushanbe",
            Currency = "TJS",
            Zones = [new EdgeZoneConfig { ZoneId = "zone_std", Name = "Standard", PricePerHourMinorUnits = 12_000, Rounding = RoundingRule.CeilingPerMinute, RuleVersion = 1 }],
            Devices = [new EdgeDeviceConfig { DeviceId = DeviceId, DisplayName = "PC-01", ZoneId = "zone_std", Simulated = false, CertificatePem = "cert" }]
        }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _dir.Dispose();
    }

    private T LastEvent<T>(string type) =>
        JsonSerializer.Deserialize<T>(_store.GetPendingEvents(1000).Last(e => e.EventType == type).PayloadJson, ContractJson.Options)!;

    private int CountEvents(string type) => _store.GetPendingEvents(1000).Count(e => e.EventType == type);

    private async Task<EdgeSession> StartLimited(int minutes)
    {
        var result = await _store.StartLocalSessionAsync(DeviceId, "edge-cli:op", minutes);
        Assert.Equal(SessionOutcome.Started, result.Outcome);
        return result.Session!;
    }

    [Fact]
    public void Database_is_migrated_to_current_schema() => Assert.Equal(4, _db.SchemaVersion); // v2 — лимиты сессий, v3 — бездисковые ПК, v4 — тарифы по времени и пакеты

    [Fact]
    public async Task Limited_session_has_planned_end_in_state_and_event()
    {
        var session = await StartLimited(30);

        Assert.Equal(_time.Now.AddMinutes(30), session.PlannedEndAtUtc);
        Assert.Equal(_time.Now.AddMinutes(30), LastEvent<SessionStartedPayload>(EventTypes.SessionStarted).PlannedEndAtUtc);
    }

    [Fact]
    public async Task Open_session_has_no_planned_end()
    {
        var result = await _store.StartLocalSessionAsync(DeviceId, "edge-cli:op");
        Assert.Null(result.Session!.PlannedEndAtUtc);
        Assert.Null(LastEvent<SessionStartedPayload>(EventTypes.SessionStarted).PlannedEndAtUtc);

        _time.Advance(TimeSpan.FromDays(1));
        Assert.Empty(await _store.EndExpiredSessionsAsync());
    }

    [Fact]
    public async Task Timer_ends_session_exactly_at_planned_end_with_deterministic_total()
    {
        var session = await StartLimited(30);

        _time.Advance(TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(59));
        Assert.Empty(await _store.EndExpiredSessionsAsync());

        _time.Advance(TimeSpan.FromSeconds(3)); // таймер сработал на 2 с позже планового окончания
        var ended = Assert.Single(await _store.EndExpiredSessionsAsync());

        Assert.Equal(SessionState.Ended, ended.State);
        Assert.Equal(session.PlannedEndAtUtc, ended.EndedAtUtc);
        Assert.Equal(6_000, ended.TotalMinorUnits); // 30 минут × 120 TJS/час = 60,00 TJS, без лишней минуты
        Assert.Equal(SessionEndReasons.TimeLimit, ended.EndReason);
        Assert.Equal(SessionEndReasons.TimerActor, ended.EndedBy);

        var evt = LastEvent<SessionEndedPayload>(EventTypes.SessionEnded);
        Assert.Equal(SessionEndReasons.TimeLimit, evt.Reason);
        Assert.Equal(6_000, evt.TotalMinorUnits);
        Assert.Equal(session.PlannedEndAtUtc, evt.EndedAtUtc);

        // Повторный проход таймера ничего не делает.
        Assert.Empty(await _store.EndExpiredSessionsAsync());
        Assert.Equal(1, CountEvents(EventTypes.SessionEnded));
    }

    [Fact]
    public async Task Edge_down_past_planned_end_does_not_overcharge()
    {
        await StartLimited(60);
        _time.Advance(TimeSpan.FromHours(5)); // Edge был выключен

        var ended = Assert.Single(await _store.EndExpiredSessionsAsync());
        Assert.Equal(12_000, ended.TotalMinorUnits);
    }

    [Fact]
    public async Task Staff_end_after_planned_end_is_capped_at_planned_end()
    {
        var session = await StartLimited(10);
        _time.Advance(TimeSpan.FromMinutes(25));

        var result = await _store.EndSessionAsync(session.SessionId, "edge-cli:op");
        Assert.Equal(SessionOutcome.Ended, result.Outcome);
        Assert.Equal(session.PlannedEndAtUtc, result.Session!.EndedAtUtc);
        Assert.Equal(2_000, result.Session.TotalMinorUnits);
        Assert.Equal(SessionEndReasons.Staff, result.Session.EndReason);
    }

    [Fact]
    public async Task Staff_end_before_planned_end_charges_actual_time()
    {
        var session = await StartLimited(60);
        _time.Advance(TimeSpan.FromSeconds(61));

        var result = await _store.EndSessionAsync(session.SessionId, "edge-cli:op");
        Assert.Equal(400, result.Session!.TotalMinorUnits);
    }

    [Fact]
    public async Task Extend_moves_planned_end_and_writes_event()
    {
        var session = await StartLimited(30);
        _time.Advance(TimeSpan.FromMinutes(20));

        var result = await _store.ExtendSessionAsync(session.SessionId, 15, "edge-cli:op");

        Assert.Equal(SessionOutcome.Extended, result.Outcome);
        Assert.Equal(session.PlannedEndAtUtc!.Value.AddMinutes(15), result.Session!.PlannedEndAtUtc);
        Assert.Equal(result.Session.PlannedEndAtUtc, _store.GetSession(session.SessionId)!.PlannedEndAtUtc);
        var evt = LastEvent<SessionExtendedPayload>(EventTypes.SessionExtended);
        Assert.Equal(15, evt.AddedMinutes);
        Assert.Equal(result.Session.PlannedEndAtUtc, evt.PlannedEndAtUtc);

        // Старое окончание прошло — сессия продолжается до нового.
        _time.Advance(TimeSpan.FromMinutes(15));
        Assert.Empty(await _store.EndExpiredSessionsAsync());
        _time.Advance(TimeSpan.FromMinutes(10));
        var ended = Assert.Single(await _store.EndExpiredSessionsAsync());
        Assert.Equal(9_000, ended.TotalMinorUnits); // 45 минут
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(SessionLimits.MaxExtendMinutes + 1)]
    public async Task Extend_rejects_invalid_minutes(int minutes)
    {
        var session = await StartLimited(30);
        var result = await _store.ExtendSessionAsync(session.SessionId, minutes, "op");
        Assert.Equal(SessionOutcome.Rejected, result.Outcome);
        Assert.Equal(0, CountEvents(EventTypes.SessionExtended));
    }

    [Fact]
    public async Task Extend_rejects_open_ended_and_finished_sessions()
    {
        var open = (await _store.StartLocalSessionAsync(DeviceId, "op")).Session!;
        Assert.Equal(SessionOutcome.Rejected, (await _store.ExtendSessionAsync(open.SessionId, 10, "op")).Outcome);
        await _store.EndSessionAsync(open.SessionId, "op");

        var limited = await StartLimited(5);
        await _store.EndSessionAsync(limited.SessionId, "op");
        Assert.Equal(SessionOutcome.Rejected, (await _store.ExtendSessionAsync(limited.SessionId, 10, "op")).Outcome);
        Assert.Equal(SessionOutcome.NotFound, (await _store.ExtendSessionAsync("ses_missing", 10, "op")).Outcome);
        Assert.Equal(0, CountEvents(EventTypes.SessionExtended));
    }

    [Fact]
    public async Task Extend_cannot_exceed_24_hours_in_total()
    {
        var session = await StartLimited(SessionLimits.MaxDurationMinutes - 30);
        Assert.Equal(SessionOutcome.Rejected, (await _store.ExtendSessionAsync(session.SessionId, 31, "op")).Outcome);
        Assert.Equal(SessionOutcome.Extended, (await _store.ExtendSessionAsync(session.SessionId, 30, "op")).Outcome);
    }

    [Fact]
    public async Task Extend_after_limit_expired_is_rejected()
    {
        var session = await StartLimited(10);
        _time.Advance(TimeSpan.FromMinutes(11)); // таймер ещё не прошёл
        Assert.Equal(SessionOutcome.Rejected, (await _store.ExtendSessionAsync(session.SessionId, 10, "op")).Outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(SessionLimits.MaxDurationMinutes + 1)]
    public async Task Start_rejects_invalid_duration(int minutes)
    {
        var result = await _store.StartLocalSessionAsync(DeviceId, "op", minutes);
        Assert.Equal(SessionOutcome.Rejected, result.Outcome);
        Assert.Empty(_store.GetActiveSessions());
    }

    [Fact]
    public async Task Cloud_start_with_invalid_duration_emits_rejection()
    {
        await _store.ApplyCloudCommandAsync(new EdgeCommand
        {
            Id = "ecm_1",
            Kind = EdgeCommandKind.StartSession,
            IssuedAtUtc = _time.Now,
            ExpiresAtUtc = _time.Now.AddMinutes(10),
            StartSession = new StartSessionCommand
            {
                SessionId = "ses_cloud",
                DeviceId = DeviceId,
                PriceSnapshot = _store.GetZonePrice("zone_std")!,
                Actor = "user:x",
                CorrelationId = "cor_1",
                DurationMinutes = 0
            }
        });

        Assert.Equal(1, CountEvents(EventTypes.SessionStartRejected));
        Assert.Empty(_store.GetActiveSessions());
    }

    [Fact]
    public async Task Cloud_extend_is_idempotent_by_queue_item_id()
    {
        await _store.ApplyCloudCommandAsync(new EdgeCommand
        {
            Id = "ecm_start",
            Kind = EdgeCommandKind.StartSession,
            IssuedAtUtc = _time.Now,
            ExpiresAtUtc = _time.Now.AddMinutes(10),
            StartSession = new StartSessionCommand
            {
                SessionId = "ses_cloud",
                DeviceId = DeviceId,
                PriceSnapshot = _store.GetZonePrice("zone_std")!,
                Actor = "user:x",
                CorrelationId = "cor_1",
                DurationMinutes = 60
            }
        });
        var extend = new EdgeCommand
        {
            Id = "ecm_extend",
            Kind = EdgeCommandKind.ExtendSession,
            IssuedAtUtc = _time.Now,
            ExpiresAtUtc = _time.Now.AddMinutes(10),
            ExtendSession = new ExtendSessionCommand { SessionId = "ses_cloud", Minutes = 30, Actor = "user:x", CorrelationId = "cor_1" }
        };

        Assert.True(await _store.ApplyCloudCommandAsync(extend));
        Assert.False(await _store.ApplyCloudCommandAsync(extend)); // повторная доставка

        Assert.Equal(_time.Now.AddMinutes(90), _store.GetSession("ses_cloud")!.PlannedEndAtUtc);
        Assert.Equal(1, CountEvents(EventTypes.SessionExtended));
    }

    [Fact]
    public async Task Local_session_uses_zone_periods_and_offset_from_config()
    {
        // 10:00 UTC = 15:00 в Душанбе (UTC+5); с 15:10 до 16:00 — «счастливый час» 60/час.
        await _store.SaveConfigAsync(new EdgeConfigResponse
        {
            LocationId = "loc_1",
            LocationName = "Dushanbe Pilot",
            Timezone = "Asia/Dushanbe",
            Currency = "TJS",
            UtcOffsetMinutes = 300,
            Zones =
            [
                new EdgeZoneConfig
                {
                    ZoneId = "zone_std", Name = "Standard", PricePerHourMinorUnits = 12_000, Rounding = RoundingRule.CeilingPerMinute,
                    RuleVersion = 2,
                    Periods = [new PricePeriod { Days = PricePeriod.AllDays, StartMinute = 15 * 60 + 10, EndMinute = 16 * 60, PricePerHourMinorUnits = 6_000 }]
                }
            ],
            Devices = [new EdgeDeviceConfig { DeviceId = DeviceId, DisplayName = "PC-01", ZoneId = "zone_std", Simulated = false, CertificatePem = "cert" }]
        });

        var price = _store.GetZonePrice("zone_std")!;
        Assert.Equal(300, price.UtcOffsetMinutes);
        Assert.Single(price.Periods);

        var session = await StartLimited(30);
        Assert.Single(session.PriceSnapshot.Periods); // снимок с периодами сохранён в сессии
        _time.Advance(TimeSpan.FromMinutes(30));
        var ended = Assert.Single(await _store.EndExpiredSessionsAsync());
        // 10 мин × 120 + 20 мин × 60 = 20,00 + 20,00.
        Assert.Equal(4_000, ended.TotalMinorUnits);
        var evt = LastEvent<SessionEndedPayload>(EventTypes.SessionEnded);
        Assert.Equal(4_000, evt.TotalMinorUnits);
        Assert.Equal(300, evt.PriceSnapshot.UtcOffsetMinutes);
        Assert.Single(evt.PriceSnapshot.Periods);
    }

    [Fact]
    public async Task Cloud_package_session_keeps_package_price_and_bills_extension()
    {
        var snapshot = _store.GetZonePrice("zone_std")! with
        {
            PackageId = "pkg_1",
            PackageName = "1 час",
            PackageMinutes = 60,
            PackagePriceMinorUnits = 10_000
        };
        await _store.ApplyCloudCommandAsync(new EdgeCommand
        {
            Id = "ecm_pkg",
            Kind = EdgeCommandKind.StartSession,
            IssuedAtUtc = _time.Now,
            ExpiresAtUtc = _time.Now.AddMinutes(10),
            StartSession = new StartSessionCommand
            {
                SessionId = "ses_pkg",
                DeviceId = DeviceId,
                PriceSnapshot = snapshot,
                Actor = "user:x",
                CorrelationId = "cor_pkg",
                DurationMinutes = 60
            }
        });

        // Агент видит пакет в снимке — индикатор на ПК считает так же.
        Assert.Equal("1 час", _store.GetAgentState(DeviceId)!.Session!.PriceSnapshot.PackageName);
        Assert.Equal(10_000, LastEvent<SessionStartedPayload>(EventTypes.SessionStarted).PriceSnapshot.PackagePriceMinorUnits);

        // Продление на 15 мин и полный лимит: пакет + 15 мин × 120/час.
        await _store.ExtendSessionAsync("ses_pkg", 15, "op");
        _time.Advance(TimeSpan.FromMinutes(75));
        var ended = Assert.Single(await _store.EndExpiredSessionsAsync());
        Assert.Equal(10_000 + 3_000, ended.TotalMinorUnits);
    }

    [Fact]
    public async Task Package_session_ended_early_costs_the_package_price()
    {
        var snapshot = _store.GetZonePrice("zone_std")! with { PackageId = "pkg_1", PackageName = "3 часа", PackageMinutes = 180, PackagePriceMinorUnits = 25_000 };
        await _store.ApplyCloudCommandAsync(new EdgeCommand
        {
            Id = "ecm_pkg2",
            Kind = EdgeCommandKind.StartSession,
            IssuedAtUtc = _time.Now,
            ExpiresAtUtc = _time.Now.AddMinutes(10),
            StartSession = new StartSessionCommand
            {
                SessionId = "ses_pkg2",
                DeviceId = DeviceId,
                PriceSnapshot = snapshot,
                Actor = "user:x",
                CorrelationId = "cor_pkg2",
                DurationMinutes = 180
            }
        });
        _time.Advance(TimeSpan.FromMinutes(20));
        var result = await _store.EndSessionAsync("ses_pkg2", "op");
        Assert.Equal(25_000, result.Session!.TotalMinorUnits);
    }

    [Fact]
    public async Task Agent_state_reflects_session_lifecycle()
    {
        var idle = _store.GetAgentState(DeviceId)!;
        Assert.Equal(AgentDeviceState.NoSessionStamp, idle.Stamp);
        Assert.Equal("PC-01", idle.DeviceName);
        Assert.Equal("Dushanbe Pilot", idle.LocationName);
        Assert.Null(idle.Session);
        Assert.Null(idle.LastEnded);

        var session = await StartLimited(30);
        var active = _store.GetAgentState(DeviceId)!;
        Assert.Equal(session.SessionId, active.Session!.SessionId);
        Assert.Equal(session.PlannedEndAtUtc, active.Session.PlannedEndAtUtc);
        Assert.Equal(12_000, active.Session.PriceSnapshot.PricePerHourMinorUnits);
        Assert.NotEqual(idle.Stamp, active.Stamp);

        await _store.ExtendSessionAsync(session.SessionId, 10, "op");
        var extended = _store.GetAgentState(DeviceId)!;
        Assert.NotEqual(active.Stamp, extended.Stamp); // продление будит long-poll агента

        _time.Advance(TimeSpan.FromMinutes(12));
        await _store.EndSessionAsync(session.SessionId, "op");
        var ended = _store.GetAgentState(DeviceId)!;
        Assert.Null(ended.Session);
        Assert.Equal(AgentDeviceState.NoSessionStamp, ended.Stamp);
        Assert.Equal(session.SessionId, ended.LastEnded!.SessionId);
        Assert.Equal(2_400, ended.LastEnded.TotalMinorUnits);
        Assert.Equal(SessionEndReasons.Staff, ended.LastEnded.Reason);

        _time.Advance(EdgeStore.LastEndedVisibleFor + TimeSpan.FromSeconds(1));
        Assert.Null(_store.GetAgentState(DeviceId)!.LastEnded);
    }

    [Fact]
    public void Agent_state_for_unknown_device_is_null() => Assert.Null(_store.GetAgentState("dev_unknown"));

    [Fact]
    public async Task Maintenance_heartbeat_status_is_kept()
    {
        await _store.RecordHeartbeatAsync(new HeartbeatMessage
        {
            DeviceId = DeviceId,
            Status = DeviceStatus.Maintenance,
            ClockUtc = _time.Now
        });
        Assert.Equal(DeviceStatus.Maintenance, _store.GetDevice(DeviceId)!.AgentStatus);

        // Активная сессия важнее статуса агента в отчёте Cloud.
        await StartLimited(10);
        Assert.Equal(DeviceStatus.Active, _store.BuildStatusEntries().Single().Status);
    }
}

/// <summary>Совместимость версий: Cloud новее Edge не блокирует очередь Cloud → Edge.</summary>
public class PulledCommandsTests
{
    [Fact]
    public void Unknown_command_kind_is_skipped_without_blocking_known_ones()
    {
        var json = JsonDocument.Parse("""
            [
              {"id":"ecm_new","kind":"TeleportSession","issuedAtUtc":"2026-09-29T10:00:00Z","expiresAtUtc":"2026-09-29T10:10:00Z"},
              {"id":"ecm_end","kind":"EndSession","issuedAtUtc":"2026-09-29T10:00:00Z","expiresAtUtc":"2026-09-29T10:10:00Z",
               "endSession":{"sessionId":"ses_1","actor":"user:x","correlationId":"cor_1"}},
              {"id":"ecm_num","kind":99,"issuedAtUtc":"2026-09-29T10:00:00Z","expiresAtUtc":"2026-09-29T10:10:00Z"},
              "garbage"
            ]
            """).RootElement.EnumerateArray().ToList();

        var parsed = ClubOS.EdgeController.Cloud.PulledCommands.Parse(json);

        var known = Assert.Single(parsed.Commands);
        Assert.Equal("ecm_end", known.Id);
        Assert.Equal(EdgeCommandKind.EndSession, known.Kind);
        Assert.Equal(["ecm_new", "ecm_num", "?"], parsed.Unsupported);
    }
}
