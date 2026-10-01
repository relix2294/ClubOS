using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ClubOS.Contracts;
using ClubOS.EdgeController.Storage;

namespace ClubOS.EdgeController.Api;

public sealed record OfflineLoginRequest(string? UserId, string? Pin);

public sealed record OfflinePaymentRequest(string? SessionId, long AmountMinorUnits, string? Method, string? IdempotencyKey);

/// <summary>
/// Касса Edge без интернета (D-023): страница <c>/cash</c> в сети клуба (порты API агентов). Вход — сотрудник с
/// PIN офлайн-кассы из Admin Web (хэш приходит в конфигурации Cloud). Только приём оплаты сессий наличными и
/// картой; оплаты уходят в Cloud событиями и проводятся в кассовую смену. Защита: cookie с HMAC (ключ живёт до
/// перезапуска Edge), SameSite=Strict, заголовок X-ClubOS-Cash для POST, 5 неверных PIN — блокировка на 5 минут.
/// </summary>
public static class OfflineCashEndpoints
{
    public const string CookieName = "clubos_cash";
    public const string CsrfHeader = "X-ClubOS-Cash";
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset Until)> Failures = new();

    public static void MapOfflineCashEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/cash", () => Results.Content(OfflineCashPage.Html, "text/html; charset=utf-8"));
        var api = app.MapGroup("/cash/api");
        api.MapGet("/staff", (EdgeStore store) =>
            Results.Ok(store.ListOfflineStaff().Select(s => new { s.UserId, s.DisplayName })));
        api.MapPost("/login", Login);
        api.MapPost("/logout", (HttpContext http) =>
        {
            http.Response.Cookies.Delete(CookieName);
            return Results.NoContent();
        });
        api.MapGet("/me", (HttpContext http, EdgeStore store, TimeProvider time) =>
            Cashier(http, store, time) is { } c ? Results.Ok(new { c.UserId, c.DisplayName }) : Results.Unauthorized());
        api.MapGet("/payable", (HttpContext http, EdgeStore store, TimeProvider time) =>
        {
            if (Cashier(http, store, time) is null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new
            {
                location = store.GetKv("location_name"),
                currency = store.GetKv("currency"),
                pendingEvents = store.CountPendingEvents(),
                payable = store.GetOfflinePayable().Where(p => p.DueMinorUnits > 0),
                payments = store.ListOfflinePayments(50)
            });
        });
        api.MapPost("/payments", Pay);
    }

    private static string Sign(string userId, long expires)
    {
        var data = $"{userId}|{expires}";
        var mac = HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(data));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(data)) + "." + Convert.ToBase64String(mac);
    }

    /// <summary>Кассир по cookie; кассира, удалённого из конфигурации (отключён, сменил PIN или роль), больше нет.</summary>
    private static OfflineStaffMember? Cashier(HttpContext http, EdgeStore store, TimeProvider time)
    {
        var cookie = http.Request.Cookies[CookieName];
        var parts = cookie?.Split('.');
        if (parts is not [var dataText, var macText])
        {
            return null;
        }

        try
        {
            var data = Convert.FromBase64String(dataText);
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Key, data), Convert.FromBase64String(macText)))
            {
                return null;
            }

            var fields = Encoding.UTF8.GetString(data).Split('|');
            return fields is [var userId, var expiresText] && long.TryParse(expiresText, out var expires) &&
                   time.GetUtcNow().ToUnixTimeSeconds() < expires
                ? store.GetOfflineStaff(userId)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool CsrfOk(HttpContext http) => http.Request.Headers[CsrfHeader] == "1";

    private static IResult Login(OfflineLoginRequest request, HttpContext http, EdgeStore store, TimeProvider time,
        ILoggerFactory loggers)
    {
        if (!CsrfOk(http))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var now = time.GetUtcNow();
        var userId = request.UserId ?? string.Empty;
        if (Failures.TryGetValue(userId, out var state) && state.Failures >= MaxFailures && state.Until > now)
        {
            return Results.Json(new { detail = $"Слишком много неверных PIN. Попробуйте через {Math.Ceiling((state.Until - now).TotalMinutes)} мин." },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var staff = store.GetOfflineStaff(userId);
        // Хэш для несуществующего сотрудника всё равно считается — время ответа не выдаёт, есть ли такой.
        var ok = OfflinePin.Verify(request.Pin ?? string.Empty, staff?.PinHash ?? DummyHash) && staff is not null;
        if (!ok)
        {
            if (Failures.Count > 10_000)
            {
                Failures.Clear(); // перебор случайных userId не раздувает память
            }

            var failures = (Failures.TryGetValue(userId, out var f) && f.Until > now ? f.Failures : 0) + 1;
            Failures[userId] = (failures, now + LockoutTime);
            loggers.CreateLogger("OfflineCash").LogWarning("Касса Edge: неверный PIN для {UserId} с {Ip} ({Count})", userId,
                http.Connection.RemoteIpAddress, failures);
            return Results.Json(new { detail = "Неверный сотрудник или PIN." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        Failures.TryRemove(userId, out _);
        http.Response.Cookies.Append(CookieName, Sign(staff!.UserId, (now + SessionLifetime).ToUnixTimeSeconds()), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
            Path = "/cash",
            MaxAge = SessionLifetime
        });
        loggers.CreateLogger("OfflineCash").LogInformation("Касса Edge: вход {Name}", staff.DisplayName);
        return Results.Ok(new { staff.UserId, staff.DisplayName });
    }

    private static readonly string DummyHash = OfflinePin.Hash("000000", 1_000);

    private static async Task<IResult> Pay(OfflinePaymentRequest request, HttpContext http, EdgeStore store, TimeProvider time,
        CancellationToken ct)
    {
        if (!CsrfOk(http))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var cashier = Cashier(http, store, time);
        if (cashier is null)
        {
            return Results.Unauthorized();
        }

        if (request.SessionId is null || request.AmountMinorUnits is < 1 or > 100_000_000 ||
            request.Method is not ("Cash" or "Card") ||
            request.IdempotencyKey is { Length: < 8 or > 64 })
        {
            return Results.BadRequest(new { detail = "Укажите сессию, сумму и способ оплаты (наличные или карта)." });
        }

        var (outcome, payment, due) = await store.RecordOfflinePaymentAsync(request.SessionId, request.AmountMinorUnits, request.Method,
            cashier, request.IdempotencyKey, ct);
        return outcome switch
        {
            OfflinePaymentOutcome.Recorded => Results.Ok(payment),
            OfflinePaymentOutcome.Replayed => Results.Ok(payment),
            OfflinePaymentOutcome.UnknownSession => Results.NotFound(new { detail = "Сессия не найдена на Edge." }),
            OfflinePaymentOutcome.ExceedsDue => Results.Conflict(new { detail = $"Сумма больше долга ({due / 100m:0.00})." }),
            _ => Results.Conflict(new { detail = "По сессии нет долга." })
        };
    }
}
