using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClubOS.Security;

namespace ClubOS.EdgeController.Storage;

/// <summary>
/// TLS-сертификат API агентов (D-007). Ключ TLS отдельный от ключа идентичности Edge и хранится так же
/// (DPAPI LocalMachine на Windows, 600 в Linux). Сертификат подменяется на лету при продлении — Kestrel
/// берёт текущий через <see cref="ServerContext"/> на каждое TLS-рукопожатие, перезапуск не нужен.
/// Клиенту отправляется цепочка «сертификат + CA»: агент сверяет отпечаток CA при первом контакте.
/// </summary>
public sealed class EdgeTlsCertificateStore
{
    private readonly string _keyPath;
    private readonly string _certPath;
    private readonly Lock _gate = new();
    private SslStreamCertificateContext? _context;
    private X509Certificate2? _certificate;

    public EdgeTlsCertificateStore(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        _keyPath = Path.Combine(dataPath, "tls.key");
        _certPath = Path.Combine(dataPath, "tls.crt");
    }

    public X509Certificate2? Certificate
    {
        get
        {
            lock (_gate)
            {
                return _certificate;
            }
        }
    }

    /// <summary>Контекст для TLS-рукопожатия; null — сертификата ещё нет (Edge не зарегистрирован).</summary>
    public SslStreamCertificateContext? ServerContext
    {
        get
        {
            lock (_gate)
            {
                return _context;
            }
        }
    }

    /// <summary>
    /// Параметры TLS-рукопожатия API агентов. Клиентский сертификат запрашивается, но не требуется на уровне TLS:
    /// его проверяет AgentAuth (цепочка, CN = deviceId из подписанного токена, CRL) — так регистрация ПК, загрузка
    /// бездискового ПК и касса /cash работают без сертификата, а режим «только с сертификатом» включается флагом.
    /// </summary>
    public SslServerAuthenticationOptions CreateServerOptions() => new()
    {
        ServerCertificateContext = ServerContext ?? throw new InvalidOperationException("TLS-сертификат Edge ещё не выпущен."),
        ClientCertificateRequired = true,
        RemoteCertificateValidationCallback = (_, _, _, _) => true
    };

    public ECDsa GetOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            var key = ECDsa.Create();
            key.ImportFromPem(EdgeIdentityStore.ReadKeyPem(_keyPath));
            return key;
        }

        var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        EdgeIdentityStore.WriteKeyPem(_keyPath, created.ExportECPrivateKeyPem());
        return created;
    }

    public string CreateSigningRequestPem(ECDsa key) =>
        new CertificateRequest("CN=clubos-edge-tls", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();

    /// <summary>Загружает сохранённый сертификат (при старте) вместе с CA для цепочки.</summary>
    public bool TryLoad(string caCertificatePem)
    {
        if (!File.Exists(_certPath) || !File.Exists(_keyPath))
        {
            return false;
        }

        Install(File.ReadAllText(_certPath), caCertificatePem);
        return true;
    }

    public void Save(string certificatePem, string caCertificatePem)
    {
        var temp = _certPath + ".tmp";
        File.WriteAllText(temp, certificatePem);
        File.Move(temp, _certPath, overwrite: true);
        Install(certificatePem, caCertificatePem);
    }

    private void Install(string certificatePem, string caCertificatePem)
    {
        using var key = GetOrCreateKey();
        using var publicCert = X509Certificate2.CreateFromPem(certificatePem);
        var withKey = publicCert.CopyWithPrivateKey(key);
        // На Windows SChannel нужен ключ, пригодный для TLS: переупаковка через PKCS#12 (эфемерно, без записи на диск).
        var usable = OperatingSystem.IsWindows()
            ? X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null)
            : withKey;
        var ca = X509Certificate2.CreateFromPem(caCertificatePem);
        var context = SslStreamCertificateContext.Create(usable, [ca], offline: true);
        lock (_gate)
        {
            _certificate = usable;
            _context = context;
        }
    }

    /// <summary>Имена и адреса Edge для SAN: заданные в конфигурации + имя машины + IPv4 активных интерфейсов.</summary>
    public static (IReadOnlyList<string> Dns, IReadOnlyList<string> Ips) DiscoverNames(IEnumerable<string> configured)
    {
        var dns = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost" };
        var ips = new SortedSet<string>(StringComparer.Ordinal) { IPAddress.Loopback.ToString() };
        if (!string.IsNullOrWhiteSpace(Environment.MachineName))
        {
            dns.Add(Environment.MachineName.ToLowerInvariant());
        }

        foreach (var name in configured.Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            if (IPAddress.TryParse(name, out var ip))
            {
                ips.Add(ip.ToString());
            }
            else
            {
                dns.Add(name.ToLowerInvariant());
            }
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    ips.Add(address.Address.ToString());
                }
            }
        }

        return (dns.Take(16).ToList(), ips.Take(16).ToList());
    }

    /// <summary>Нужен новый сертификат: нет, скоро истекает или изменились имена/адреса.</summary>
    public static bool NeedsRenewal(X509Certificate2? current, IReadOnlyList<string> dns, IReadOnlyList<string> ips,
        DateTimeOffset now, TimeSpan renewBefore)
    {
        if (current is null || current.NotAfter.ToUniversalTime() - now.UtcDateTime <= renewBefore)
        {
            return true;
        }

        var san = current.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null)
        {
            return true;
        }

        var haveDns = san.EnumerateDnsNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var haveIps = san.EnumerateIPAddresses().Select(x => x.ToString()).ToHashSet(StringComparer.Ordinal);
        return !dns.All(haveDns.Contains) || !ips.All(haveIps.Contains);
    }
}
