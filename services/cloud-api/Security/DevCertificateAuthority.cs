using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ClubOS.CloudApi.Security;

/// <summary>
/// Dev-CA для enrollment устройств (ТЗ §3.1). На M0 корневой сертификат генерируется
/// в памяти при старте и подписывает CSR устройств. Приватный ключ устройства НИКОГДА
/// не покидает устройство и не попадает в Cloud (ТЗ §27.3): сюда приходит только CSR (PEM).
/// В проде заменяется на управляемый CA — зафиксировано в docs/DEVIATIONS.md.
/// </summary>
public sealed class DevCertificateAuthority : IDisposable
{
    private readonly RSA _caKey;
    private readonly X509Certificate2 _caCertificate;

    public DevCertificateAuthority()
    {
        _caKey = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ClubOS Dev CA",
            _caKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

        var now = DateTimeOffset.UtcNow;
        _caCertificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));
    }

    /// <summary>Срок действия выпускаемых сертификатов устройств.</summary>
    public static TimeSpan DeviceCertificateLifetime => TimeSpan.FromDays(365);

    public sealed record SignedCertificate(string CertificatePem, DateTimeOffset ExpiresAtUtc);

    /// <summary>
    /// Подписывает CSR устройства (PKCS#10 PEM) сертификатом Dev-CA.
    /// Возвращает null, если CSR некорректен.
    /// </summary>
    public SignedCertificate? SignDeviceCsr(string csrPem, string deviceId)
    {
        CertificateRequest csr;
        try
        {
            csr = CertificateRequest.LoadSigningRequestPem(
                csrPem,
                HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.Default,
                RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var notAfter = now.Add(DeviceCertificateLifetime);
        var serial = BuildSerial(deviceId);
        var generator = X509SignatureGenerator.CreateForRSA(_caKey, RSASignaturePadding.Pkcs1);

        try
        {
            using var issued = csr.Create(
                _caCertificate.SubjectName,
                generator,
                now.AddMinutes(-5),
                notAfter,
                serial);

            return new SignedCertificate(issued.ExportCertificatePem(), notAfter);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[] BuildSerial(string deviceId)
    {
        // Ненулевой положительный серийный номер: время + хэш deviceId.
        Span<byte> serial = stackalloc byte[16];
        BitConverter.TryWriteBytes(serial, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var hash = System.Text.Encoding.UTF8.GetBytes(deviceId).Aggregate(17, (acc, b) => acc * 31 + b);
        BitConverter.TryWriteBytes(serial[8..], hash);
        serial[0] &= 0x7F; // гарантированно положительный
        if (serial[0] == 0)
        {
            serial[0] = 1;
        }

        return serial.ToArray();
    }

    public void Dispose()
    {
        _caCertificate.Dispose();
        _caKey.Dispose();
    }
}
