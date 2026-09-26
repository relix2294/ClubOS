using System.Text.Json.Serialization;
using ClubOS.EdgeController;
using ClubOS.EdgeController.Api;
using ClubOS.EdgeController.Cloud;
using ClubOS.EdgeController.Storage;
using ClubOS.EdgeController.Workers;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("CLUBOS_");
builder.Services.Configure<EdgeOptions>(builder.Configuration.GetSection(EdgeOptions.Section));

var edgeOptions = builder.Configuration.GetSection(EdgeOptions.Section).Get<EdgeOptions>() ?? new EdgeOptions();

// Два listener'а: API агентов (LAN) и локальный admin API (только loopback) — ТЗ §25.2.4.
if (builder.Configuration["ASPNETCORE_URLS"] is null && builder.Configuration["urls"] is null)
{
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.ListenAnyIP(edgeOptions.AgentApiPort);
        k.ListenLocalhost(edgeOptions.LocalApiPort);
    });
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new EdgeDatabase(sp.GetRequiredService<IOptions<EdgeOptions>>().Value.DataPath));
builder.Services.AddSingleton(sp => new EdgeIdentityStore(sp.GetRequiredService<IOptions<EdgeOptions>>().Value.DataPath));
builder.Services.AddSingleton<EdgeSignals>();
builder.Services.AddSingleton<EdgeStore>();
builder.Services.AddSingleton<AgentAuth>();
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

app.MapAgentEndpoints();
app.MapLocalEndpoints();

app.Run();

