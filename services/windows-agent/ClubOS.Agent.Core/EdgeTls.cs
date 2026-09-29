using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using ClubOS.Security;
using Microsoft.Extensions.Logging;

namespace ClubOS.Agent.Core;

/// <summary>
/// HTTPS к Edge (D-007). Агент не доверяет системному хранилищу сертификатов: только dev CA ClubOS.
/// До регистрации CA определяется по отпечатку из установщика (<see cref="AgentOptions.EdgeCaFingerprint"/>) —
/// Edge присылает CA в цепочке, агент сверяет его отпечаток. После регистрации CA закреплён в identity.
/// Имя сервера проверяется по SAN сертификата (адрес из EdgeUrl должен в нём быть).
/// </summary>
public static class EdgeTls
{
    public static SocketsHttpHandler CreateHandler(AgentOptions options, AgentIdentityStore identity, TimeProvider time,
        ILogger logger) =>
        CreateHandler(() => identity.TrustedCaPem, options.EdgeCaFingerprint, time, logger);

    public static SocketsHttpHandler CreateHandler(Func<string?> pinnedCaPem, string? fingerprint, TimeProvider time,
        ILogger logger)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
        {
            var ok = Validate(certificate, chain, errors, pinnedCaPem(), fingerprint, time.GetUtcNow(), out var reason);
            if (!ok)
            {
                logger.LogError("TLS-сертификат Edge отклонён: {Reason}", reason);
            }

            return ok;
        };
        return handler;
    }

    /// <summary>
    /// Первое подключение без закреплённого CA: забирает сертификат CA у Edge (<c>GET agent/v1/ca</c>), сверяет его
    /// SHA-256 с отпечатком из Admin Web и проверяет, что TLS-сертификат этого же соединения выпущен им.
    /// Подмена Edge в LAN не пройдёт: у злоумышленника нет CA с таким отпечатком.
    /// </summary>
    public static async Task<string> FetchTrustedCaAsync(Uri edgeBaseUrl, string fingerprint, CancellationToken ct)
    {
        X509Certificate2? presented = null;
        using var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            {
                return false;
            }

            presented = new X509Certificate2(certificate);
            return true; // окончательная проверка — после сверки CA ниже
        };
        using var http = new HttpClient(handler) { BaseAddress = edgeBaseUrl, Timeout = TimeSpan.FromSeconds(15) };
        var response = await http.GetFromJsonAsync<CaResponse>("agent/v1/ca", ct)
                       ?? throw new InvalidOperationException("Edge не вернул сертификат CA.");
        using var ca = X509Certificate2.CreateFromPem(response.CaCertificatePem);
        if (EdgeTlsTrust.Fingerprint(ca) != EdgeTlsTrust.NormalizeFingerprint(fingerprint))
        {
            throw new InvalidOperationException("Отпечаток CA Edge не совпадает с Agent:EdgeCaFingerprint — возможна подмена Edge.");
        }

        if (edgeBaseUrl.Scheme == Uri.UriSchemeHttps &&
            (presented is null || !EdgeTlsTrust.IsIssuedBy(presented, ca, DateTimeOffset.UtcNow)))
        {
            throw new InvalidOperationException("TLS-сертификат Edge выпущен не CA ClubOS.");
        }

        return response.CaCertificatePem;
    }

    private sealed record CaResponse(string CaCertificatePem);

    public static bool Validate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors, string? pinnedCaPem,
        string? fingerprint, DateTimeOffset now, out string? reason)
    {
        reason = null;
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            reason = "Edge не предъявил сертификат";
            return false;
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            reason = "адрес Edge в EdgeUrl отсутствует в сертификате (Edge:TlsHostNames)";
            return false;
        }

        using var leaf = new X509Certificate2(certificate);
        var expected = string.IsNullOrWhiteSpace(fingerprint) ? null : EdgeTlsTrust.NormalizeFingerprint(fingerprint);
        X509Certificate2? ca = null;
        if (pinnedCaPem is not null)
        {
            ca = X509Certificate2.CreateFromPem(pinnedCaPem);
            if (expected is not null && EdgeTlsTrust.Fingerprint(ca) != expected)
            {
                reason = "закреплённый CA не совпадает с Agent:EdgeCaFingerprint";
                return false;
            }
        }
        else if (expected is not null && chain is not null)
        {
            ca = chain.ChainElements.Select(e => e.Certificate)
                .Concat(chain.ChainPolicy.ExtraStore)
                .FirstOrDefault(c => EdgeTlsTrust.Fingerprint(c) == expected);
        }

        if (ca is null)
        {
            reason = pinnedCaPem is null && expected is null
                ? "нет доверенного CA: задайте Agent:EdgeCaFingerprint (Admin Web → Подключение)"
                : "CA с указанным отпечатком не найден в цепочке Edge";
            return false;
        }

        if (!EdgeTlsTrust.IsIssuedBy(leaf, ca, now))
        {
            reason = "сертификат Edge выпущен не доверенным CA ClubOS";
            return false;
        }

        return true;
    }
}
