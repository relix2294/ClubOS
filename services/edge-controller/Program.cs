using Microsoft.AspNetCore.Server.Kestrel.Https;
using System.Net.Security;
using System.Text.Json.Serialization;
using ClubOS.EdgeController;
using ClubOS.EdgeController.Api;
using ClubOS.EdgeController.Cloud;
using ClubOS.EdgeController.Storage;
using ClubOS.EdgeController.Workers;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;

// Edge Controller: Windows Service на сервере клуба (ClubOSEdge) или консоль/контейнер для разработки.
const string ServiceName = WindowsHosting.ServiceName;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Служба Windows стартует с текущим каталогом System32 — content root берём из каталога exe.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null
});

// Windows: данные и edge.json в %ProgramData%\ClubOS\Edge (ACL ставит install-edge.ps1).
var defaultDataPath = OperatingSystem.IsWindows()
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClubOS", "Edge")
    : "edge-data";
builder.Configuration.AddJsonFile(Path.Combine(defaultDataPath, "edge.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("CLUBOS_");
builder.Configuration.AddCommandLine(args);

builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
{
    // Источник Event Log = имя службы (регистрирует install-edge.ps1). В консольном запуске источник не задаём:
    // без регистрации (нужны права администратора) первая же запись в журнал уронила бы Edge.
    builder.Services.Configure<EventLogSettings>(WindowsHosting.UseServiceEventSource);
}
builder.Services.Configure<EdgeOptions>(o =>
{
    o.DataPath = defaultDataPath;
    builder.Configuration.GetSection(EdgeOptions.Section).Bind(o);
});

var edgeOptions = new EdgeOptions { DataPath = defaultDataPath };
builder.Configuration.GetSection(EdgeOptions.Section).Bind(edgeOptions);

// Два listener'а: API агентов (LAN) и локальный admin API (только loopback) — ТЗ §25.2.4.
if (builder.Configuration["ASPNETCORE_URLS"] is null && builder.Configuration["urls"] is null)
{
    builder.WebHost.ConfigureKestrel(k =>
    {
        if (edgeOptions.AgentHttpEnabled)
        {
            k.ListenAnyIP(edgeOptions.AgentApiPort);
        }

        if (edgeOptions.AgentTlsPort > 0)
        {
            // Сертификат берётся на каждое рукопожатие: после выпуска/продления перезапуск не нужен.
            k.ListenAnyIP(edgeOptions.AgentTlsPort, listen => listen.UseHttps(new TlsHandshakeCallbackOptions
            {
                OnConnection = _ =>
                {
                    var context = k.ApplicationServices.GetRequiredService<EdgeTlsCertificateStore>().ServerContext
                                  ?? throw new InvalidOperationException("TLS-сертификат Edge ещё не выпущен.");
                    return ValueTask.FromResult(new SslServerAuthenticationOptions { ServerCertificateContext = context });
                }
            }));
        }

        k.ListenLocalhost(edgeOptions.LocalApiPort);
    });
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new EdgeDatabase(sp.GetRequiredService<IOptions<EdgeOptions>>().Value.DataPath));
builder.Services.AddSingleton(sp => new EdgeIdentityStore(sp.GetRequiredService<IOptions<EdgeOptions>>().Value.DataPath));
builder.Services.AddSingleton<EdgeSignals>();
builder.Services.AddSingleton(sp => new EdgeTlsCertificateStore(sp.GetRequiredService<IOptions<EdgeOptions>>().Value.DataPath));
builder.Services.AddSingleton<EdgeStore>();
builder.Services.AddSingleton<AgentAuth>();
builder.Services.AddSingleton<DisklessAuthority>();
builder.Services.AddSingleton<LocalAdminToken>();

builder.Services.AddHttpClient(CloudClient.HttpClientName, (sp, http) =>
{
    var url = sp.GetRequiredService<IOptions<EdgeOptions>>().Value.CloudUrl.TrimEnd('/') + "/";
    http.BaseAddress = new Uri(url);
    http.Timeout = TimeSpan.FromSeconds(40); // > long-poll 20-25 сек
});
// CloudClient — singleton: хранит состояние связи с Cloud (для health/edge-cli).
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<CloudClient>(sp,
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(CloudClient.HttpClientName)));

builder.Services.AddHostedService<EnrollmentWorker>();
builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddHostedService<CommandPuller>();
builder.Services.AddHostedService<StatusWorker>();
builder.Services.AddHostedService<SessionTimerWorker>();
builder.Services.AddSingleton<CertificateRenewalWorker>();
builder.Services.AddSingleton<EdgeTlsWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EdgeTlsWorker>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CertificateRenewalWorker>());

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

var app = builder.Build();

// Прогрев: схема SQLite и локальный admin-токен создаются при старте, а не при первом запросе.
app.Services.GetRequiredService<EdgeDatabase>();
app.Services.GetRequiredService<LocalAdminToken>();

app.UseExceptionHandler();

app.MapGet("/health", (EdgeIdentityStore identity, EdgeStore store, CloudClient cloud) => Results.Ok(new
{
    status = "Healthy",
    enrolled = identity.IsEnrolled,
    cloudReachable = cloud.IsReachable,
    pendingOutboxEvents = store.CountPendingEvents()
}));

app.UseSignedBodyCapture("/agent/v1");
app.MapAgentEndpoints();
app.MapLocalEndpoints();
app.MapOfflineCashEndpoints();

app.Run();

