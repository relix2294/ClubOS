using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClubOS.Security;

/// <summary>
/// Короткоживущий подписанный токен запроса (компактный JWS, ES256).
/// Edge и Agent подписывают каждый запрос своим приватным ключом; сервер проверяет подпись
/// по сертификату, выданному dev CA. Заголовок: <c>Authorization: ClubOS-Sig &lt;token&gt;</c>.
/// Защищает от подделки идентичности и повторов (jti). С привязкой к запросу (<see cref="RequestBinding"/>:
/// метод, путь, SHA-256 тела) подпись защищает и содержимое запроса от подмены на канале (D-007).
/// </summary>
public static class SignedToken
{
    public const string Scheme = "ClubOS-Sig";
    public const string AudienceCloud = "clubos-cloud";
    public const string AudienceEdge = "clubos-edge";

    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    public static string Create(string subjectId, ECDsa key, string audience, TimeProvider time,
        RequestBinding? binding = null)
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var header = new TokenHeader("ES256", "JWT", subjectId);
        var payload = new TokenPayload(subjectId, audience, now, now + (long)Lifetime.TotalSeconds,
            Guid.NewGuid().ToString("N"), binding?.Method, binding?.PathAndQuery, binding?.BodyHash);

        var signingInput = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header, TokenJson.Default.TokenHeader)) + "." +
                           Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload, TokenJson.Default.TokenPayload));
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + Base64Url(signature);
    }

    /// <summary>Читает kid без проверки подписи — только чтобы найти сертификат субъекта.</summary>
    public static bool TryReadKeyId(string token, out string keyId)
    {
        keyId = string.Empty;
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        try
        {
            var header = JsonSerializer.Deserialize(FromBase64Url(parts[0]), TokenJson.Default.TokenHeader);
            if (header is null || string.IsNullOrWhiteSpace(header.Kid) || header.Kid.Length > 128)
            {
                return false;
            }

            keyId = header.Kid;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return false;
        }
    }

    internal static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch
        {
            2 => s + "==",
            3 => s + "=",
            _ => s
        };
        return Convert.FromBase64String(s);
    }
}

/// <summary>Проверка <see cref="SignedToken"/> по сертификату субъекта и доверенному dev CA.</summary>
public sealed class SignedTokenValidator
{
    private readonly X509Certificate2 _ca;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new();
    private DateTimeOffset _lastPurge;

    public SignedTokenValidator(X509Certificate2 caCertificate, TimeProvider time)
    {
        _ca = caCertificate;
        _time = time;
        _lastPurge = time.GetUtcNow();
    }

    /// <param name="binding">Фактический запрос. Если токен привязан к запросу, привязка обязана совпасть.</param>
    /// <param name="requireBinding">Отклонять токены без привязки (после обновления всех клиентов).</param>
    public TokenValidationResult Validate(string token, string certificatePem, string expectedAudience, string expectedRole,
        RequestBinding? binding = null, bool requireBinding = false)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return TokenValidationResult.Fail("malformed token");
        }

        TokenHeader? header;
        TokenPayload? payload;
        byte[] signature;
        try
        {
            header = JsonSerializer.Deserialize(SignedToken.FromBase64Url(parts[0]), TokenJson.Default.TokenHeader);
            payload = JsonSerializer.Deserialize(SignedToken.FromBase64Url(parts[1]), TokenJson.Default.TokenPayload);
            signature = SignedToken.FromBase64Url(parts[2]);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return TokenValidationResult.Fail("malformed token");
        }

        if (header is null || payload is null || header.Alg != "ES256")
        {
            return TokenValidationResult.Fail("unsupported token");
        }

        using var cert = X509Certificate2.CreateFromPem(certificatePem);
        var now = _time.GetUtcNow();

        var chainError = ValidateCertificate(cert, header.Kid, expectedRole, now);
        if (chainError is not null)
        {
            return TokenValidationResult.Fail(chainError);
        }

        using var publicKey = cert.GetECDsaPublicKey();
        if (publicKey is null ||
            !publicKey.VerifyData(System.Text.Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature,
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            return TokenValidationResult.Fail("bad signature");
        }

        if (payload.Sub != header.Kid || payload.Aud != expectedAudience)
        {
            return TokenValidationResult.Fail("wrong subject or audience");
        }

        var bound = payload.Htm is not null || payload.Htu is not null || payload.Bh is not null;
        if (!bound && requireBinding)
        {
            return TokenValidationResult.Fail("request binding required");
        }

        if (bound && (binding is null || payload.Htm != binding.Method || payload.Htu != binding.PathAndQuery ||
                      !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(payload.Bh ?? string.Empty),
                          Encoding.ASCII.GetBytes(binding.BodyHash))))
        {
            return TokenValidationResult.Fail("request does not match signature");
        }

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(payload.Iat);
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.Exp);
        if (expiresAt - issuedAt > SignedToken.MaxLifetime || expiresAt <= issuedAt)
        {
            return TokenValidationResult.Fail("invalid lifetime");
        }

        if (now > expiresAt + SignedToken.ClockSkew || now < issuedAt - SignedToken.ClockSkew)
        {
            return TokenValidationResult.Fail("token expired or not yet valid");
        }

        if (string.IsNullOrEmpty(payload.Jti) || !_seen.TryAdd(payload.Sub + ":" + payload.Jti, expiresAt + SignedToken.ClockSkew))
        {
            return TokenValidationResult.Fail("replayed token");
        }

        PurgeIfNeeded(now);
        return TokenValidationResult.Ok(payload.Sub);
    }

    private string? ValidateCertificate(X509Certificate2 cert, string expectedSubjectId, string expectedRole, DateTimeOffset now)
    {
        if (now < cert.NotBefore.ToUniversalTime() || now > cert.NotAfter.ToUniversalTime())
        {
            return "certificate expired";
        }

        var cn = cert.GetNameInfo(X509NameType.SimpleName, false);
        var ou = GetOrganizationalUnit(cert.SubjectName);
        if (cn != expectedSubjectId || ou != expectedRole)
        {
            return "certificate subject mismatch";
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        return chain.Build(cert) ? null : "certificate not issued by trusted CA";
    }

    private static string? GetOrganizationalUnit(X500DistinguishedName name)
    {
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.GetSingleElementType().Value == "2.5.4.11")
            {
                return rdn.GetSingleElementValue();
            }
        }

        return null;
    }

    private void PurgeIfNeeded(DateTimeOffset now)
    {
        if (now - _lastPurge < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastPurge = now;
        foreach (var (key, until) in _seen)
        {
            if (until < now)
            {
                _seen.TryRemove(key, out _);
            }
        }
    }
}

public sealed record TokenValidationResult(bool Success, string? SubjectId, string? Error)
{
    public static TokenValidationResult Ok(string subject) => new(true, subject, null);
    public static TokenValidationResult Fail(string error) => new(false, null, error);
}

internal sealed record TokenHeader(
    [property: JsonPropertyName("alg")] string Alg,
    [property: JsonPropertyName("typ")] string Typ,
    [property: JsonPropertyName("kid")] string Kid);

internal sealed record TokenPayload(
    [property: JsonPropertyName("sub")] string Sub,
    [property: JsonPropertyName("aud")] string Aud,
    [property: JsonPropertyName("iat")] long Iat,
    [property: JsonPropertyName("exp")] long Exp,
    [property: JsonPropertyName("jti")] string Jti,
    [property: JsonPropertyName("htm"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Htm = null,
    [property: JsonPropertyName("htu"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Htu = null,
    [property: JsonPropertyName("bh"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Bh = null);

/// <summary>
/// Привязка подписи к конкретному запросу: метод, путь с query и SHA-256 тела (base64url).
/// Изменение любого из них на канале делает подпись недействительной.
/// </summary>
public sealed record RequestBinding(string Method, string PathAndQuery, string BodyHash)
{
    public const int MaxBodyBytes = 1024 * 1024;

    public static RequestBinding For(string method, string pathAndQuery, ReadOnlySpan<byte> body) =>
        new(method.ToUpperInvariant(), pathAndQuery, SignedToken.Base64Url(SHA256.HashData(body)));
}

[JsonSerializable(typeof(TokenHeader))]
[JsonSerializable(typeof(TokenPayload))]
internal sealed partial class TokenJson : JsonSerializerContext;
