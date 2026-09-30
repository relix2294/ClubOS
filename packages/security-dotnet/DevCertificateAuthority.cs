using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ClubOS.Security;

/// <summary>
/// Development CA (ТЗ §3.1: допускается на M0 при описанной ротации, см. docs/security/dev-ca.md).
/// Подписывает CSR Edge и устройств. Приватный ключ CA хранится только на Cloud, в файле
/// с правами 600, и никогда не пишется в логи. Промышленный PKI — M1/M2 (DEVIATIONS D-002).
/// </summary>
public sealed class DevCertificateAuthority : IDisposable
{
    public const string RoleEdge = "edge";
    public const string RoleDevice = "device";

    /// <summary>Серверный TLS-сертификат API агентов на Edge.</summary>
    public const string RoleEdgeServer = "edge-server";

    private const string KeyFile = "ca.key";
    private const string CertFile = "ca.crt";

    private readonly ECDsa _key;
    private readonly TimeProvider _time;

    private DevCertificateAuthority(X509Certificate2 certificate, ECDsa key, TimeProvider time)
    {
        Certificate = certificate;
        _key = key;
        _time = time;
    }

    public X509Certificate2 Certificate { get; }

    public string CertificatePem => Certificate.ExportCertificatePem();

    /// <summary>Загружает CA из каталога или создаёт новый (первый запуск).</summary>
    public static DevCertificateAuthority LoadOrCreate(string directory, TimeProvider time,
        string subject = "CN=ClubOS Development CA, O=ClubOS (DEV ONLY)")
    {
        Directory.CreateDirectory(directory);
        var keyPath = Path.Combine(directory, KeyFile);
        var certPath = Path.Combine(directory, CertFile);

        if (File.Exists(keyPath) && File.Exists(certPath))
        {
            var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(keyPath));
            var cert = X509Certificate2.CreateFromPem(File.ReadAllText(certPath));
            return new DevCertificateAuthority(cert, key, time);
        }

        var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, newKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var now = time.GetUtcNow();
        using var caCert = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(5));

        WritePrivate(keyPath, newKey.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(certPath, caCert.ExportCertificatePem());

        var publicCert = X509Certificate2.CreateFromPem(caCert.ExportCertificatePem());
        return new DevCertificateAuthority(publicCert, newKey, time);
    }

    /// <summary>
    /// Выпускает сертификат по CSR. Subject задаёт CA (CN=id, OU=role) — значения из CSR игнорируются,
    /// чтобы устройство не могло само выбрать себе идентичность. Подпись CSR проверяется.
    /// </summary>
    public IssuedCertificate Issue(string csrPem, string subjectId, string role, TimeSpan validity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csrPem);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);

        CertificateRequest csr;
        try
        {
            csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidCsrException("CSR повреждён или подпись CSR неверна.", ex);
        }

        if (csr.PublicKey.Oid.Value != "1.2.840.10045.2.1")
        {
            throw new InvalidCsrException("Поддерживаются только ключи ECDsa (P-256).");
        }

        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={subjectId}, OU={role}, O=ClubOS"), csr.PublicKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.2")], false)); // clientAuth

        var now = _time.GetUtcNow();
        var notAfter = now.Add(validity);
        if (notAfter > Certificate.NotAfter)
        {
            notAfter = Certificate.NotAfter;
        }

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        var generator = X509SignatureGenerator.CreateForECDsa(_key);
        using var cert = request.Create(Certificate.SubjectName, generator, now.AddMinutes(-5), notAfter, serial);
        return new IssuedCertificate(cert.ExportCertificatePem(), new DateTimeOffset(cert.NotAfter.ToUniversalTime()));
    }

    /// <summary>
    /// Серверный TLS-сертификат (serverAuth) для API агентов на Edge (D-007). Имена и адреса в SAN задаёт
    /// Edge; Cloud выпускает его только аутентифицированному Edge и только с его id в CN.
    /// </summary>
    public IssuedCertificate IssueServer(string csrPem, string subjectId, IReadOnlyList<string> dnsNames,
        IReadOnlyList<System.Net.IPAddress> ipAddresses, TimeSpan validity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csrPem);
        if (dnsNames.Count + ipAddresses.Count is 0 or > 32)
        {
            throw new InvalidCsrException("Нужно от 1 до 32 имён/адресов для SAN.");
        }

        CertificateRequest csr;
        try
        {
            csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidCsrException("CSR повреждён или подпись CSR неверна.", ex);
        }

        if (csr.PublicKey.Oid.Value != "1.2.840.10045.2.1")
        {
            throw new InvalidCsrException("Поддерживаются только ключи ECDsa (P-256).");
        }

        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={subjectId}, OU={RoleEdgeServer}, O=ClubOS"), csr.PublicKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth
        var san = new SubjectAlternativeNameBuilder();
        foreach (var dns in dnsNames)
        {
            san.AddDnsName(dns);
        }

        foreach (var ip in ipAddresses)
        {
            san.AddIpAddress(ip);
        }

        request.CertificateExtensions.Add(san.Build());

        var now = _time.GetUtcNow();
        var notAfter = now.Add(validity) > Certificate.NotAfter ? Certificate.NotAfter : now.Add(validity);
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        var generator = X509SignatureGenerator.CreateForECDsa(_key);
        using var cert = request.Create(Certificate.SubjectName, generator, now.AddMinutes(-5), notAfter, serial);
        return new IssuedCertificate(cert.ExportCertificatePem(), new DateTimeOffset(cert.NotAfter.ToUniversalTime()));
    }

    /// <summary>SHA-256 отпечаток сертификата CA (hex) — агент сверяет с ним цепочку TLS Edge при первом контакте.</summary>
    public string FingerprintSha256 => Convert.ToHexString(SHA256.HashData(Certificate.RawData));

    public void Dispose()
    {
        _key.Dispose();
        Certificate.Dispose();
    }

    private static void WritePrivate(string path, string content)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, content);
            return;
        }

        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }
}

public sealed record IssuedCertificate(string CertificatePem, DateTimeOffset ExpiresAtUtc);

public sealed class InvalidCsrException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Проверка TLS-сертификата Edge агентом: цепочка до доверенного dev CA и имя из SAN (D-007).</summary>
public static class EdgeTlsTrust
{
    /// <summary>SHA-256 отпечаток сертификата (hex, без двоеточий, верхний регистр).</summary>
    public static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    public static string NormalizeFingerprint(string value) =>
        new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    /// <summary>
    /// true — сертификат выпущен CA <paramref name="ca"/> и предназначен для сервера (serverAuth). Проверку имени
    /// делает TLS-стек (SslPolicyErrors.RemoteCertificateNameMismatch); здесь — только цепочка.
    /// </summary>
    public static bool IsIssuedBy(X509Certificate2 certificate, X509Certificate2 ca, DateTimeOffset now)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return chain.Build(certificate) &&
               chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(ca.RawData);
    }
}
