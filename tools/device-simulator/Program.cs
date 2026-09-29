using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.Agent.Core;
using ClubOS.Agent.Core.PlayerShell;
using ClubOS.Contracts;
using Microsoft.Extensions.Logging;

// ClubOS Device Simulator — SIMULATED ПК для разработки и демо (ТЗ §3.4).

var opts = SimOptions.Parse(args);
using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information)
    .AddFilter("System.Net.Http", LogLevel.Warning));
var log = loggerFactory.CreateLogger("Simulator");

log.LogWarning("=== ClubOS DEVICE SIMULATOR: {Count} SIMULATED ПК → Edge {Edge} ===", opts.Count, opts.EdgeUrl);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var time = TimeProvider.System;
// HTTPS к Edge (D-007): как настоящий агент — CA проверяется по отпечатку и закрепляется. Отпечаток: env
// CLUBOS_SIM_EDGE_CA_FINGERPRINT или из Cloud (/api/v1/pki/ca), как администратор видит его в Admin Web.
string? cloudToken = null;
string? edgeCa = null;
var edgeUri = new Uri(opts.EdgeUrl.TrimEnd('/') + "/");
var fingerprint = Environment.GetEnvironmentVariable("CLUBOS_SIM_EDGE_CA_FINGERPRINT");
if (edgeUri.Scheme == Uri.UriSchemeHttps)
{
    for (var attempt = 1; edgeCa is null; attempt++)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                cloudToken ??= await LoginAsync(opts, cts.Token);
                using var cloudHttp = new HttpClient { BaseAddress = new Uri(opts.CloudUrl.TrimEnd('/') + "/") };
                cloudHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cloudToken);
                fingerprint = (await cloudHttp.GetFromJsonAsync<JsonElement>("api/v1/pki/ca", cts.Token)).GetProperty("fingerprintSha256").GetString();
            }

            edgeCa = await EdgeTls.FetchTrustedCaAsync(edgeUri, fingerprint!, cts.Token);
            log.LogInformation("CA Edge проверен по отпечатку {Fingerprint}", fingerprint![..16] + "…");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && attempt < 60)
        {
            log.LogWarning("TLS Edge ещё не готов ({Error}), повтор через 3 с", ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
        }
    }
}

using var edgeHttp = new HttpClient(EdgeTls.CreateHandler(() => edgeCa, fingerprint, TimeProvider.System, log))
{
    BaseAddress = edgeUri,
    Timeout = TimeSpan.FromSeconds(40)
};
var devices = new List<SimDevice>();

for (var i = 1; i <= opts.Count; i++)
{
    var name = $"SIM-PC-{i:D2}";
    var dataPath = Path.Combine(opts.StateDir, name);
    var identity = new AgentIdentityStore(dataPath, new FileKeyProtector());
    string? enrollmentToken = null;

    if (identity.Current is null)
    {
        try
        {
            cloudToken ??= await LoginAsync(opts, cts.Token);
            enrollmentToken = await CreateEnrollmentTokenAsync(opts, cloudToken, name, i, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            log.LogError("Не удалось получить enrollment-токен для {Name} из Cloud: {Error}", name, ex.Message);
            return 1;
        }
    }

    var agentOptions = new AgentOptions
    {
        EdgeUrl = opts.EdgeUrl,
        // Каждый ПК закрепляет CA сам, как настоящий агент (EnsureEdgeTrustAsync).
        EdgeCaFingerprint = fingerprint,
        EnrollmentToken = enrollmentToken,
        DataPath = dataPath,
        HeartbeatSeconds = 10,
        CommandPollSeconds = 20,
        Shell = new ShellOptions { Mode = opts.ShellMode }
    };
    var presenter = new ConsolePresenter(loggerFactory.CreateLogger<ConsolePresenter>(), $"SIMULATED {name}");
    var shell = new PlayerShellController(agentOptions, presenter, time, loggerFactory.CreateLogger<PlayerShellController>());
    var runtime = new AgentRuntime(agentOptions, identity, new EdgeClient(edgeHttp, identity, time),
        new SimulatedInventory(name, i), presenter,
        new CommandExecutor(presenter, new ExecutedCommandStore(dataPath), time, loggerFactory.CreateLogger<CommandExecutor>()),
        shell, time, loggerFactory.CreateLogger<AgentRuntime>());
    devices.Add(new SimDevice(i, name, runtime, presenter));
}

var tasks = devices.Select(d => Task.Run(() => d.Runtime.RunAsync(cts.Token))).ToList();

if (!Console.IsInputRedirected)
{
    Console.WriteLine("Команды: list | offline <N> | online <N> | quit");
    _ = Task.Run(() =>
    {
        while (!cts.IsCancellationRequested && Console.ReadLine() is { } line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var device = parts.Length > 1 && int.TryParse(parts[1], out var n) ? devices.FirstOrDefault(d => d.Index == n) : null;
            switch (parts.FirstOrDefault())
            {
                case "list":
                    foreach (var d in devices)
                    {
                        Console.WriteLine($"  {d.Index}. {d.Name} {d.Runtime.DeviceId} " +
                                          $"{(d.Runtime.Paused ? "OFFLINE (симуляция)" : "online")} · экран: {ShellSummary(d.Presenter.Shell)}");
                    }

                    break;
                case "offline" when device is not null:
                    device.Runtime.Paused = true;
                    Console.WriteLine($"{device.Name}: heartbeat остановлен — через ~30 с Edge отметит offline.");
                    break;
                case "online" when device is not null:
                    device.Runtime.Paused = false;
                    Console.WriteLine($"{device.Name}: heartbeat возобновлён.");
                    break;
                case "quit":
                    cts.Cancel();
                    break;
                default:
                    Console.WriteLine("Команды: list | offline <N> | online <N> | quit");
                    break;
            }
        }
    });
}

try
{
    await Task.WhenAll(tasks);
}
catch (OperationCanceledException)
{
}

log.LogWarning("Симулятор остановлен.");
return 0;

static async Task<string> LoginAsync(SimOptions o, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(o.Password))
    {
        throw new InvalidOperationException("Нужен пароль сотрудника: --password или env CLUBOS_SIM_PASSWORD.");
    }

    using var http = new HttpClient { BaseAddress = new Uri(o.CloudUrl.TrimEnd('/') + "/") };
    using var response = await http.PostAsJsonAsync("api/v1/auth/login", new { email = o.Email, password = o.Password }, ct);
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"Вход в Cloud не удался: {(int)response.StatusCode}");
    }

    return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("accessToken").GetString()!;
}

static async Task<string> CreateEnrollmentTokenAsync(SimOptions o, string accessToken, string name, int index,
    CancellationToken ct)
{
    using var http = new HttpClient { BaseAddress = new Uri(o.CloudUrl.TrimEnd('/') + "/") };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    var me = await http.GetFromJsonAsync<JsonElement>("api/v1/me", ct);
    var location = me.GetProperty("locations").EnumerateArray()
        .First(l => o.LocationId is null || l.GetProperty("locationId").GetString() == o.LocationId);
    var zones = location.GetProperty("zones").EnumerateArray().ToList();
    // Первые 3 ПК — Standard, остальные — VIP (демо сетки по зонам).
    var zone = zones.FirstOrDefault(z => z.GetProperty("name").GetString() == (index <= 3 ? "Standard" : "VIP"));
    var zoneId = (zone.ValueKind == JsonValueKind.Undefined ? zones[0] : zone).GetProperty("zoneId").GetString();

    using var response = await http.PostAsJsonAsync("api/v1/enrollment-tokens/device", new
    {
        locationId = location.GetProperty("locationId").GetString(),
        zoneId,
        displayName = name,
        simulated = true
    }, ct);
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("enrollmentToken").GetString()!;
}

static string ShellSummary(ShellState? state)
{
    if (state is null)
    {
        return "—";
    }

    var now = ShellClock.EdgeNow(state, DateTimeOffset.UtcNow);
    return state.View switch
    {
        ShellView.Session when state.Session is { } s => $"сессия, {ShellText.Hud(s, now).Time}, {ShellText.Hud(s, now).Cost}",
        ShellView.Ended when state.Ended is { } e => $"завершена ({ShellText.EndedSummary(e)})",
        _ => state.View.ToString()
    };
}

internal sealed record SimDevice(int Index, string Name, AgentRuntime Runtime, ConsolePresenter Presenter);

internal sealed class SimulatedInventory(string name, int index) : IInventoryProvider
{
    public DeviceInventory Collect() => new()
    {
        Hostname = name,
        WindowsVersion = "SIMULATED Windows 11 Pro",
        Cpu = "SIMULATED CPU (8 cores)",
        RamMegabytes = 16_384,
        Ipv4 = $"10.99.0.{index}",
        AgentVersion = $"sim-{BasicInventoryProvider.AgentVersion()}"
    };
}

internal sealed record SimOptions(string EdgeUrl, string CloudUrl, string Email, string? Password, int Count, string StateDir,
    string? LocationId, ShellMode ShellMode)
{
    public static SimOptions Parse(string[] args)
    {
        string? Get(string key)
        {
            var i = Array.IndexOf(args, "--" + key);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        return new SimOptions(
            Get("edge-url") ?? Environment.GetEnvironmentVariable("CLUBOS_SIM_EDGE_URL") ?? "http://localhost:7070",
            Get("cloud-url") ?? Environment.GetEnvironmentVariable("CLUBOS_SIM_CLOUD_URL") ?? "http://localhost:5080",
            Get("email") ?? Environment.GetEnvironmentVariable("CLUBOS_SIM_EMAIL") ?? "owner@demo.clubos.local",
            Get("password") ?? Environment.GetEnvironmentVariable("CLUBOS_SIM_PASSWORD"),
            int.TryParse(Get("count"), out var c) ? Math.Clamp(c, 1, 50) : 5,
            Get("state-dir") ?? Environment.GetEnvironmentVariable("CLUBOS_SIM_STATE") ?? "sim-data",
            Get("location") ?? Environment.GetEnvironmentVariable("CLUBOS_SIM_LOCATION"),
            // Player Shell симулированных ПК: состояние экрана видно в логе и в команде list.
            Enum.TryParse<ShellMode>(Get("shell") ?? Environment.GetEnvironmentVariable("CLUBOS_SIM_SHELL"), true, out var mode)
                ? mode
                : ShellMode.Enforced);
    }
}
