using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.Agent.Core;
using ClubOS.Agent.Core.PlayerShell;
using ClubOS.EdgeController;
using ClubOS.EdgeController.Api;
using ClubOS.EdgeController.Cloud;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubOS.Integration.Tests;

/// <summary>Переключатель «WAN есть / WAN нет» между Edge и Cloud.</summary>
public sealed class WanSwitch(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public volatile bool Offline;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Offline ? throw new HttpRequestException("WAN down (test)") : base.SendAsync(request, ct);
}

/// <summary>Edge Controller in-process; исходящий HTTP в Cloud идёт через TestServer Cloud.</summary>
public sealed class EdgeHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<EdgeOptions> _factory;

    public EdgeHost(CloudFixture cloud, string dataPath, string? enrollmentToken, int renewBeforeDays = 30)
    {
        DataPath = dataPath;
        Wan = new WanSwitch(cloud.Factory.Server.CreateHandler());
        _factory = new WebApplicationFactory<EdgeOptions>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("Edge:DataPath", dataPath);
            b.UseSetting("Edge:CloudUrl", "http://localhost");
            b.UseSetting("Edge:EnrollmentToken", enrollmentToken ?? string.Empty);
            b.UseSetting("Edge:LocalApiEnforceLoopback", "false");
            b.UseSetting("Edge:StatusReportSeconds", "1");
            b.UseSetting("Edge:ConfigRefreshSeconds", "1");
            b.UseSetting("Edge:CommandLongPollSeconds", "2");
            b.UseSetting("Edge:MaxBackoffSeconds", "1");
            b.UseSetting("Edge:CertificateRenewBeforeDays", renewBeforeDays.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.ConfigureServices(s => s.AddHttpClient(CloudClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Wan));
        });
        _ = _factory.Server;
    }

    public string DataPath { get; }

    public WanSwitch Wan { get; }

    public HttpClient CreateClient() => _factory.CreateClient();

    public T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    public HttpClient LocalAdmin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            File.ReadAllText(Path.Combine(DataPath, LocalAdminToken.FileName)).Trim());
        return client;
    }

    public async Task<JsonElement> StartLocalSessionAsync(string deviceId)
    {
        var response = await LocalAdmin().PostAsJsonAsync("/local/v1/sessions", new { deviceId, actor = "it" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session");
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

/// <summary>Агент на реальном ядре ClubOS.Agent.Core, подключённый к in-process Edge.</summary>
public sealed class AgentHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _run;

    public AgentHost(EdgeHost edge, string dataPath, string enrollmentToken, int renewBeforeDays = 30)
    {
        Presenter = new CountingPresenter();
        var identity = new AgentIdentityStore(dataPath, new FileKeyProtector());
        Identity = identity;
        var options = new AgentOptions
        {
            EdgeUrl = "http://localhost",
            EnrollmentToken = enrollmentToken,
            DataPath = dataPath,
            HeartbeatSeconds = 1,
            CommandPollSeconds = 2,
            MaxBackoffSeconds = 1,
            Shell = new ShellOptions { Mode = ShellMode.Enforced },
            CertificateRenewBeforeDays = renewBeforeDays,
            CertificateCheckMinutes = 24 * 60 // в тестах продление вызывается явно
        };
        Runtime = new AgentRuntime(options, identity, new EdgeClient(edge.CreateClient(), identity, TimeProvider.System),
            new BasicInventoryProvider(), Presenter,
            new CommandExecutor(Presenter, new ExecutedCommandStore(dataPath), TimeProvider.System, NullLogger<CommandExecutor>.Instance),
            new PlayerShellController(options, Presenter, TimeProvider.System, NullLogger<PlayerShellController>.Instance),
            TimeProvider.System, NullLogger<AgentRuntime>.Instance);
        _run = Task.Run(() => Runtime.RunAsync(_cts.Token));
    }

    public AgentRuntime Runtime { get; }

    public AgentIdentityStore Identity { get; }

    public CountingPresenter Presenter { get; }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await _run;
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public sealed class CountingPresenter : IUserPresenter
{
    private int _shown;

    public int Shown => _shown;

    public bool IsLocked { get; private set; }

    /// <summary>Последнее состояние Player Shell, полученное агентом.</summary>
    public ShellState? Shell { get; private set; }

    public Task<PresentResult> UpdateShellAsync(ShellState state, CancellationToken ct)
    {
        Shell = state;
        return Task.FromResult(PresentResult.Success);
    }

    public Task<PresentResult> ShowMessageAsync(string commandId, string title, string message, CancellationToken ct)
    {
        Interlocked.Increment(ref _shown);
        return Task.FromResult(PresentResult.Success);
    }

    public Task<PresentResult> SetLockAsync(string commandId, bool locked, string? reason, CancellationToken ct)
    {
        IsLocked = locked;
        return Task.FromResult(PresentResult.Success);
    }
}

public static class Wait
{
    public static async Task<T> ForAsync<T>(Func<Task<T?>> probe, TimeSpan? timeout = null, string? what = null)
        where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Не дождались: {what ?? "условие"}");
    }

    public static async Task UntilAsync(Func<Task<bool>> probe, TimeSpan? timeout = null, string? what = null) =>
        await ForAsync<object>(async () => await probe() ? new object() : null, timeout, what);
}
