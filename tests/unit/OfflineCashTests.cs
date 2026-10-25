using System.Text.Json;
using ClubOS.Contracts;
using ClubOS.EdgeController.Storage;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>Касса Edge без интернета (D-023): PIN, кассиры из конфигурации, долг с учётом Cloud и Edge, оплаты в outbox.</summary>
public class OfflineCashTests : IDisposable
{
    private const string DeviceId = "dev_1";
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly EdgeDatabase _db;
    private readonly EdgeStore _store;
    private readonly OfflineStaffMember _cashier;

    public OfflineCashTests()
    {
        _db = new EdgeDatabase(_dir.Path);
        _store = new EdgeStore(_db, new EdgeSignals(), _time);
        _store.SaveConfigAsync(Config(new EdgeOfflineStaff { UserId = "u1", DisplayName = "Кассир", PinHash = OfflinePin.Hash("583914", 1_000) }))
            .GetAwaiter().GetResult();
        _cashier = _store.GetOfflineStaff("u1")!;
    }

    public void Dispose()
    {
        _db.Dispose();
        _dir.Dispose();
    }

    private static EdgeConfigResponse Config(params EdgeOfflineStaff[] staff) => new()
    {
        LocationId = "loc_1",
        LocationName = "Club",
        Timezone = "Asia/Dushanbe",
        Currency = "TJS",
        Zones = [new EdgeZoneConfig { ZoneId = "z", Name = "Standard", PricePerHourMinorUnits = 12_000, Rounding = RoundingRule.CeilingPerMinute, RuleVersion = 1 }],
        Devices = [new EdgeDeviceConfig { DeviceId = DeviceId, DisplayName = "PC-01", ZoneId = "z", Simulated = false, CertificatePem = "c" }],
        OfflineStaff = staff
    };

    private async Task<string> EndedSession(int minutes)
    {
        var started = await _store.StartLocalSessionAsync(DeviceId, "edge-cli", null);
        _time.Advance(TimeSpan.FromMinutes(minutes));
        await _store.EndSessionAsync(started.Session!.SessionId, "edge-cli");
        return started.Session.SessionId;
    }

    [Theory]
    [InlineData("583914", true)]
    [InlineData("12345678", false)] // подряд
    [InlineData("111111", false)]
    [InlineData("12345", false)]
    [InlineData("12a456", false)]
    [InlineData("987654", false)]
    public void Pin_format_rejects_short_and_trivial(string pin, bool valid) => Assert.Equal(valid, OfflinePin.IsValidFormat(pin));

    [Fact]
    public void Pin_hash_verifies_only_the_same_pin()
    {
        var hash = OfflinePin.Hash("583914", 1_000);
        Assert.True(OfflinePin.Verify("583914", hash));
        Assert.False(OfflinePin.Verify("583915", hash));
        Assert.False(OfflinePin.Verify("583914", "garbage"));
        Assert.False(OfflinePin.Verify("583914", null));
        Assert.NotEqual(hash, OfflinePin.Hash("583914", 1_000)); // соль
    }

    [Fact]
    public async Task Config_replaces_offline_staff()
    {
        Assert.Single(_store.ListOfflineStaff());
        await _store.SaveConfigAsync(Config());
        Assert.Empty(_store.ListOfflineStaff());
        Assert.Null(_store.GetOfflineStaff("u1"));
    }

    [Fact]
    public async Task Due_counts_cloud_and_edge_payments_and_payment_goes_to_outbox()
    {
        var sessionId = await EndedSession(10); // 10 мин × 120/час = 20,00
        Assert.Equal(2_000, _store.GetOfflinePayable().Single().DueMinorUnits);

        // Cloud сообщил об оплате 5,00 (CashSync).
        await _store.ApplyCloudCommandAsync(new EdgeCommand
        {
            Id = "ecm_sync",
            Kind = EdgeCommandKind.CashSync,
            IssuedAtUtc = _time.Now,
            ExpiresAtUtc = _time.Now.AddDays(1),
            CashSync = new CashSyncCommand { SessionId = sessionId, PaidMinorUnits = 500 }
        });
        Assert.Equal(1_500, _store.GetOfflinePayable().Single().DueMinorUnits);

        var tooMuch = await _store.RecordOfflinePaymentAsync(sessionId, 1_600, "Cash", _cashier, "key-00000001");
        Assert.Equal(OfflinePaymentOutcome.ExceedsDue, tooMuch.Outcome);
        var paid = await _store.RecordOfflinePaymentAsync(sessionId, 1_000, "Card", _cashier, "key-00000002");
        Assert.Equal(OfflinePaymentOutcome.Recorded, paid.Outcome);
        Assert.Equal(500, paid.Due);
        var replay = await _store.RecordOfflinePaymentAsync(sessionId, 1_000, "Card", _cashier, "key-00000002");
        Assert.Equal(OfflinePaymentOutcome.Replayed, replay.Outcome);
        Assert.Equal(paid.Payment!.PaymentId, replay.Payment!.PaymentId);
        Assert.Equal(500, _store.GetOfflinePayable().Single().DueMinorUnits);

        var evt = _store.GetPendingEvents(100).Single(e => e.EventType == EventTypes.OfflinePaymentRecorded);
        var payload = JsonSerializer.Deserialize<OfflinePaymentRecordedPayload>(evt.PayloadJson, ContractJson.Options)!;
        Assert.Equal(1_000, payload.AmountMinorUnits);
        Assert.Equal("u1", payload.UserId);
        Assert.Equal(DeviceId, payload.DeviceId);
        Assert.False(Assert.Single(_store.ListOfflinePayments(10)).Sent);

        await _store.RecordOfflinePaymentAsync(sessionId, 500, "Cash", _cashier, null);
        Assert.DoesNotContain(_store.GetOfflinePayable(), p => p.DueMinorUnits > 0);
        Assert.Equal(OfflinePaymentOutcome.NotPayable, (await _store.RecordOfflinePaymentAsync(sessionId, 100, "Cash", _cashier, null)).Outcome);
        Assert.Equal(OfflinePaymentOutcome.UnknownSession, (await _store.RecordOfflinePaymentAsync("ses_x", 100, "Cash", _cashier, null)).Outcome);
    }

    [Fact]
    public async Task Old_ended_sessions_leave_the_list()
    {
        await EndedSession(5);
        _time.Advance(EdgeStore.OfflinePayableWindow + TimeSpan.FromMinutes(1));
        Assert.Empty(_store.GetOfflinePayable());
    }
}
