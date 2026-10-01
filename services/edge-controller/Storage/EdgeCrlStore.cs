using System.Security.Cryptography.X509Certificates;
using ClubOS.Security;

namespace ClubOS.EdgeController.Storage;

/// <summary>
/// Список отзыва CA Cloud на Edge (D-002): файл <c>crl.der</c> в каталоге данных, проверенный подписью CA из
/// регистрации Edge. Переживает перезапуск и работу без интернета: отозванный ПК не проходит, даже если Edge
/// давно не получал конфигурацию. Более старый список (меньший номер) свежий не заменяет.
/// </summary>
public sealed class EdgeCrlStore(string dataPath)
{
    private readonly string _path = Path.Combine(dataPath, "crl.der");
    private readonly Lock _gate = new();
    private CertificateRevocationList? _current;

    public CertificateRevocationList? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Загрузить сохранённый список; повреждённый или чужой файл игнорируется (будет скачан заново).</summary>
    public bool TryLoad(string caCertificatePem)
    {
        if (!File.Exists(_path))
        {
            return false;
        }

        try
        {
            using var ca = X509Certificate2.CreateFromPem(caCertificatePem);
            var crl = CrlReader.Read(File.ReadAllBytes(_path), ca);
            lock (_gate)
            {
                _current = crl;
            }

            return true;
        }
        catch (InvalidCrlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Принять список от Cloud. true — список новее текущего и сохранён. Подпись проверяется до сохранения:
    /// подменённый в пути список (HTTP без TLS к Cloud в dev) отбрасывается.
    /// </summary>
    public bool Apply(byte[] der, string caCertificatePem)
    {
        using var ca = X509Certificate2.CreateFromPem(caCertificatePem);
        var crl = CrlReader.Read(der, ca);
        lock (_gate)
        {
            if (_current is { } current && crl.Number <= current.Number)
            {
                return false;
            }

            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, der);
            File.Move(temp, _path, overwrite: true);
            _current = crl;
            return true;
        }
    }

    /// <summary>true — сертификат в списке отзыва. Списка ещё нет — ничего не отозвано (только что зарегистрированный Edge).</summary>
    public bool IsRevoked(X509Certificate2 certificate) => Current?.IsRevoked(certificate) == true;
}
