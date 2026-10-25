using System.Security.Cryptography;
using System.Text;
using ClubOS.EdgeController.Storage;
using Microsoft.Extensions.Options;

namespace ClubOS.EdgeController.Api;

/// <summary>
/// Токен локального admin API (edge-cli). Генерируется при первом запуске в каталоге данных
/// (права 600); edge-cli читает его оттуда. Локальный API слушает только loopback.
/// </summary>
public sealed class LocalAdminToken
{
    public const string FileName = "local-admin.token";

    private readonly byte[] _token;

    public LocalAdminToken(IOptions<EdgeOptions> options)
    {
        Directory.CreateDirectory(options.Value.DataPath);
        var path = Path.Combine(options.Value.DataPath, FileName);
        if (!File.Exists(path))
        {
            EdgeIdentityStore.WritePrivate(path,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        }

        _token = Encoding.UTF8.GetBytes(File.ReadAllText(path).Trim());
    }

    public bool Matches(string? presented) =>
        presented is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented.Trim()), _token);
}
