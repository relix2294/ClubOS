using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ClubOS.Contracts;
using ClubOS.Security;

namespace ClubOS.Agent.Core;

public sealed record AgentIdentity
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public required string CertificatePem { get; init; }
    public required DateTimeOffset CertificateExpiresAtUtc { get; init; }

    /// <summary>Закреплённый при регистрации CA: TLS Edge принимается только от него (D-007).</summary>
    public string? CaCertificatePem { get; init; }
}

/// <summary>
/// Индивидуальная идентичность устройства после enrollment (ТЗ §3.1). Приватный ключ генерируется
/// локально, хранится защищённым (<see cref="IKeyProtector"/>) и никогда не отправляется по сети.
/// </summary>
public sealed class AgentIdentityStore
{
    private readonly string _identityPath;
    private readonly string _keyPath;
    private readonly IKeyProtector _protector;
    private DeviceKey? _key;

    public AgentIdentityStore(string dataPath, IKeyProtector protector)
    {
        Directory.CreateDirectory(dataPath);
        _identityPath = Path.Combine(dataPath, "identity.json");
        _keyPath = Path.Combine(dataPath, "device.key");
        _protector = protector;
        _nextKeyPath = _keyPath + ".next";
        // Незавершённая ротация (сбой до ответа Edge): при следующем продлении будет новый ключ.
        File.Delete(_nextKeyPath);

        if (File.Exists(_identityPath) && File.Exists(_keyPath))
        {
            Current = JsonSerializer.Deserialize<AgentIdentity>(File.ReadAllText(_identityPath), ContractJson.Options);
            _key = LoadKey();
        }
    }

    public AgentIdentity? Current { get; private set; }

    /// <summary>CA, проверенный по отпечатку до регистрации (в памяти; после регистрации — в identity).</summary>
    public string? BootstrapCaPem { get; set; }

    /// <summary>CA, которому доверяет TLS-клиент агента.</summary>
    public string? TrustedCaPem => Current?.CaCertificatePem ?? BootstrapCaPem;

    public DeviceKey Key => _key ?? throw new InvalidOperationException("Устройство ещё не зарегистрировано.");

    private readonly Lock _clientGate = new();
    private (string Pem, DeviceKey Key, X509Certificate2 Certificate)? _client;

    /// <summary>
    /// Сертификат устройства с закрытым ключом для mTLS к Edge (D-002); null — устройство не зарегистрировано
    /// (или идёт смена ключа). Новый объект — только при смене сертификата или ключа: по нему EdgeTls понимает,
    /// что соединения пора открыть заново. Прежний объект не освобождается — им может пользоваться открытое соединение.
    /// </summary>
    public X509Certificate2? ClientCertificate()
    {
        var identity = Current;
        var key = _key;
        if (identity is null || key is null || string.IsNullOrEmpty(identity.CertificatePem))
        {
            return null;
        }

        lock (_clientGate)
        {
            if (_client is { } cached && cached.Pem == identity.CertificatePem && ReferenceEquals(cached.Key, key))
            {
                return cached.Certificate;
            }

            try
            {
                using var certificate = X509Certificate2.CreateFromPem(identity.CertificatePem);
                using var withKey = certificate.CopyWithPrivateKey(key.Key);
                // SChannel (Windows) не принимает эфемерный ключ в клиентской аутентификации — загружаем через PKCS#12.
                var loaded = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
                _client = (identity.CertificatePem, key, loaded);
                return loaded;
            }
            catch (CryptographicException)
            {
                return null; // ключ и сертификат на миг разошлись при ротации — следующий запрос возьмёт новые
            }
        }
    }

    public DeviceKey GetOrCreatePendingKey()
    {
        if (_key is not null)
        {
            return _key;
        }

        if (File.Exists(_keyPath))
        {
            return _key = LoadKey();
        }

        _key = DeviceKey.Generate();
        WritePrivate(_keyPath, _protector.Protect(Encoding.UTF8.GetBytes(_key.ExportPrivateKeyPem())));
        return _key;
    }

    /// <summary>Забыть identity и ключ (бездисковый ПК при загрузке: прежние файлы могли попасть в общий образ).</summary>
    public void Reset()
    {
        File.Delete(_identityPath);
        File.Delete(_keyPath);
        Current = null;
        _key = null;
    }

    /// <summary>Новый ключ вместо текущего; identity остаётся до получения нового сертификата.</summary>
    public DeviceKey RotateKey()
    {
        var key = DeviceKey.Generate();
        WritePrivate(_keyPath, _protector.Protect(Encoding.UTF8.GetBytes(key.ExportPrivateKeyPem())));
        _key = key;
        return key;
    }

    private readonly string _nextKeyPath;

    /// <summary>Новый ключ для продления с ротацией (D-011): хранится отдельно, текущий ключ пока действует.</summary>
    public DeviceKey CreateNextKey()
    {
        var key = DeviceKey.Generate();
        WritePrivate(_nextKeyPath, _protector.Protect(Encoding.UTF8.GetBytes(key.ExportPrivateKeyPem())));
        return key;
    }

    /// <summary>
    /// Перейти на новый ключ и сертификат: сначала ключ (атомарная замена файла), затем identity. При сбое между ними
    /// Edge уже принимает новый ключ, а сертификат обновится при следующем продлении.
    /// </summary>
    public void CommitNextKey(DeviceKey key, AgentIdentity identity)
    {
        File.Move(_nextKeyPath, _keyPath, overwrite: true);
        _key = key; // прежний объект не освобождаем: им может подписываться текущий запрос
        Save(identity);
    }

    public void Save(AgentIdentity identity)
    {
        var temp = _identityPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(identity, ContractJson.Options));
        File.Move(temp, _identityPath, overwrite: true);
        Current = identity;
    }

    private DeviceKey LoadKey() =>
        DeviceKey.FromPrivateKeyPem(Encoding.UTF8.GetString(_protector.Unprotect(File.ReadAllBytes(_keyPath))));

    private static void WritePrivate(string path, byte[] content)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, content);
            return;
        }

        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        stream.Write(content);
    }
}
