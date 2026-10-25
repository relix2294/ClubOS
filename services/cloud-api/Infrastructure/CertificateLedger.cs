using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClubOS.CloudApi.Infrastructure;

/// <summary>
/// Реестр выпущенных сертификатов и список отзыва (D-002). Каждый выпуск записывается; удаление устройства или
/// отключение Edge отзывает все его действующие сертификаты (клиентские, прежний ключ, серверный TLS).
/// CRL публикуется анонимно (как принято для CRL) и подписан CA — Edge проверяет подпись сам.
/// </summary>
public sealed class CertificateLedger(DevCertificateAuthority ca, IOptions<PkiOptions> pki, IServiceScopeFactory scopes,
    TimeProvider time)
{
    /// <summary>Сколько секунд один и тот же CRL отдаётся из памяти (анонимный адрес не нагружает БД и подпись).</summary>
    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (byte[] Der, DateTimeOffset BuiltAt, long Version)? _cached;
    private long _version;

    public static void Record(ClubOsDbContext db, string tenantId, string subjectId, string role, IssuedCertificate cert,
        DateTimeOffset now) =>
        db.Certificates.Add(new CertificateRecord
        {
            Serial = cert.SerialHex,
            TenantId = tenantId,
            SubjectId = subjectId,
            Role = role,
            IssuedAtUtc = now,
            ExpiresAtUtc = cert.ExpiresAtUtc
        });

    /// <summary>
    /// Отзывает все действующие сертификаты субъекта. <paramref name="knownPems"/> — сертификаты из записи
    /// устройства/Edge: выпущенные до появления реестра добавляются в него сразу отозванными.
    /// Сохранение — вызывающим (вместе с самим удалением), затем <see cref="Invalidate"/>.
    /// </summary>
    public async Task<int> RevokeSubjectAsync(ClubOsDbContext db, string tenantId, string subjectId,
        IEnumerable<string?> knownPems, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var records = await db.Certificates
            .Where(x => x.TenantId == tenantId && x.SubjectId == subjectId && x.RevokedAtUtc == null && x.ExpiresAtUtc > now)
            .ToListAsync(ct);
        var known = await db.Certificates.Where(x => x.SubjectId == subjectId).Select(x => x.Serial).ToListAsync(ct);
        foreach (var pem in knownPems)
        {
            if (string.IsNullOrWhiteSpace(pem))
            {
                continue; // бездисковый ПК: сертификата CA Cloud нет
            }

            X509Certificate2 cert;
            try
            {
                cert = X509Certificate2.CreateFromPem(pem);
            }
            catch (CryptographicException)
            {
                continue;
            }

            using (cert)
            {
                var serial = DevCertificateAuthority.NormalizeSerial(cert.SerialNumber);
                if (known.Contains(serial) || records.Any(r => r.Serial == serial))
                {
                    continue;
                }

                var record = new CertificateRecord
                {
                    Serial = serial,
                    TenantId = tenantId,
                    SubjectId = subjectId,
                    Role = cert.SubjectName.EnumerateRelativeDistinguishedNames()
                        .FirstOrDefault(x => x.GetSingleElementType().Value == "2.5.4.11")?.GetSingleElementValue() ?? "unknown",
                    IssuedAtUtc = new DateTimeOffset(cert.NotBefore.ToUniversalTime()),
                    ExpiresAtUtc = new DateTimeOffset(cert.NotAfter.ToUniversalTime())
                };
                db.Certificates.Add(record);
                records.Add(record);
            }
        }

        foreach (var record in records)
        {
            record.RevokedAtUtc = now;
            record.RevocationReason = (int)X509RevocationReason.CessationOfOperation;
        }

        return records.Count;
    }

    /// <summary>Сбросить кэш CRL — после сохранения отзыва.</summary>
    public void Invalidate() => Interlocked.Increment(ref _version);

    /// <summary>CRL в DER: отозванные и ещё не истёкшие сертификаты. Кэш сбрасывается при каждом отзыве.</summary>
    public async Task<byte[]> GetCrlAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var version = Interlocked.Read(ref _version);
        if (_cached is { } hit && hit.Version == version && now - hit.BuiltAt < CacheTime)
        {
            return hit.Der;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is { } again && again.Version == version && now - again.BuiltAt < CacheTime)
            {
                return again.Der;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ClubOsDbContext>();
            var revoked = await db.Certificates.AsNoTracking()
                .Where(x => x.RevokedAtUtc != null && x.ExpiresAtUtc > now)
                .OrderBy(x => x.RevokedAtUtc)
                .Select(x => new { x.Serial, x.RevokedAtUtc, x.RevocationReason })
                .ToListAsync(ct);
            var der = ca.CreateCrl(revoked.Select(x => new RevokedSerial(x.Serial, x.RevokedAtUtc!.Value,
                (X509RevocationReason)(x.RevocationReason ?? 0))), pki.Value.CrlValidity);
            _cached = (der, now, version);
            return der;
        }
        finally
        {
            _gate.Release();
        }
    }
}
