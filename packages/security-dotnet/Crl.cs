using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ClubOS.Security;

/// <summary>Проверенный список отзыва: подпись CA сошлась, серийные номера — hex верхним регистром.</summary>
public sealed record CertificateRevocationList(
    BigInteger Number,
    DateTimeOffset ThisUpdateUtc,
    DateTimeOffset? NextUpdateUtc,
    IReadOnlySet<string> RevokedSerials)
{
    public bool IsRevoked(X509Certificate2 certificate) =>
        RevokedSerials.Contains(DevCertificateAuthority.NormalizeSerial(certificate.SerialNumber));

    public bool IsRevoked(string certificatePem)
    {
        using var cert = X509Certificate2.CreateFromPem(certificatePem);
        return IsRevoked(cert);
    }
}

public sealed class InvalidCrlException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Разбор и проверка CRL (RFC 5280 §5) без системного хранилища: Edge получает список от Cloud и сверяет подпись
/// с сертификатом CA из своей регистрации. Поддерживается только ECDSA (ключ dev CA — P-256).
/// </summary>
public static class CrlReader
{
    private const string EcdsaSha256 = "1.2.840.10045.4.3.2";
    private const string EcdsaSha384 = "1.2.840.10045.4.3.3";
    private const string CrlNumberOid = "2.5.29.20";

    public static CertificateRevocationList Read(byte[] der, X509Certificate2 issuer)
    {
        try
        {
            return ReadCore(der, issuer);
        }
        catch (AsnContentException ex)
        {
            throw new InvalidCrlException("CRL повреждён.", ex);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidCrlException("CRL повреждён.", ex);
        }
    }

    private static CertificateRevocationList ReadCore(byte[] der, X509Certificate2 issuer)
    {
        var outer = new AsnReader(der, AsnEncodingRules.DER);
        var certificateList = outer.ReadSequence();
        outer.ThrowIfNotEmpty();

        var tbsBytes = certificateList.PeekEncodedValue().ToArray();
        var tbs = certificateList.ReadSequence();
        var signatureAlgorithm = ReadAlgorithm(certificateList);
        var signature = certificateList.ReadBitString(out var unused);
        certificateList.ThrowIfNotEmpty();
        if (unused != 0)
        {
            throw new InvalidCrlException("CRL повреждён: подпись.");
        }

        var hash = signatureAlgorithm switch
        {
            EcdsaSha256 => HashAlgorithmName.SHA256,
            EcdsaSha384 => HashAlgorithmName.SHA384,
            _ => throw new InvalidCrlException($"Алгоритм подписи CRL не поддерживается: {signatureAlgorithm}.")
        };

        using var key = issuer.GetECDsaPublicKey() ?? throw new InvalidCrlException("Ключ CA не ECDSA.");
        if (!key.VerifyData(tbsBytes, signature, hash, DSASignatureFormat.Rfc3279DerSequence))
        {
            throw new InvalidCrlException("Подпись CRL не сходится с CA.");
        }

        // TBSCertList: version (v2), signature, issuer, thisUpdate, nextUpdate?, revokedCertificates?, [0] crlExtensions?
        if (tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Integer))
        {
            tbs.ReadInteger();
        }

        if (ReadAlgorithm(tbs) != signatureAlgorithm)
        {
            throw new InvalidCrlException("CRL повреждён: алгоритм подписи не совпадает.");
        }

        var issuerName = new X500DistinguishedName(tbs.ReadEncodedValue().Span);
        if (!issuerName.RawData.AsSpan().SequenceEqual(issuer.SubjectName.RawData))
        {
            throw new InvalidCrlException("CRL выпущен другим CA.");
        }

        var thisUpdate = ReadTime(tbs);
        DateTimeOffset? nextUpdate = null;
        if (tbs.HasData && IsTime(tbs.PeekTag()))
        {
            nextUpdate = ReadTime(tbs);
        }

        var serials = new HashSet<string>(StringComparer.Ordinal);
        if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
        {
            var entries = tbs.ReadSequence();
            while (entries.HasData)
            {
                var entry = entries.ReadSequence();
                serials.Add(DevCertificateAuthority.NormalizeSerial(Convert.ToHexString(entry.ReadIntegerBytes().Span)));
            }
        }

        var number = BigInteger.Zero;
        var extensionsTag = new Asn1Tag(TagClass.ContextSpecific, 0, true);
        if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(extensionsTag))
        {
            var extensions = tbs.ReadSequence(extensionsTag).ReadSequence();
            while (extensions.HasData)
            {
                var extension = extensions.ReadSequence();
                var oid = extension.ReadObjectIdentifier();
                if (extension.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean))
                {
                    extension.ReadBoolean();
                }

                var value = extension.ReadOctetString();
                if (oid == CrlNumberOid)
                {
                    number = new AsnReader(value, AsnEncodingRules.DER).ReadInteger();
                }
            }
        }

        tbs.ThrowIfNotEmpty();
        return new CertificateRevocationList(number, thisUpdate, nextUpdate, serials);
    }

    private static string ReadAlgorithm(AsnReader reader)
    {
        var algorithm = reader.ReadSequence();
        var oid = algorithm.ReadObjectIdentifier();
        if (algorithm.HasData)
        {
            algorithm.ReadEncodedValue(); // параметры (у ECDSA отсутствуют)
        }

        algorithm.ThrowIfNotEmpty();
        return oid;
    }

    private static bool IsTime(Asn1Tag tag) =>
        tag.HasSameClassAndValue(Asn1Tag.UtcTime) || tag.HasSameClassAndValue(Asn1Tag.GeneralizedTime);

    private static DateTimeOffset ReadTime(AsnReader reader) =>
        reader.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime) ? reader.ReadUtcTime() : reader.ReadGeneralizedTime();
}
