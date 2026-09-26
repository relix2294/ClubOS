using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ClubOS.Security;

/// <summary>
/// Ключевая пара Edge/устройства (ECDsa P-256). Приватный ключ генерируется на месте
/// и никогда не передаётся по сети — наружу уходит только CSR (ТЗ §3.1).
/// </summary>
public sealed class DeviceKey : IDisposable
{
    private DeviceKey(ECDsa key) => Key = key;

    public ECDsa Key { get; }

    public static DeviceKey Generate() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static DeviceKey FromPrivateKeyPem(string pem)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return new DeviceKey(key);
    }

    public string ExportPrivateKeyPem() => Key.ExportPkcs8PrivateKeyPem();

    /// <summary>CSR; subject носит лишь справочный характер — CA назначает свой.</summary>
    public string CreateSigningRequestPem(string subjectHint)
    {
        var request = new CertificateRequest($"CN={subjectHint}", Key, HashAlgorithmName.SHA256);
        return request.CreateSigningRequestPem();
    }

    public void Dispose() => Key.Dispose();
}
