using ClubOS.Agent.Core;
using ClubOS.Agent.Service;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;

// ClubOS Windows Agent: .NET Windows Service (Session 0) + console mode для разработки.
// UI не рисуется из службы — только через AgentSessionHost (Named Pipe), ТЗ §11.1.

var builder = Host.CreateApplicationBuilder(args);

var defaultDataPath = OperatingSystem.IsWindows()
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClubOS", "Agent")
    : Path.Combine(AppContext.BaseDirectory, "agent-data");

// Конфигурация: appsettings.json рядом с exe → %ProgramData%\ClubOS\Agent\agent.json → env CLUBOS_ → аргументы.
builder.Configuration.AddJsonFile(Path.Combine(defaultDataPath, "agent.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("CLUBOS_");
builder.Configuration.AddCommandLine(args);

const string ServiceName = "ClubOSAgent";
builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
// Источник Event Log = имя службы (регистрируется install-agent.ps1); по умолчанию было бы имя сборки.
builder.Services.Configure<EventLogSettings>(o => o.SourceName = ServiceName);
builder.Services.Configure<AgentOptions>(o =>
{
    o.DataPath = defaultDataPath;
    builder.Configuration.GetSection(AgentOptions.Section).Bind(o);
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AgentOptions>>().Value);
builder.Services.AddSingleton<IKeyProtector>(_ =>
    OperatingSystem.IsWindows() ? new DpapiKeyProtector() : new FileKeyProtector());
builder.Services.AddSingleton<IInventoryProvider>(_ =>
    OperatingSystem.IsWindows() ? new WindowsInventoryProvider() : new BasicInventoryProvider());

if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<SessionHostPresenter>();
    builder.Services.AddSingleton<IUserPresenter>(sp => sp.GetRequiredService<SessionHostPresenter>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionHostPresenter>());
}
else
{
    builder.Services.AddSingleton<IUserPresenter>(sp =>
        new ConsolePresenter(sp.GetRequiredService<ILogger<ConsolePresenter>>()));
}

builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<AgentOptions>();
    return new AgentIdentityStore(o.DataPath, sp.GetRequiredService<IKeyProtector>());
});
builder.Services.AddSingleton(sp => new ExecutedCommandStore(sp.GetRequiredService<AgentOptions>().DataPath));
builder.Services.AddHttpClient<EdgeClient>((sp, http) =>
{
    http.BaseAddress = new Uri(sp.GetRequiredService<AgentOptions>().EdgeUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(40);
});
builder.Services.AddSingleton<CommandExecutor>();
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<AgentRuntime>(sp,
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(EdgeClient)) is var http
        ? new EdgeClient(http, sp.GetRequiredService<AgentIdentityStore>(), sp.GetRequiredService<TimeProvider>())
        : throw new InvalidOperationException()));
builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();
host.Run();
