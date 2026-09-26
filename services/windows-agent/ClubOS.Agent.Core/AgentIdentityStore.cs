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

        if (File.Exists(_identityPath) && File.Exists(_keyPath))
        {
            Current = JsonSerializer.Deserialize<AgentIdentity>(File.ReadAllText(_identityPath), ContractJson.Options);
            _key = LoadKey();
        }
    }

    public AgentIdentity? Current { get; private set; }

    public DeviceKey Key => _key ?? throw new InvalidOperationException("Устройство ещё не зарегистрировано.");

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
