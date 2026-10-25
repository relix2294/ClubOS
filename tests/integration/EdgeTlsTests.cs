using System.Net;
using System.Security.Cryptography.X509Certificates;
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
        // Те же параметры рукопожатия, что у Edge (Program.cs): клиентский сертификат запрашивается.
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Any, 0, listen => listen.UseHttps(new TlsHandshakeCallbackOptions
        {
            OnConnection = _ => ValueTask.FromResult(store.CreateServerOptions())
        })));
        var app = builder.Build();
        app.MapGet("/ping", () => "pong");
        app.MapGet("/whoami", (HttpContext http) =>
            http.Connection.ClientCertificate?.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false) ?? "none");
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

    /// <summary>
    /// mTLS (D-002) на настоящем рукопожатии: агент предъявляет сертификат устройства, Kestrel передаёт его в
    /// приложение; после продления (новый ключ и сертификат) агент открывает новое соединение с новым сертификатом.
    /// </summary>
    [Fact]
    public async Task Agent_presents_device_certificate_and_switches_it_after_renewal()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        await using var edge = new EdgeHost(cloud, AuthAndTenancyTests.TempPath(), await owner.EdgeTokenAsync(location));
        await using var agent = new AgentHost(edge, AuthAndTenancyTests.TempPath(), await owner.DeviceTokenAsync(location),
            renewBeforeDays: 100);
        var deviceId = await Wait.ForAsync(async () => agent.Runtime.DeviceId, what: "enrollment");
        var store = edge.Service<EdgeTlsCertificateStore>();
        await Wait.UntilAsync(async () => store.Certificate is not null, what: "TLS-сертификат Edge");
        var caPem = edge.Service<EdgeIdentityStore>().Current!.CaCertificatePem;
        var (app, port) = await StartTlsServerAsync(store, caPem);
        await using var _ = app;

        X509Certificate2? presented = null;
        using var http = new HttpClient(new ClientCertificateHandler(() => agent.Identity.ClientCertificate(),
            certificate =>
            {
                presented = certificate;
                return EdgeTls.CreateHandler(() => caPem, null, TimeProvider.System, NullLogger.Instance, certificate);
            }));
        Assert.Equal(deviceId, await http.GetStringAsync($"https://127.0.0.1:{port}/whoami"));
        var first = presented!;
        Assert.True(first.HasPrivateKey);
        Assert.Equal(first.SerialNumber, X509Certificate2.CreateFromPem(agent.Identity.Current!.CertificatePem).SerialNumber);

        // Продление с ротацией ключа: следующий запрос идёт по новому соединению с новым сертификатом.
        Assert.True(await agent.Runtime.RenewCertificateIfDueAsync(CancellationToken.None));
        Assert.Equal(deviceId, await http.GetStringAsync($"https://127.0.0.1:{port}/whoami"));
        Assert.NotEqual(first.SerialNumber, presented!.SerialNumber);
        Assert.Equal(presented.SerialNumber, X509Certificate2.CreateFromPem(agent.Identity.Current!.CertificatePem).SerialNumber);

        // Агент без сертификата (до регистрации) подключается — сертификат проверяет AgentAuth, а не TLS.
        Assert.Equal("none", await Client(caPem, null).GetStringAsync($"https://127.0.0.1:{port}/whoami"));
    }
}
