using ClubOS.Contracts;
using ClubOS.EdgeController.Storage;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>Edge забывает удалённое устройство (D-011): сессия закрыта, команды провалены, сертификат удалён.</summary>
public class EdgeRevocationTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
    private readonly EdgeDatabase _db;
    private readonly EdgeStore _store;

    public EdgeRevocationTests()
    {
        _db = new EdgeDatabase(_dir.Path);
        _store = new EdgeStore(_db, new EdgeSignals(), _time);
        _store.SaveConfigAsync(Config([])).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _dir.Dispose();
    }

    private static EdgeConfigResponse Config(IReadOnlyList<string> revoked) => new()
    {
        LocationId = "loc_1",
        LocationName = "Club",
        Timezone = "Asia/Dushanbe",
        Currency = "TJS",
        Zones = [new EdgeZoneConfig { ZoneId = "zone_1", Name = "Standard", PricePerHourMinorUnits = 12_000, Rounding = RoundingRule.CeilingPerMinute, RuleVersion = 1 }],
        Devices = revoked.Contains("dev_1") ? [] : [new EdgeDeviceConfig { DeviceId = "dev_1", DisplayName = "PC-01", ZoneId = "zone_1", Simulated = false, CertificatePem = "cert" }],
        RevokedDeviceIds = revoked
    };

    [Fact]
    public async Task Revoke_ends_offline_session_fails_commands_and_removes_device()
    {
        var session = (await _store.StartLocalSessionAsync("dev_1", "edge-cli:op")).Session!;
        await _store.ApplyCloudCommandAsync(new EdgeCommand
        {
            Id = "ecm_cmd",
            Kind = EdgeCommandKind.DeviceCommand,
            IssuedAtUtc = _time.Now,
            ExpiresAtUtc = _time.Now.AddMinutes(5),
            DeviceCommand = new CommandEnvelope
            {
                CommandId = "cmd_1",
                CommandType = CommandType.ShowMessage,
                TargetDeviceIds = ["dev_1"],
                IssuedBy = "user:x",
                IssuedAtUtc = _time.Now,
                ExpiresAtUtc = _time.Now.AddMinutes(5),
                CorrelationId = "cor_1",
                Payload = ContractJson.ToElement(new ShowMessagePayload { Title = "t", Message = "m" })
            }
        });
        _time.Advance(TimeSpan.FromMinutes(2));

        Assert.True(await _store.RevokeDeviceAsync("dev_1", "user:owner"));

        Assert.Null(_store.GetDevice("dev_1"));
        var ended = _store.GetSession(session.SessionId)!;
        Assert.Equal(SessionState.Ended, ended.State);
        Assert.Equal(400, ended.TotalMinorUnits); // 2 минуты досчитаны, деньги не потеряны
        Assert.Equal(CommandState.Failed, _store.GetCommandState("cmd_1"));
        Assert.False(await _store.RevokeDeviceAsync("dev_1", "user:owner")); // повтор — без эффекта
    }

    [Fact]
    public async Task Revoked_ids_in_config_remove_device()
    {
        Assert.NotNull(_store.GetDevice("dev_1"));
        await _store.SaveConfigAsync(Config(["dev_1"]));
        Assert.Null(_store.GetDevice("dev_1"));
    }

    [Fact]
    public async Task Cloud_revoke_command_is_idempotent()
    {
        var command = new EdgeCommand
        {
            Id = "ecm_revoke",
            Kind = EdgeCommandKind.RevokeDevice,
            IssuedAtUtc = _time.Now,
            ExpiresAtUtc = _time.Now.AddDays(30),
            RevokeDevice = new RevokeDeviceCommand { DeviceId = "dev_1", Actor = "user:owner" }
        };
        Assert.True(await _store.ApplyCloudCommandAsync(command));
        Assert.False(await _store.ApplyCloudCommandAsync(command));
        Assert.Null(_store.GetDevice("dev_1"));
    }

    [Fact]
    public async Task Certificate_update_replaces_stored_pem()
    {
        await _store.UpdateDeviceCertificateAsync("dev_1", "new-cert");
        Assert.Equal("new-cert", _store.GetDevice("dev_1")!.CertificatePem);
    }
}
