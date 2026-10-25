using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ClubOS.Agent.Core;
using ClubOS.EdgeController.Storage;
using ClubOS.Security;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>D-007: подпись привязана к запросу; TLS Edge — сертификат dev CA, агент доверяет только ему.</summary>
public class ChannelSecurityTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new(DateTimeOffset.UtcNow);

    public void Dispose() => _dir.Dispose();

    private DevCertificateAuthority Ca(string name = "ca") => DevCertificateAuthority.LoadOrCreate(Path.Combine(_dir.Path, name), _time);

    private (string Token, string CertPem, DevCertificateAuthority Ca) Signed(RequestBinding? binding)
    {
        var ca = Ca();
        using var key = DeviceKey.Generate();
        var cert = ca.Issue(key.CreateSigningRequestPem("x"), "edge_1", DevCertificateAuthority.RoleEdge, TimeSpan.FromDays(1));
        return (SignedToken.Create("edge_1", key.Key, SignedToken.AudienceCloud, _time, binding), cert.CertificatePem, ca);
    }

    private static RequestBinding Post(string body, string path = "/api/v1/edge/status") =>
        RequestBinding.For("POST", path, Encoding.UTF8.GetBytes(body));

    [Fact]
    public void Bound_token_accepts_only_the_signed_request()
    {
        var (token, cert, ca) = Signed(Post("{\"a\":1}"));
        var validator = new SignedTokenValidator(ca.Certificate, _time);
        Assert.True(validator.Validate(token, cert, SignedToken.AudienceCloud, DevCertificateAuthority.RoleEdge,
            Post("{\"a\":1}"), requireBinding: true).Success);

        foreach (var other in new[] { Post("{\"a\":2}"), Post("{\"a\":1}", "/api/v1/edge/sync"), RequestBinding.For("PUT", "/api/v1/edge/status", Encoding.UTF8.GetBytes("{\"a\":1}")) })
        {
            var (t, c, a) = Signed(Post("{\"a\":1}"));
            var result = new SignedTokenValidator(a.Certificate, _time).Validate(t, c, SignedToken.AudienceCloud,
                DevCertificateAuthority.RoleEdge, other);
            Assert.False(result.Success);
            Assert.Equal("request does not match signature", result.Error);
        }
    }

    [Fact]
    public void Unbound_token_is_accepted_only_when_binding_is_not_required()
    {
        var (token, cert, ca) = Signed(null);
        Assert.True(new SignedTokenValidator(ca.Certificate, _time).Validate(token, cert, SignedToken.AudienceCloud,
            DevCertificateAuthority.RoleEdge, Post("{}")).Success);

        var (token2, cert2, ca2) = Signed(null);
        var strict = new SignedTokenValidator(ca2.Certificate, _time).Validate(token2, cert2, SignedToken.AudienceCloud,
            DevCertificateAuthority.RoleEdge, Post("{}"), requireBinding: true);
        Assert.Equal("request binding required", strict.Error);
    }

    private (X509Certificate2 Leaf, DevCertificateAuthority Ca) ServerCert(string? caName = null)
    {
        var ca = Ca(caName ?? "ca");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=tls", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        var issued = ca.IssueServer(csr, "edge_1", ["edge.club.local"], [IPAddress.Parse("192.168.1.10")], TimeSpan.FromDays(90));
        return (X509Certificate2.CreateFromPem(issued.CertificatePem), ca);
    }

    [Fact]
    public void Server_certificate_has_san_and_server_auth()
    {
        var (leaf, ca) = ServerCert();
        Assert.Equal("edge_1", leaf.GetNameInfo(X509NameType.SimpleName, false));
        var san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("edge.club.local", san.EnumerateDnsNames());
        Assert.Contains(IPAddress.Parse("192.168.1.10"), san.EnumerateIPAddresses());
        Assert.True(EdgeTlsTrust.IsIssuedBy(leaf, ca.Certificate, _time.GetUtcNow()));
        Assert.False(EdgeTlsTrust.IsIssuedBy(leaf, Ca("other").Certificate, _time.GetUtcNow()));

        // Клиентский сертификат устройства не годится как серверный (нет serverAuth).
        using var deviceKey = DeviceKey.Generate();
        var device = X509Certificate2.CreateFromPem(ca.Issue(deviceKey.CreateSigningRequestPem("d"), "dev_1",
            DevCertificateAuthority.RoleDevice, TimeSpan.FromDays(1)).CertificatePem);
        Assert.False(EdgeTlsTrust.IsIssuedBy(device, ca.Certificate, _time.GetUtcNow()));
    }

    [Fact]
    public void Agent_trusts_edge_by_pinned_ca_or_fingerprint_only()
    {
        var (leaf, ca) = ServerCert();
        var caPem = ca.Certificate.ExportCertificatePem();
        var now = _time.GetUtcNow();

        Assert.True(EdgeTls.Validate(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, caPem, null, now, out _));

        using var chain = new X509Chain();
        chain.ChainPolicy.ExtraStore.Add(ca.Certificate);
        Assert.True(EdgeTls.Validate(leaf, chain, SslPolicyErrors.RemoteCertificateChainErrors, null,
            ca.FingerprintSha256.ToLowerInvariant(), now, out _));

        Assert.False(EdgeTls.Validate(leaf, chain, SslPolicyErrors.None, null, null, now, out var noTrust));
        Assert.Contains("EdgeCaFingerprint", noTrust);
        Assert.False(EdgeTls.Validate(leaf, chain, SslPolicyErrors.None, null, new string('A', 64), now, out _));
        Assert.False(EdgeTls.Validate(leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch, caPem, null, now, out var name));
        Assert.Contains("TlsHostNames", name);

        // Сертификат от чужого CA с «правильным» отпечатком чужого CA не пройдёт закреплённый CA.
        var (foreignLeaf, _) = ServerCert("foreign");
        Assert.False(EdgeTls.Validate(foreignLeaf, null, SslPolicyErrors.RemoteCertificateChainErrors, caPem, null, now, out _));
    }

    [Fact]
    public void Tls_certificate_is_renewed_when_expiring_or_addresses_change()
    {
        var (leaf, _) = ServerCert();
        var now = _time.GetUtcNow();
        Assert.False(EdgeTlsCertificateStore.NeedsRenewal(leaf, ["edge.club.local"], ["192.168.1.10"], now, TimeSpan.FromDays(30)));
        Assert.True(EdgeTlsCertificateStore.NeedsRenewal(leaf, ["edge.club.local"], ["192.168.1.11"], now, TimeSpan.FromDays(30)));
        Assert.True(EdgeTlsCertificateStore.NeedsRenewal(leaf, ["edge.club.local"], ["192.168.1.10"], now.AddDays(61), TimeSpan.FromDays(30)));
        Assert.True(EdgeTlsCertificateStore.NeedsRenewal(null, [], [], now, TimeSpan.FromDays(30)));
    }
}
