using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

// edge-cli — локальное управление Edge без прямого доступа к SQL (ТЗ §25.2.4).
// Работает через локальный admin API Edge (только loopback) с токеном из каталога данных Edge.

var parsed = CliArgs.Parse(args);
if (parsed.Command is null or "help" or "--help" or "-h")
{
    PrintUsage();
    return parsed.Command is null ? 1 : 0;
}

var dataPath = parsed.Get("data") ?? Environment.GetEnvironmentVariable("CLUBOS_EDGE_DATA") ?? "edge-data";
var url = parsed.Get("url") ?? Environment.GetEnvironmentVariable("CLUBOS_EDGE_LOCAL_URL") ?? "http://127.0.0.1:7071";
var tokenPath = Path.Combine(dataPath, "local-admin.token");
if (!File.Exists(tokenPath))
{
    Console.Error.WriteLine($"Не найден {tokenPath}. Укажите каталог данных Edge: --data <path> (или CLUBOS_EDGE_DATA).");
    return 2;
}

using var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", File.ReadAllText(tokenPath).Trim());
var json = new JsonSerializerOptions { WriteIndented = true };

try
{
    switch (parsed.Command)
    {
        case "status":
            return await Print(http.GetAsync("local/v1/status"));
        case "devices":
            return await Print(http.GetAsync("local/v1/devices"));
        case "sessions":
            return await Print(http.GetAsync(parsed.Has("active") ? "local/v1/sessions?active=true" : "local/v1/sessions"));
        case "start":
            {
                var device = parsed.Positional.ElementAtOrDefault(0);
                if (device is null)
                {
                    Console.Error.WriteLine("Использование: edge-cli start <deviceId|имя устройства> [--actor <имя>]");
                    return 1;
                }

                var deviceId = await ResolveDeviceId(http, device);
                if (deviceId is null)
                {
                    Console.Error.WriteLine($"Устройство «{device}» не найдено на Edge.");
                    return 3;
                }

                return await Print(http.PostAsJsonAsync("local/v1/sessions", new { deviceId, actor = parsed.Get("actor") }));
            }
        case "end":
            {
                var sessionId = parsed.Positional.ElementAtOrDefault(0);
                if (sessionId is null)
                {
                    Console.Error.WriteLine("Использование: edge-cli end <sessionId> [--actor <имя>]");
                    return 1;
                }

                return await Print(http.PostAsJsonAsync($"local/v1/sessions/{Uri.EscapeDataString(sessionId)}/end",
                    new { actor = parsed.Get("actor") }));
            }
        default:
            Console.Error.WriteLine($"Неизвестная команда: {parsed.Command}");
            PrintUsage();
            return 1;
    }
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Edge недоступен по {url}: {ex.Message}");
    return 4;
}

async Task<int> Print(Task<HttpResponseMessage> call)
{
    using var response = await call;
    var body = await response.Content.ReadAsStringAsync();
    try
    {
        body = JsonSerializer.Serialize(JsonDocument.Parse(body).RootElement, json);
    }
    catch (JsonException)
    {
    }

    (response.IsSuccessStatusCode ? Console.Out : Console.Error).WriteLine(body);
    return response.IsSuccessStatusCode ? 0 : 5;
}

static async Task<string?> ResolveDeviceId(HttpClient http, string value)
{
    var devices = await http.GetFromJsonAsync<JsonElement>("local/v1/devices");
    foreach (var d in devices.EnumerateArray())
    {
        var id = d.GetProperty("deviceId").GetString();
        var name = d.GetProperty("displayName").GetString();
        if (string.Equals(id, value, StringComparison.Ordinal) ||
            string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
        {
            return id;
        }
    }

    return null;
}

static void PrintUsage() => Console.WriteLine("""
    edge-cli — локальное управление ClubOS Edge (работает без Cloud)

    Команды:
      status                              состояние Edge, связь с Cloud, очередь outbox
      devices                             устройства и их статус
      sessions [--active]                 последние / активные сессии
      start <deviceId|имя> [--actor X]    начать сессию локально (тариф из кэша)
      end <sessionId> [--actor X]         завершить сессию (идемпотентно)

    Опции:
      --data <path>   каталог данных Edge (env CLUBOS_EDGE_DATA, по умолчанию ./edge-data)
      --url <url>     локальный API Edge (env CLUBOS_EDGE_LOCAL_URL, по умолчанию http://127.0.0.1:7071)
    """);

internal sealed record CliArgs(string? Command, List<string> Positional, Dictionary<string, string?> Options)
{
    public static CliArgs Parse(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var key = args[i][2..];
                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && key != "active";
                options[key] = hasValue ? args[++i] : null;
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        return new CliArgs(positional.FirstOrDefault(), positional.Skip(1).ToList(), options);
    }

    public string? Get(string key) => Options.GetValueOrDefault(key);

    public bool Has(string key) => Options.ContainsKey(key);
}
