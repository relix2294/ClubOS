using System.Security.Cryptography.X509Certificates;
using ClubOS.Security;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>Список отзыва (D-002): CA подписывает CRL, CrlReader проверяет подпись, издателя и читает серийные номера.</summary>
public class CrlTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly DevCertificateAuthority _ca;

    public CrlTests() => _ca = DevCertificateAuthority.LoadOrCreate(Path.Combine(_dir.Path, "ca"), _time);

    public void Dispose()
    {
        _ca.Dispose();
        _dir.Dispose();
    }

    private IssuedCertificate Issue(string id)
    {
        using var key = DeviceKey.Generate();
        return _ca.Issue(key.CreateSigningRequestPem(id), id, DevCertificateAuthority.RoleDevice, TimeSpan.FromDays(90));
    }

    [Fact]
    public void Roundtrip_lists_revoked_serials_only()
    {
        var revoked = Issue("dev_1");
        var alive = Issue("dev_2");
        Assert.Equal(DevCertificateAuthority.SerialOf(revoked.CertificatePem), revoked.SerialHex);

        var der = _ca.CreateCrl([new RevokedSerial(revoked.SerialHex, _time.GetUtcNow(), X509RevocationReason.CessationOfOperation)],
            TimeSpan.FromDays(7));
        var crl = CrlReader.Read(der, _ca.Certificate);

        Assert.True(crl.IsRevoked(revoked.CertificatePem));
        Assert.False(crl.IsRevoked(alive.CertificatePem));
        Assert.Single(crl.RevokedSerials);
        Assert.Equal(_time.GetUtcNow().AddDays(7), crl.NextUpdateUtc);
        Assert.True(crl.ThisUpdateUtc <= _time.GetUtcNow());
    }

    [Fact]
    public void Empty_list_and_number_grows_with_time()
    {
        var first = CrlReader.Read(_ca.CreateCrl([], TimeSpan.FromDays(7)), _ca.Certificate);
        _time.Advance(TimeSpan.FromSeconds(1));
        var second = CrlReader.Read(_ca.CreateCrl([], TimeSpan.FromDays(7)), _ca.Certificate);
        Assert.Empty(first.RevokedSerials);
        Assert.True(second.Number > first.Number);
    }

    [Fact]
    public void Many_entries_and_serial_with_leading_zero_byte()
    {
        var serials = Enumerable.Range(0, 200).Select(_ => Issue("dev_x").SerialHex).ToList();
        serials.Add("00" + "7F".PadRight(30, 'A')); // лишний ведущий ноль — нормализуется
        Assert.Equal("7F".PadRight(30, 'A'), DevCertificateAuthority.NormalizeSerial(serials[^1]));
        Assert.Equal("0080", DevCertificateAuthority.NormalizeSerial("000080"));
        var crl = CrlReader.Read(_ca.CreateCrl(serials.Select(s => new RevokedSerial(s, _time.GetUtcNow(),
            X509RevocationReason.Superseded)), TimeSpan.FromDays(1)), _ca.Certificate);
        Assert.Equal(201, crl.RevokedSerials.Count);
        Assert.All(serials, s => Assert.Contains(DevCertificateAuthority.NormalizeSerial(s), crl.RevokedSerials));
    }

    [Fact]
    public void Tampered_or_foreign_crl_is_rejected()
    {
        var target = Issue("dev_1");
        var der = _ca.CreateCrl([new RevokedSerial(target.SerialHex, _time.GetUtcNow(), X509RevocationReason.KeyCompromise)],
            TimeSpan.FromDays(7));

        // Подмена серийного номера внутри подписанной части.
        var serialBytes = Convert.FromHexString(target.SerialHex);
        var at = der.AsSpan().IndexOf(serialBytes);
        Assert.True(at > 0);
        var tampered = (byte[])der.Clone();
        tampered[at + 3] ^= 0x01;
        Assert.Throws<InvalidCrlException>(() => CrlReader.Read(tampered, _ca.Certificate));

        // Мусор и обрезанный список.
        Assert.Throws<InvalidCrlException>(() => CrlReader.Read([0x30, 0x03, 0x02, 0x01, 0x01], _ca.Certificate));
        Assert.Throws<InvalidCrlException>(() => CrlReader.Read(der[..^5], _ca.Certificate));

        // Подписан другим CA (тот же subject, другой ключ).
        using var otherDir = new TempDir();
        using var other = DevCertificateAuthority.LoadOrCreate(otherDir.Path, _time);
        Assert.Throws<InvalidCrlException>(() => CrlReader.Read(other.CreateCrl([], TimeSpan.FromDays(7)), _ca.Certificate));
    }
}
