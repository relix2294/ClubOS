using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.CloudApi.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>
/// Касса Edge без интернета (D-023): PIN из Admin Web приходит на Edge, кассир входит в /cash, принимает оплату
/// при отключённом WAN; после восстановления связи оплата проводится в Cloud (смена открывается сама), а оплата,
/// принятая в Cloud, уменьшает долг на Edge (CashSync). Блокировка перебора PIN, CSRF, повтор по ключу.
/// </summary>
[Collection(CloudCollection.Name)]
public class OfflineCashTests(CloudFixture cloud)
{
    private static long Long(JsonElement e, string name) => e.GetProperty(name).GetInt64();

    private static HttpRequestMessage Post(string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-ClubOS-Cash", "1");
        return request;
    }

    [Fact]
    public async Task Pin_login_offline_payment_sync_and_cash_sync_back()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();

        // PIN: валидация и пароль.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/me/offline-pin",
            new { currentPassword = CloudFixture.OwnerPassword, pin = "123456" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/me/offline-pin",
            new { currentPassword = "wrong-password", pin = "583914" })).StatusCode);
        Assert.True((await owner.PostJsonAsync("/api/v1/me/offline-pin",
            new { currentPassword = CloudFixture.OwnerPassword, pin = "583914" })).GetProperty("offlinePinSet").GetBoolean());
        Assert.True((await owner.GetJsonAsync("/api/v1/me")).GetProperty("user").GetProperty("offlinePinSet").GetBoolean());

        await using var edge = new EdgeHost(cloud, AuthAndTenancyTests.TempPath(), await owner.EdgeTokenAsync(location));
        await using var agent = new AgentHost(edge, AuthAndTenancyTests.TempPath(), await owner.DeviceTokenAsync(location));
        var deviceId = await Wait.ForAsync(async () => agent.Runtime.DeviceId, what: "enrollment агента");
        await Wait.UntilAsync(async () =>
            (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}")).Str("status") == "Idle", what: "устройство online");

        var cash = edge.CreateClient();
        var ownerId = (await owner.GetJsonAsync("/api/v1/me")).GetProperty("user").Str("userId");
        await Wait.UntilAsync(async () =>
            (await cash.GetFromJsonAsync<JsonElement>("/cash/api/staff")).EnumerateArray().Any(s => s.Str("userId") == ownerId),
            what: "кассир с PIN в конфигурации Edge");
        Assert.Contains("касса клуба", await cash.GetStringAsync("/cash"));

        // Без входа — 401; без заголовка CSRF — 403; перебор PIN — блокировка.
        Assert.Equal(HttpStatusCode.Unauthorized, (await cash.GetAsync("/cash/api/payable")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cash.PostAsJsonAsync("/cash/api/login", new { userId = ownerId, pin = "583914" })).StatusCode);
        var attacker = edge.CreateClient();
        var victim = $"user_{Guid.NewGuid():N}";
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.SendAsync(Post("/cash/api/login", new { userId = victim, pin = "000111" }))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await attacker.SendAsync(Post("/cash/api/login", new { userId = victim, pin = "000111" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cash.SendAsync(Post("/cash/api/login", new { userId = ownerId, pin = "583914" }))).StatusCode);

        // Сессия с лимитом 30 мин (60,00) — предоплата видна на Edge.
        var sessionId = (await owner.PostJsonAsync($"/api/v1/devices/{deviceId}/sessions", new { durationMinutes = 30 })).Str("sessionId");
        var row = (JsonElement)await Wait.ForAsync(async () =>
        {
            var r = (await cash.GetFromJsonAsync<JsonElement>("/cash/api/payable")).GetProperty("payable").EnumerateArray()
                .FirstOrDefault(p => p.Str("sessionId") == sessionId);
            return r.ValueKind == JsonValueKind.Object ? (object)r : null;
        }, what: "сессия к оплате на Edge");
        Assert.Equal(6_000, Long(row, "dueMinorUnits"));

        // Интернета нет: оплата 40,00 наличными на Edge; больше долга — нельзя; повтор по ключу — та же оплата.
        edge.Wan.Offline = true;
        Assert.Equal(HttpStatusCode.Conflict, (await cash.SendAsync(Post("/cash/api/payments",
            new { sessionId, amountMinorUnits = 7_000, method = "Cash", idempotencyKey = "offline-key-0" }))).StatusCode);
        var first = await (await cash.SendAsync(Post("/cash/api/payments",
            new { sessionId, amountMinorUnits = 4_000, method = "Cash", idempotencyKey = "offline-key-1" }))).JsonAsync();
        var again = await (await cash.SendAsync(Post("/cash/api/payments",
            new { sessionId, amountMinorUnits = 4_000, method = "Cash", idempotencyKey = "offline-key-1" }))).JsonAsync();
        Assert.Equal(first.Str("paymentId"), again.Str("paymentId"));
        var offline = await cash.GetFromJsonAsync<JsonElement>("/cash/api/payable");
        Assert.Equal(2_000, Long(offline.GetProperty("payable").EnumerateArray().Single(p => p.Str("sessionId") == sessionId), "dueMinorUnits"));
        Assert.True(offline.GetProperty("pendingEvents").GetInt32() > 0);
        Assert.False(offline.GetProperty("payments")[0].GetProperty("sent").GetBoolean());
        await Task.Delay(1500);
        Assert.False(await cloud.WithDb(db => db.CashOperations.AnyAsync(x => x.SessionId == sessionId)));

        // Связь вернулась: оплата в Cloud в автоматически открытой смене от имени кассира.
        edge.Wan.Offline = false;
        var operation = await Wait.ForAsync(() => cloud.WithDb(db => db.CashOperations.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SessionId == sessionId)), what: "офлайн-оплата в Cloud");
        Assert.Equal(4_000, operation.AmountMinorUnits);
        Assert.Equal(PaymentMethods.Cash, operation.Method);
        Assert.Equal($"user:{ownerId}", operation.CreatedBy);
        var shift = await cloud.WithDb(db => db.CashShifts.AsNoTracking().SingleAsync(x => x.Id == operation.ShiftId));
        Assert.Null(shift.ClosedAtUtc);
        var desk = await owner.GetJsonAsync($"/api/v1/locations/{location.LocationId}/cash");
        Assert.Equal(4_000, Long(desk.GetProperty("shift").GetProperty("totals"), "cashPaymentsMinorUnits"));
        Assert.Equal(2_000, Long(desk.GetProperty("payable").EnumerateArray().Single(p => p.Str("sessionId") == sessionId), "dueMinorUnits"));
        await Wait.UntilAsync(async () =>
            (await cash.GetFromJsonAsync<JsonElement>("/cash/api/payable")).GetProperty("payments")[0].GetProperty("sent").GetBoolean(),
            what: "оплата отмечена отправленной");

        // Остаток принят в Cloud картой — Edge узнаёт об этом (CashSync): долга больше нет.
        await owner.PostJsonAsync($"/api/v1/sessions/{sessionId}/payments", new { amountMinorUnits = 2_000, method = "Card" });
        await Wait.UntilAsync(async () =>
            !(await cash.GetFromJsonAsync<JsonElement>("/cash/api/payable")).GetProperty("payable").EnumerateArray()
                .Any(p => p.Str("sessionId") == sessionId), what: "долг закрыт на Edge после оплаты в Cloud");

        // PIN снят — кассир пропадает из конфигурации Edge и его cookie больше не действует.
        await owner.PostJsonAsync("/api/v1/me/offline-pin", new { currentPassword = CloudFixture.OwnerPassword, pin = (string?)null });
        await Wait.UntilAsync(async () => (await cash.GetAsync("/cash/api/payable")).StatusCode == HttpStatusCode.Unauthorized,
            what: "вход отозван после снятия PIN");
    }
}
