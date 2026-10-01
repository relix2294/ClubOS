using ClubOS.Agent.Core;
using ClubOS.Contracts;
using ClubOS.EdgeController.Storage;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>Бездисковые ПК (D-018): нормализация MAC, кандидаты на Edge, привязка из конфигурации, RefreshConfig.</summary>
public class DisklessTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
    private readonly EdgeDatabase _db;
    private readonly EdgeSignals _signals = new();
    private readonly EdgeStore _store;

    public DisklessTests()
    {
        _db = new EdgeDatabase(_dir.Path);
        _store = new EdgeStore(_db, _signals, _time);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dir.Dispose();
    }

    private static DisklessBootRequest Boot(string mac, string host = "DESKTOP-1") => new()
    {
        HardwareId = mac,
        MacAddresses = [mac, "0A-0B-0C-0D-0E-0F"],
        Inventory = new DeviceInventory
        {
            Hostname = host,
            WindowsVersion = "Windows 11",
            Cpu = "CPU",
            RamMegabytes = 8192,
            Ipv4 = "192.168.1.50",
            AgentVersion = "1"
        },
        CertificateSigningRequestPem = "csr"
    };

    private static EdgeConfigResponse Config(params EdgeDeviceConfig[] devices) => new()
    {
        LocationId = "loc_1",
        LocationName = "Test",
        Timezone = "Asia/Dushanbe",
        Currency = "TJS",
        Zones = [new EdgeZoneConfig { ZoneId = "zone_std", Name = "Standard", PricePerHourMinorUnits = 12_000, Rounding = RoundingRule.CeilingPerMinute, RuleVersion = 1 }],
        Devices = devices
    };

    private static EdgeDeviceConfig Diskless(string id, string mac) => new()
    {
        DeviceId = id,
        DisplayName = id,
        ZoneId = "zone_std",
        Simulated = false,
        CertificatePem = string.Empty,
        HardwareId = mac
    };

    [Theory]
    [InlineData("0a:1b:2c:3d:4e:5f", "0A1B2C3D4E5F")]
    [InlineData("0A-1B-2C-3D-4E-5F", "0A1B2C3D4E5F")]
    [InlineData("0a1b.2c3d.4e5f", "0A1B2C3D4E5F")]
    [InlineData("0A1B2C3D4E5F", "0A1B2C3D4E5F")]
    [InlineData("00:00:00:00:00:00", null)]
    [InlineData("FF:FF:FF:FF:FF:FF", null)]
    [InlineData("0A:1B:2C:3D:4E", null)]
    [InlineData("0A:1B:2C:3D:4E:5G", null)]
    [InlineData("zz0a1b2c3d4e5f", null)]
    [InlineData(null, null)]
    public void Mac_is_normalized_or_rejected(string? input, string? expected) =>
        Assert.Equal(expected, HardwareIds.NormalizeMac(input));

    [Fact]
    public void Mac_is_formatted_for_people() => Assert.Equal("0A:1B:2C:3D:4E:5F", HardwareIds.FormatMac("0A1B2C3D4E5F"));

    [Fact]
    public void Hardware_id_override_wins_and_is_normalized()
    {
        var info = new NetworkHardwareIdentity(new AgentOptions { HardwareIdOverride = "02-c1-0b-5d-00-01" }).Collect();
        Assert.Equal("02C10B5D0001", info.HardwareId);
        Assert.Equal("02C10B5D0001", info.MacAddresses[0]);
    }

    [Fact]
    public async Task Unknown_pc_becomes_candidate_and_disappears_when_bound_by_config()
    {
        Assert.True(await _store.RecordDisklessCandidateAsync(Boot("02AA00000001"), "02AA00000001"));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await _store.RecordDisklessCandidateAsync(Boot("02AA00000001", "DESKTOP-2"), "02AA00000001"));

        var candidate = Assert.Single(_store.ListDisklessCandidates(TimeSpan.FromMinutes(10)));
        Assert.Equal("DESKTOP-2", candidate.Hostname);
        Assert.Equal(["02AA00000001", "0A0B0C0D0E0F"], candidate.MacAddresses);
        Assert.Equal(_time.GetUtcNow().AddMinutes(-1), candidate.FirstSeenUtc);

        await _store.SaveConfigAsync(Config(Diskless("dev_dl", "02:AA:00:00:00:01")));
        Assert.Empty(_store.ListDisklessCandidates(TimeSpan.FromMinutes(10)));
        var device = _store.FindDeviceByHardware("02AA00000001");
        Assert.NotNull(device);
        Assert.True(device.Diskless);
        Assert.Null(device.LocalCertificatePem); // сертификат будет при загрузке
    }

    [Fact]
    public async Task Local_certificate_survives_config_refresh_but_not_rebinding()
    {
        await _store.SaveConfigAsync(Config(Diskless("dev_dl", "02AA00000001")));
        await _store.SetLocalCertificateAsync("dev_dl", "local-cert", Boot("02AA00000001").Inventory);

        await _store.SaveConfigAsync(Config(Diskless("dev_dl", "02AA00000001")));
        Assert.Equal("local-cert", _store.GetDevice("dev_dl")!.LocalCertificatePem);

        // MAC перешёл к новому устройству (старое удалено в Admin Web): старое его теряет, сертификат сбрасывается.
        await _store.SaveConfigAsync(Config(Diskless("dev_dl", "02AA00000001"), Diskless("dev_new", "02AA00000001")));
        Assert.Equal("dev_new", _store.FindDeviceByHardware("02AA00000001")!.DeviceId);
        Assert.Null(_store.GetDevice("dev_new")!.LocalCertificatePem);
        Assert.False(_store.GetDevice("dev_dl")!.Diskless);
    }

    [Fact]
    public async Task Candidates_are_capped_listed_by_recency_and_pruned()
    {
        for (var i = 0; i < EdgeStore.MaxDisklessCandidates; i++)
        {
            Assert.True(await _store.RecordDisklessCandidateAsync(Boot($"02AA{i:X8}"), $"02AA{i:X8}"));
        }

        Assert.False(await _store.RecordDisklessCandidateAsync(Boot("02BB00000001"), "02BB00000001"));
        Assert.True(await _store.RecordDisklessCandidateAsync(Boot("02AA00000000"), "02AA00000000")); // известный — обновляется

        _time.Advance(TimeSpan.FromMinutes(11));
        Assert.Empty(_store.ListDisklessCandidates(TimeSpan.FromMinutes(10)));
        _time.Advance(TimeSpan.FromDays(8));
        Assert.Equal(EdgeStore.MaxDisklessCandidates, await _store.PruneDisklessCandidatesAsync(TimeSpan.FromDays(7)));
    }

    [Fact]
    public async Task Refresh_config_command_wakes_the_config_loop()
    {
        var wait = _signals.WaitConfigAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.False(wait.IsCompleted);
        await _store.ApplyCloudCommandAsync(new EdgeCommand
        {
            Id = "ecm_1",
            Kind = EdgeCommandKind.RefreshConfig,
            IssuedAtUtc = _time.GetUtcNow(),
            ExpiresAtUtc = _time.GetUtcNow().AddDays(1)
        });
        Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5))));
    }
}
