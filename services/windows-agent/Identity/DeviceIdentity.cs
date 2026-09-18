using System.Text.Json;

namespace ClubOS.WindowsAgent.Identity;

/// <summary>
/// Персистентная идентичность устройства (ТЗ §3.1). deviceId и сертификат хранятся в JSON,
/// приватный ключ — отдельным PEM-файлом с ограниченным доступом. Ключ НИКОГДА не уходит в Cloud.
/// </summary>
public sealed record DeviceIdentity
{
    public required string DeviceId { get; init; }
    public required string CertificatePem { get; init; }
    public DateTimeOffset EnrolledAtUtc { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static string IdentityFile(string dir) => Path.Combine(dir, "identity.json");

    public static string PrivateKeyFile(string dir) => Path.Combine(dir, "device-key.pem");

    public static DeviceIdentity? TryLoad(string dir)
    {
        var file = IdentityFile(dir);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(file);
            return JsonSerializer.Deserialize<DeviceIdentity>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(IdentityFile(dir), JsonSerializer.Serialize(this, JsonOptions));
    }
}
