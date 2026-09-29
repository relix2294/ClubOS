using System.Security.Cryptography;
using System.Text.Json;
using ClubOS.Contracts;
using ClubOS.Security;

namespace ClubOS.EdgeController.Storage;

/// <summary>Идентичность Edge после enrollment. Сертификаты — публичные данные; ключ — отдельный файл 600.</summary>
public sealed record EdgeIdentity
{
    public required string EdgeId { get; init; }
    public required string TenantId { get; init; }
    public required string LocationId { get; init; }
    public required string CertificatePem { get; init; }
    public required string CaCertificatePem { get; init; }
    public required DateTimeOffset CertificateExpiresAtUtc { get; init; }
}

/// <summary>
/// Хранение идентичности Edge на диске. Приватный ключ Edge не покидает этот каталог
/// и не передаётся в Cloud (ТЗ §3.1).
/// </summary>
public sealed class EdgeIdentityStore
{
    private readonly string _identityPath;
    private readonly string _keyPath;
    private readonly TaskCompletionSource _enrolled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DeviceKey? _key;

    public EdgeIdentityStore(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        _identityPath = Path.Combine(dataPath, "identity.json");
        _keyPath = Path.Combine(dataPath, "edge.key");

        if (File.Exists(_identityPath) && File.Exists(_keyPath))
        {
            Current = JsonSerializer.Deserialize<EdgeIdentity>(File.ReadAllText(_identityPath), ContractJson.Options);
            _key = DeviceKey.FromPrivateKeyPem(ReadKeyPem(_keyPath));
            _enrolled.TrySetResult();
        }
    }

    public EdgeIdentity? Current { get; private set; }

    public DeviceKey Key => _key ?? throw new InvalidOperationException("Edge ещё не зарегистрирован.");

    public bool IsEnrolled => Current is not null;

    /// <summary>Завершается, когда Edge зарегистрирован (сразу, если identity уже на диске).</summary>
    public Task WhenEnrolled => _enrolled.Task;

    /// <summary>Готовит ключ для enrollment (сохраняет его сразу — повторный запуск использует тот же ключ).</summary>
    public DeviceKey GetOrCreatePendingKey()
    {
        if (_key is not null)
        {
            return _key;
        }

        if (File.Exists(_keyPath))
        {
            _key = DeviceKey.FromPrivateKeyPem(ReadKeyPem(_keyPath));
            return _key;
        }

        _key = DeviceKey.Generate();
        WriteKeyPem(_keyPath, _key.ExportPrivateKeyPem());
        return _key;
    }

    public void Save(EdgeIdentity identity)
    {
        var temp = _identityPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(identity, ContractJson.Options));
        File.Move(temp, _identityPath, overwrite: true);
        Current = identity;
        _enrolled.TrySetResult();
    }

    private static readonly byte[] KeyEntropy = "ClubOS.Edge.Key.v1"u8.ToArray();

    /// <summary>
    /// Windows: ключ Edge шифруется DPAPI (LocalMachine) — файл бесполезен на другом ПК;
    /// доступ к каталогу ограничен SYSTEM и Administrators. Linux/контейнер: файл с правами 600.
    /// </summary>
    internal static string ReadKeyPem(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), KeyEntropy, DataProtectionScope.LocalMachine);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        return File.ReadAllText(path);
    }

    internal static void WriteKeyPem(string path, string pem)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(pem), KeyEntropy,
                DataProtectionScope.LocalMachine));
            return;
        }

        WritePrivate(path, pem);
    }

    internal static void WritePrivate(string path, string content)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, content);
            return;
        }

        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }
}
