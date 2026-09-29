using System.Net;
using System.Net.Security;
using ClubOS.Agent.Core;
using ClubOS.EdgeController.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>
/// D-007: Edge получает TLS-сертификат у CA Cloud, агент устанавливает HTTPS только с Edge этого CA
/// (реальное TLS-рукопожатие через Kestrel).
/// </summary>
[Collection(CloudCollection.Name)]
public class EdgeTlsTests(CloudFixture cloud)
{
    private static async Task<(WebApplication App, int Port)> StartTlsServerAsync(EdgeTlsCertificateStore store, string caPem)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Any, 0, listen => listen.UseHttps(new TlsHandshakeCallbackOptions
        {
            OnConnection = _ => ValueTask.FromResult(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = store.ServerContext ?? throw new InvalidOperationException("нет сертификата")
            })
        })));
        var app = builder.Build();
        app.MapGet("/ping", () => "pong");
        app.MapGet("/agent/v1/ca", () => Results.Ok(new { caCertificatePem = caPem })); // как у Edge
        await app.StartAsync();
        var port = new Uri(app.Urls.First()).Port;
        return (app, port);
    }

    private static HttpClient Client(string? pinnedCa, string? fingerprint) =>
        new(EdgeTls.CreateHandler(() => pinnedCa, fingerprint, TimeProvider.System, NullLogger.Instance))
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

    [Fact]
    public async Task Edge_gets_tls_certificate_and_agent_trusts_only_the_clubos_ca()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        await using var edge = new EdgeHost(cloud, AuthAndTenancyTests.TempPath(), await owner.EdgeTokenAsync(location));

        // Воркер Edge сам запрашивает сертификат после регистрации.
        var store = edge.Service<EdgeTlsCertificateStore>();
        await Wait.UntilAsync(async () => store.Certificate is not null, what: "TLS-сертификат Edge");
        var san = store.Certificate!.Extensions.OfType<System.Security.Cryptography.X509Certificates.X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("localhost", san.EnumerateDnsNames());
        Assert.Contains(IPAddress.Loopback, san.EnumerateIPAddresses());

        var ca = await owner.GetJsonAsync("/api/v1/pki/ca");
        var fingerprint = ca.Str("fingerprintSha256");
        Assert.Equal(64, fingerprint.Length);

        var caPem = edge.Service<EdgeIdentityStore>().Current!.CaCertificatePem;
        var (app, port) = await StartTlsServerAsync(store, caPem);
        await using var _ = app;

        // Первый контакт: CA берётся у Edge и сверяется с отпечатком из Admin Web.
        var bootstrapped = await EdgeTls.FetchTrustedCaAsync(new Uri($"https://127.0.0.1:{port}/"), fingerprint, CancellationToken.None);
        Assert.Equal("pong", await Client(bootstrapped, null).GetStringAsync($"https://localhost:{port}/ping"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EdgeTls.FetchTrustedCaAsync(new Uri($"https://127.0.0.1:{port}/"), new string('1', 64), CancellationToken.None));

        // После регистрации: закреплённый CA.
        Assert.Equal("pong", await Client(caPem, null).GetStringAsync($"https://127.0.0.1:{port}/ping"));

        // Нет доверия / чужой отпечаток / адрес не из SAN — соединение не устанавливается.
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(null, null).GetStringAsync($"https://127.0.0.1:{port}/ping"));
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(null, new string('0', 64)).GetStringAsync($"https://127.0.0.1:{port}/ping"));
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(caPem, null).GetStringAsync($"https://127.0.0.2:{port}/ping"));

        // Системное хранилище (обычный HttpClient) сертификату Edge не доверяет — только dev CA ClubOS.
        using var plain = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => plain.GetStringAsync($"https://127.0.0.1:{port}/ping"));
    }
}
