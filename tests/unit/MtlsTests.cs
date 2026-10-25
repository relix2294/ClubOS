using System.Security.Cryptography.X509Certificates;
using ClubOS.Contracts;
using ClubOS.EdgeController;
using ClubOS.EdgeController.Api;
using ClubOS.EdgeController.Storage;
using ClubOS.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>
/// mTLS и CRL на Edge (D-002): клиентский сертификат ПК — от CA устройства, этого же устройства, действует и не
/// отозван; режим «только с сертификатом»; отозванный по CRL сертификат не проходит даже при живой конфигурации.
/// </summary>
public class MtlsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly DevCertificateAuthority _ca;
    private readonly EdgeOptions _options;
    private readonly EdgeDatabase _db;
    private readonly EdgeStore _store;
    private readonly EdgeCrlStore _crl;
    private readonly DisklessAuthority _diskless;
    private readonly AgentAuth _auth;
    private readonly DeviceKey _key = DeviceKey.Generate();
    private readonly IssuedCertificate _cert;

    public MtlsTests()
    {
        _ca = DevCertificateAuthority.LoadOrCreate(Path.Combine(_dir.Path, "ca"), _time);
        _options = new EdgeOptions { DataPath = Path.Combine(_dir.Path, "edge") };
        var identity = new EdgeIdentityStore(_options.DataPath);
        identity.GetOrCreatePendingKey();
        identity.Save(new EdgeIdentity
        {
            EdgeId = "edge_1",
            TenantId = "org_1",
            LocationId = "loc",
            CertificatePem = "cert",
            CaCertificatePem = _ca.CertificatePem,
            CertificateExpiresAtUtc = _time.GetUtcNow().AddDays(90)
        });
        _db = new EdgeDatabase(_options.DataPath);
        _store = new EdgeStore(_db, new EdgeSignals(), _time);
        _crl = new EdgeCrlStore(_options.DataPath);
        _diskless = new DisklessAuthority(Options.Create(_options), _time);
        _auth = new AgentAuth(identity, _store, _time, Options.Create(_options), _diskless, _crl, NullLogger<AgentAuth>.Instance);
        _cert = Issue(_ca, _key, "dev_1");
    }

    public void Dispose()
    {
        _key.Dispose();
        _diskless.Dispose();
        _db.Dispose();
        _ca.Dispose();
        _dir.Dispose();
    }

    private static IssuedCertificate Issue(DevCertificateAuthority ca, DeviceKey key, string deviceId) =>
        ca.Issue(key.CreateSigningRequestPem(deviceId), deviceId, DevCertificateAuthority.RoleDevice, TimeSpan.FromDays(90));

    private static HttpContext Request(IssuedCertificate? client, bool https = true)
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = https ? "https" : "http";
        http.Request.Method = "GET";
        http.Request.Path = "/agent/v1/commands";
        if (client is not null)
        {
            http.Connection.ClientCertificate = X509Certificate2.CreateFromPem(client.CertificatePem);
        }

        return http;
    }

    private void Revoke(params string[] serials) =>
        Assert.True(_crl.Apply(_ca.CreateCrl(serials.Select(s => new RevokedSerial(s, _time.GetUtcNow(),
            X509RevocationReason.KeyCompromise)), TimeSpan.FromDays(7)), _ca.CertificatePem));

    [Fact]
    public void Client_certificate_must_be_this_device_from_the_ca_valid_and_not_revoked()
    {
        Assert.Null(_auth.CheckClientCertificate(Request(_cert), "dev_1", disklessDevice: false));

        // Сертификат другого ПК с токеном dev_1.
        using var otherKey = DeviceKey.Generate();
        Assert.Contains("другому устройству", _auth.CheckClientCertificate(Request(Issue(_ca, otherKey, "dev_2")), "dev_1", false));

        // Чужой CA с тем же CN.
        using var foreignDir = new TempDir();
        using var foreign = DevCertificateAuthority.LoadOrCreate(foreignDir.Path, _time);
        Assert.Contains("не CA ClubOS", _auth.CheckClientCertificate(Request(Issue(foreign, _key, "dev_1")), "dev_1", false));

        // Серверный сертификат (serverAuth) не годится как клиентский.
        var server = _ca.IssueServer(_key.CreateSigningRequestPem("dev_1"), "dev_1", ["dev-1"], [], TimeSpan.FromDays(90));
        Assert.Contains("не CA ClubOS", _auth.CheckClientCertificate(Request(server), "dev_1", false));

        // Истёк.
        _time.Advance(TimeSpan.FromDays(91));
        Assert.Contains("истёк", _auth.CheckClientCertificate(Request(_cert), "dev_1", false));
    }

    [Fact]
    public void Revoked_client_certificate_is_rejected_and_crl_survives_restart()
    {
        using var otherKey = DeviceKey.Generate();
        var other = Issue(_ca, otherKey, "dev_1");
        Revoke(_cert.SerialHex);

        Assert.Contains("отозван", _auth.CheckClientCertificate(Request(_cert), "dev_1", false));
        Assert.Null(_auth.CheckClientCertificate(Request(other), "dev_1", false));

        // Перезапуск Edge: список из файла, более старый список не заменяет свежий, чужой — отклоняется.
        var restarted = new EdgeCrlStore(_options.DataPath);
        Assert.True(restarted.TryLoad(_ca.CertificatePem));
        Assert.True(restarted.IsRevoked(X509Certificate2.CreateFromPem(_cert.CertificatePem)));
        var older = _ca.CreateCrl([], TimeSpan.FromDays(7));
        _time.Advance(TimeSpan.FromSeconds(1));
        Revoke(_cert.SerialHex, other.SerialHex);
        Assert.False(_crl.Apply(older, _ca.CertificatePem));
        Assert.Equal(2, _crl.Current!.RevokedSerials.Count);
        using var foreignDir = new TempDir();
        using var foreign = DevCertificateAuthority.LoadOrCreate(foreignDir.Path, _time);
        Assert.Throws<InvalidCrlException>(() => _crl.Apply(foreign.CreateCrl([], TimeSpan.FromDays(7)), _ca.CertificatePem));
    }

    [Fact]
    public void Without_certificate_only_when_not_required()
    {
        Assert.Null(_auth.CheckClientCertificate(Request(null), "dev_1", false));
        Assert.Null(_auth.CheckClientCertificate(Request(null, https: false), "dev_1", false));

        _options.RequireAgentClientCertificate = true;
        Assert.Contains("нет клиентского сертификата", _auth.CheckClientCertificate(Request(null), "dev_1", false));
        Assert.Contains("без TLS", _auth.CheckClientCertificate(Request(null, https: false), "dev_1", false));
        Assert.Null(_auth.CheckClientCertificate(Request(_cert), "dev_1", false));
    }

    [Fact]
    public void Diskless_device_presents_certificate_of_the_edge_local_ca()
    {
        using var key = DeviceKey.Generate();
        var local = Issue(_diskless.Ca, key, "dev_dl");
        Assert.Null(_auth.CheckClientCertificate(Request(local), "dev_dl", disklessDevice: true));
        Assert.Contains("не CA ClubOS", _auth.CheckClientCertificate(Request(Issue(_ca, key, "dev_dl")), "dev_dl", true));
    }

    [Fact]
    public async Task Token_signed_with_revoked_certificate_is_rejected_even_with_live_config()
    {
        await _store.SaveConfigAsync(new EdgeConfigResponse
        {
            LocationId = "loc",
            LocationName = "Club",
            Timezone = "Asia/Dushanbe",
            Currency = "TJS",
            Zones = [new EdgeZoneConfig { ZoneId = "z", Name = "Z", PricePerHourMinorUnits = 100, Rounding = RoundingRule.CeilingPerMinute, RuleVersion = 1 }],
            Devices = [new EdgeDeviceConfig { DeviceId = "dev_1", DisplayName = "PC", ZoneId = "z", Simulated = false, CertificatePem = _cert.CertificatePem }]
        });

        HttpContext Signed()
        {
            var http = Request(null);
            http.Request.Headers.Authorization = $"{SignedToken.Scheme} {SignedToken.Create("dev_1", _key.Key, SignedToken.AudienceEdge, _time)}";
            return http;
        }

        Assert.Equal("dev_1", _auth.Authenticate(Signed()));
        Revoke(_cert.SerialHex);
        Assert.Null(_auth.Authenticate(Signed()));
    }
}
