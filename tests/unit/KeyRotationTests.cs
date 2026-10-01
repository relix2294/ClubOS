using ClubOS.Contracts;
using ClubOS.EdgeController.Storage;
using ClubOS.Security;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>Ротация ключа при продлении (D-011): прежний сертификат на Edge и защита от устаревшей конфигурации.</summary>
public class KeyRotationTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly EdgeDatabase _db;
    private readonly EdgeStore _store;
    private readonly DevCertificateAuthority _ca;

    public KeyRotationTests()
    {
        _db = new EdgeDatabase(Path.Combine(_dir.Path, "edge"));
        _store = new EdgeStore(_db, new EdgeSignals(), _time);
        _ca = DevCertificateAuthority.LoadOrCreate(Path.Combine(_dir.Path, "ca"), _time);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dir.Dispose();
    }

    private string Issue(DeviceKey key) =>
        _ca.Issue(key.CreateSigningRequestPem("d"), "dev_1", DevCertificateAuthority.RoleDevice, TimeSpan.FromDays(90)).CertificatePem;

    private Task Configure(string certificate) => _store.SaveConfigAsync(new EdgeConfigResponse
    {
        LocationId = "loc",
        LocationName = "Club",
        Timezone = "Asia/Dushanbe",
        Currency = "TJS",
        Zones = [new EdgeZoneConfig { ZoneId = "z", Name = "Z", PricePerHourMinorUnits = 100, Rounding = RoundingRule.CeilingPerMinute, RuleVersion = 1 }],
        Devices = [new EdgeDeviceConfig { DeviceId = "dev_1", DisplayName = "PC", ZoneId = "z", Simulated = false, CertificatePem = certificate }]
    });

    [Fact]
    public void Same_public_key_and_key_match()
    {
        using var a = DeviceKey.Generate();
        using var b = DeviceKey.Generate();
        var certA1 = Issue(a);
        var certA2 = Issue(a);
        Assert.True(DeviceKey.SamePublicKey(certA1, certA2));
        Assert.False(DeviceKey.SamePublicKey(certA1, Issue(b)));
        Assert.True(a.Matches(certA1));
        Assert.False(b.Matches(certA1));
    }

    [Fact]
    public async Task Rotation_keeps_previous_until_forgotten_and_stale_config_does_not_win()
    {
        using var oldKey = DeviceKey.Generate();
        using var newKey = DeviceKey.Generate();
        using var newerKey = DeviceKey.Generate();
        var oldCert = Issue(oldKey);
        await Configure(oldCert);

        // Продление с тем же ключом (старые агенты) — не ротация.
        var sameKey = Issue(oldKey);
        await _store.UpdateDeviceCertificateAsync("dev_1", sameKey);
        Assert.Null(_store.GetDevice("dev_1")!.PreviousCertificatePem);

        var newCert = Issue(newKey);
        await _store.UpdateDeviceCertificateAsync("dev_1", newCert);
        Assert.Equal(newCert, _store.GetDevice("dev_1")!.CertificatePem);
        Assert.Equal(sameKey, _store.GetDevice("dev_1")!.PreviousCertificatePem);

        // Конфигурация, прочитанная до продления, несёт прежний сертификат — новый остаётся.
        await Configure(sameKey);
        Assert.Equal(newCert, _store.GetDevice("dev_1")!.CertificatePem);

        // Повторное продление, подписанное прежним ключом (ответ потерялся): прежний — тот же.
        var newerCert = Issue(newerKey);
        await _store.UpdateDeviceCertificateAsync("dev_1", newerCert, signedWithPreviousKey: true);
        Assert.Equal(newerCert, _store.GetDevice("dev_1")!.CertificatePem);
        Assert.Equal(sameKey, _store.GetDevice("dev_1")!.PreviousCertificatePem);

        await _store.ForgetPreviousDeviceCertificateAsync("dev_1");
        Assert.Null(_store.GetDevice("dev_1")!.PreviousCertificatePem);
    }
}
