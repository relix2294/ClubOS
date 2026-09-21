using System.Net.Http.Json;

// edge-cli — offline управление сессиями Edge через локальный HTTP API (ТЗ §25.2.4).
// Никакого прямого SQL: всё идёт через тот же API, что и Agent/Admin.
// Базовый URL: env EDGE_API_URL (по умолчанию http://localhost:5080).

var baseUrl = Environment.GetEnvironmentVariable("EDGE_API_URL") ?? "http://localhost:5080";
if (!baseUrl.EndsWith('/'))
{
    baseUrl += "/";
}

using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    switch (args[0])
    {
        case "start":
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Использование: edge-cli start <deviceId> [actor]");
                return 1;
            }

            var actor = args.Length > 2 ? args[2] : "edge-cli";
            return await SendAsync(HttpMethod.Post, "api/edge/sessions/start",
                new { deviceId = args[1], actor });

        case "end":
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Использование: edge-cli end <sessionId>");
                return 1;
            }

            return await SendAsync(HttpMethod.Post, $"api/edge/sessions/{args[1]}/end", null);

        case "get":
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Использование: edge-cli get <sessionId>");
                return 1;
            }

            return await SendAsync(HttpMethod.Get, $"api/edge/sessions/{args[1]}", null);

        case "status":
            return await SendAsync(HttpMethod.Get, "health/ready", null);

        default:
            PrintUsage();
            return 1;
    }
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Не удалось связаться с Edge ({baseUrl}): {ex.Message}");
    return 2;
}

async Task<int> SendAsync(HttpMethod method, string path, object? body)
{
    using var request = new HttpRequestMessage(method, path);
    if (body is not null)
    {
        request.Content = JsonContent.Create(body);
    }

    using var response = await http.SendAsync(request);
    var content = await response.Content.ReadAsStringAsync();
    Console.WriteLine($"HTTP {(int)response.StatusCode} {response.StatusCode}");
    if (!string.IsNullOrWhiteSpace(content))
    {
        Console.WriteLine(content);
    }

    return response.IsSuccessStatusCode ? 0 : 2;
}

static void PrintUsage()
{
    Console.WriteLine("""
        edge-cli — offline управление сессиями Edge Controller.

        Команды:
          start <deviceId> [actor]   начать тестовую сессию
          end   <sessionId>          завершить сессию (идемпотентно)
          get   <sessionId>          показать сессию
          status                     проверить готовность Edge

        Переменные окружения:
          EDGE_API_URL   базовый URL Edge (по умолчанию http://localhost:5080)
        """);
}
