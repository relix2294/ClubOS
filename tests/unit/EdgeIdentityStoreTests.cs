using ClubOS.EdgeController.Storage;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>Ключ Edge переживает перезапуск (Windows: DPAPI LocalMachine, Linux: файл 600).</summary>
public class EdgeIdentityStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Pending_key_and_identity_survive_restart()
    {
        var first = new EdgeIdentityStore(_dir.Path);
        Assert.False(first.IsEnrolled);
        var publicKey = first.GetOrCreatePendingKey().Key.ExportSubjectPublicKeyInfo();

        first.Save(new EdgeIdentity
        {
            EdgeId = "edge_1",
            TenantId = "org_1",
            LocationId = "loc_1",
            CertificatePem = "cert",
            CaCertificatePem = "ca",
            CertificateExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(90)
        });

        var restarted = new EdgeIdentityStore(_dir.Path);
        Assert.True(restarted.IsEnrolled);
        Assert.True(restarted.WhenEnrolled.IsCompleted);
        Assert.Equal("edge_1", restarted.Current!.EdgeId);
        Assert.Equal(publicKey, restarted.Key.Key.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public void Key_file_does_not_contain_plain_pem_on_windows()
    {
        var store = new EdgeIdentityStore(_dir.Path);
        store.GetOrCreatePendingKey();
        var raw = File.ReadAllText(Path.Combine(_dir.Path, "edge.key"));

        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("PRIVATE KEY", raw); // зашифрован DPAPI
        }
        else
        {
            Assert.Contains("PRIVATE KEY", raw);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(_dir.Path, "edge.key")));
        }
    }
}
