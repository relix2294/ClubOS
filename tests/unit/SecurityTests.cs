using System.Security.Cryptography.X509Certificates;
using ClubOS.Security;
using Xunit;

namespace ClubOS.Unit.Tests;

public class SecurityTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new(DateTimeOffset.UtcNow);

    public void Dispose() => _dir.Dispose();

    private (DevCertificateAuthority Ca, DeviceKey Key, string CertPem) Issue(string id = "dev_1", string role = DevCertificateAuthority.RoleDevice)
    {
        var ca = DevCertificateAuthority.LoadOrCreate(Path.Combine(_dir.Path, "ca"), _time);
        var key = DeviceKey.Generate();
        var cert = ca.Issue(key.CreateSigningRequestPem("ignored-hint"), id, role, TimeSpan.FromDays(30));
        return (ca, key, cert.CertificatePem);
    }

    [Fact]
    public void Ca_assigns_subject_and_ignores_csr_subject()
    {
        var (_, _, pem) = Issue("dev_abc");
        using var cert = X509Certificate2.CreateFromPem(pem);
        Assert.Equal("dev_abc", cert.GetNameInfo(X509NameType.SimpleName, false));
        Assert.Contains("OU=device", cert.Subject);
    }

    [Fact]
    public void Ca_is_persisted_and_reloaded()
    {
        var first = DevCertificateAuthority.LoadOrCreate(Path.Combine(_dir.Path, "ca"), _time);
        var second = DevCertificateAuthority.LoadOrCreate(Path.Combine(_dir.Path, "ca"), _time);
        Assert.Equal(first.Certificate.Thumbprint, second.Certificate.Thumbprint);
    }

    [Fact]
    public void Ca_rejects_garbage_csr()
    {
        var ca = DevCertificateAuthority.LoadOrCreate(Path.Combine(_dir.Path, "ca"), _time);
        Assert.Throws<InvalidCsrException>(() => ca.Issue(
            "-----BEGIN CERTIFICATE REQUEST-----\nAAAA\n-----END CERTIFICATE REQUEST-----", "x", "device", TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Valid_token_is_accepted_once_then_replay_rejected()
    {
        var (ca, key, pem) = Issue();
        var validator = new SignedTokenValidator(ca.Certificate, _time);
        var token = SignedToken.Create("dev_1", key.Key, SignedToken.AudienceEdge, _time);

        var first = validator.Validate(token, pem, SignedToken.AudienceEdge, DevCertificateAuthority.RoleDevice);
        Assert.True(first.Success, first.Error);
        Assert.Equal("dev_1", first.SubjectId);

        var replay = validator.Validate(token, pem, SignedToken.AudienceEdge, DevCertificateAuthority.RoleDevice);
        Assert.False(replay.Success);
        Assert.Equal("replayed token", replay.Error);
    }

    [Fact]
    public void Tampered_token_is_rejected()
    {
        var (ca, key, pem) = Issue();
        var token = SignedToken.Create("dev_1", key.Key, SignedToken.AudienceEdge, _time);
        var parts = token.Split('.');
        var tampered = parts[0] + "." + parts[1][..^2] + "AA" + "." + parts[2];

        var result = new SignedTokenValidator(ca.Certificate, _time)
            .Validate(tampered, pem, SignedToken.AudienceEdge, DevCertificateAuthority.RoleDevice);
        Assert.False(result.Success);
    }

    [Fact]
    public void Token_signed_by_other_key_is_rejected()
    {
        var (ca, _, pem) = Issue();
        using var attacker = DeviceKey.Generate();
        var token = SignedToken.Create("dev_1", attacker.Key, SignedToken.AudienceEdge, _time);

        var result = new SignedTokenValidator(ca.Certificate, _time)
            .Validate(token, pem, SignedToken.AudienceEdge, DevCertificateAuthority.RoleDevice);
        Assert.Equal("bad signature", result.Error);
    }

    [Fact]
    public void Certificate_from_foreign_ca_is_rejected()
    {
        var (_, key, pem) = Issue();
        var foreign = DevCertificateAuthority.LoadOrCreate(Path.Combine(_dir.Path, "other-ca"), _time);
        var token = SignedToken.Create("dev_1", key.Key, SignedToken.AudienceEdge, _time);

        var result = new SignedTokenValidator(foreign.Certificate, _time)
            .Validate(token, pem, SignedToken.AudienceEdge, DevCertificateAuthority.RoleDevice);
        Assert.Equal("certificate not issued by trusted CA", result.Error);
    }

    [Fact]
    public void Expired_token_wrong_audience_and_wrong_role_are_rejected()
    {
        var (ca, key, pem) = Issue();
        var validator = new SignedTokenValidator(ca.Certificate, _time);

        var old = SignedToken.Create("dev_1", key.Key, SignedToken.AudienceEdge, _time);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(validator.Validate(old, pem, SignedToken.AudienceEdge, DevCertificateAuthority.RoleDevice).Success);

        var wrongAud = SignedToken.Create("dev_1", key.Key, SignedToken.AudienceCloud, _time);
        Assert.False(validator.Validate(wrongAud, pem, SignedToken.AudienceEdge, DevCertificateAuthority.RoleDevice).Success);

        // Сертификат устройства не может аутентифицироваться как Edge.
        var asEdge = SignedToken.Create("dev_1", key.Key, SignedToken.AudienceEdge, _time);
        Assert.Equal("certificate subject mismatch",
            validator.Validate(asEdge, pem, SignedToken.AudienceEdge, DevCertificateAuthority.RoleEdge).Error);
    }

    [Fact]
    public void Token_claiming_other_subject_is_rejected()
    {
        var (ca, key, pem) = Issue("dev_1");
        var token = SignedToken.Create("dev_2", key.Key, SignedToken.AudienceEdge, _time);
        Assert.Equal("certificate subject mismatch", new SignedTokenValidator(ca.Certificate, _time)
            .Validate(token, pem, SignedToken.AudienceEdge, DevCertificateAuthority.RoleDevice).Error);
    }
}
