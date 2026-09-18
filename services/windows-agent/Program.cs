using ClubOS.WindowsAgent.Config;
using ClubOS.WindowsAgent.Identity;
using ClubOS.WindowsAgent.Inventory;
using ClubOS.WindowsAgent.Ipc;
using ClubOS.WindowsAgent.SessionHost;
using ClubOS.WindowsAgent.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Режим session-host: WinForms overlay в интерактивной сессии (запускается отдельным процессом).
if (args.Contains("--session-host"))
{
    var pipeName = Environment.GetEnvironmentVariable("CLUBOS_AGENT_PIPE") ?? "clubos-agent-ui";
    var uiThread = new Thread(() => SessionHostRunner.Run(pipeName));
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();
    return;
}

// Режим службы/консоли: enrollment + heartbeat + команды.
var builder = Host.CreateApplicationBuilder(args);

var section = builder.Configuration.GetSection(AgentOptions.SectionName);
builder.Services.Configure<AgentOptions>(section);

var opt = section.Get<AgentOptions>() ?? new AgentOptions();
var token = Environment.GetEnvironmentVariable("CLUBOS_ENROLLMENT_TOKEN") ?? opt.EnrollmentToken;
var cloudUrl = Environment.GetEnvironmentVariable("CLUBOS_CLOUD_URL") ?? opt.CloudBaseUrl;
var edgeUrl = Environment.GetEnvironmentVariable("CLUBOS_EDGE_URL") ?? opt.EdgeBaseUrl;

builder.Services.PostConfigure<AgentOptions>(o =>
{
    o.EnrollmentToken = token;
    o.CloudBaseUrl = cloudUrl;
    o.EdgeBaseUrl = edgeUrl;
});

builder.Services.AddWindowsService(options => options.ServiceName = "ClubOS Agent");

builder.Services.AddSingleton<InventoryCollector>();
builder.Services.AddSingleton<UiDispatcher>();
builder.Services.AddSingleton<CommandExecutor>();
builder.Services.AddHttpClient<EnrollmentClient>(client => client.BaseAddress = new Uri(cloudUrl));
builder.Services.AddHttpClient<EdgeApiClient>(client => client.BaseAddress = new Uri(edgeUrl));
builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();
host.Run();
