using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.CloudApi.Domain;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>
/// Клиенты и балансы: пополнение через кассу (аванс, не выручка), оплата сессии с баланса, возврат на баланс,
/// корректировки администратора, блокировка, отчёты, иммутабельный журнал и изоляция tenant.
/// </summary>
[Collection(CloudCollection.Name)]
public class ClientsTests(CloudFixture cloud)
{
    private async Task<(string DeviceId, string SessionId)> EndedSessionAsync(TestLocation location, long total, string? clientId)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var now = DateTimeOffset.UtcNow;
        await cloud.WithDb(async db =>
        {
            db.Devices.Add(new Device
            {
                Id = $"dev_{suffix}",
                TenantId = location.OrganizationId,
                LocationId = location.LocationId,
                ZoneId = location.ZoneId,
                DisplayName = $"PC-{suffix[..4]}",
                Simulated = true,
                CertificatePem = "test",
                EnrolledAtUtc = now.AddHours(-2)
            });
            db.Sessions.Add(new Session
            {
                Id = $"ses_{suffix}",
                TenantId = location.OrganizationId,
                DeviceId = $"dev_{suffix}",
                LocationId = location.LocationId,
                State = SessionState.Ended,
                Origin = "cloud",
                RequestedAtUtc = now.AddHours(-1),
                StartedAtUtc = now.AddHours(-1),
                EndedAtUtc = now,
                PricePerHourMinorUnits = 12_000,
                Currency = "TJS",
                Rounding = RoundingRule.CeilingPerMinute,
                RuleVersion = 1,
                TotalMinorUnits = total,
                StartedBy = "user:test",
                CorrelationId = $"cor_{suffix}",
                ClientId = clientId
            });
            return await db.SaveChangesAsync();
        });
        return ($"dev_{suffix}", $"ses_{suffix}");
    }

    private async Task<HttpClient> OperatorAsync(HttpClient owner, string locationId)
    {
        var email = $"clients-op-{Guid.NewGuid():N}@club.test";
        var created = await owner.PostJsonAsync("/api/v1/staff",
            new { email, displayName = "Кассир", role = "Operator", locationIds = new[] { locationId } });
        var temp = created.Str("temporaryPassword");
        var client = cloud.Factory.CreateClient();
        var login = await client.PostJsonAsync("/api/v1/auth/login", new { email, password = temp });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Str("accessToken"));
        var changed = await client.PostJsonAsync("/api/v1/me/password", new { currentPassword = temp, newPassword = "Clients-Op-2026!" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", changed.Str("accessToken"));
        return client;
    }

    private static async Task<string> Code(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString()!;

    private static long Long(JsonElement e, string name) => e.GetProperty(name).GetInt64();

    private static string Phone() => "+992 9" + Random.Shared.Next(10_000_000, 99_999_999);

    [Fact]
    public async Task Topup_pay_from_balance_refund_adjust_block_and_report()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var cashier = await OperatorAsync(owner, location.LocationId);
        var phone = Phone();

        // Создание: телефон нормализуется, дубль и мусор отклоняются.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await cashier.PostAsJsonAsync("/api/v1/clients", new { phone = "abc", displayName = "X", location.LocationId })).StatusCode);
        var client = await cashier.PostJsonAsync("/api/v1/clients", new { phone, displayName = "Фаррух", locationId = location.LocationId });
        var clientId = client.Str("clientId");
        Assert.Equal(new string(phone.Where(char.IsAsciiDigit).ToArray()), client.Str("phone"));
        Assert.Equal("TJS", client.Str("currency"));
        var dup = await owner.PostAsJsonAsync("/api/v1/clients", new { phone, displayName = "Другой", locationId = location.LocationId });
        Assert.Equal("client_exists", await Code(dup));
        var found = await cashier.GetJsonAsync($"/api/v1/clients?query={phone[^7..]}");
        Assert.Contains(found.EnumerateArray(), c => c.Str("clientId") == clientId);
        Assert.Contains((await cashier.GetJsonAsync("/api/v1/clients?query=фарр")).EnumerateArray(), c => c.Str("clientId") == clientId);

        // Пополнение — только в открытой смене; идемпотентно.
        var noShift = await cashier.PostAsJsonAsync($"/api/v1/clients/{clientId}/topups",
            new { locationId = location.LocationId, amountMinorUnits = 1_000, method = "Cash" });
        Assert.Equal("shift_not_open", await Code(noShift));
        await cashier.PostJsonAsync($"/api/v1/locations/{location.LocationId}/cash/shifts", new { openingCashMinorUnits = 0 });
        var key = $"top-{Guid.NewGuid():N}";
        var top1 = await cashier.PostAsJsonAsync($"/api/v1/clients/{clientId}/topups",
            new { locationId = location.LocationId, amountMinorUnits = 1_000, method = "Cash", idempotencyKey = key });
        var top1Again = await cashier.PostAsJsonAsync($"/api/v1/clients/{clientId}/topups",
            new { locationId = location.LocationId, amountMinorUnits = 1_000, method = "Cash", idempotencyKey = key });
        Assert.Equal(HttpStatusCode.Created, top1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, top1Again.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await cashier.PostAsJsonAsync($"/api/v1/clients/{clientId}/topups",
            new { locationId = location.LocationId, amountMinorUnits = 500, method = "Balance" })).StatusCode);
        await cashier.PostJsonAsync($"/api/v1/clients/{clientId}/topups",
            new { locationId = location.LocationId, amountMinorUnits = 500, method = "Card" });
        Assert.Equal(1_500, Long((await cashier.GetJsonAsync($"/api/v1/clients/{clientId}")).GetProperty("client"), "balanceMinorUnits"));

        // Сессия клиента: в кассе видно имя и баланс; оплата с баланса — по умолчанию клиента сессии.
        var (_, sessionId) = await EndedSessionAsync(location, 400, clientId);
        var desk = $"/api/v1/locations/{location.LocationId}/cash";
        var row = (await cashier.GetJsonAsync(desk)).GetProperty("payable").EnumerateArray().Single(p => p.Str("sessionId") == sessionId);
        Assert.Equal("Фаррух", row.Str("clientName"));
        Assert.Equal(1_500, Long(row, "clientBalanceMinorUnits"));
        await cashier.PostJsonAsync($"/api/v1/sessions/{sessionId}/payments", new { amountMinorUnits = 400, method = "Balance" });

        // Не хватает баланса — отказ; без клиента — нужен клиент.
        var (_, bigSession) = await EndedSessionAsync(location, 2_000, clientId);
        Assert.Equal("insufficient_balance", await Code(await cashier.PostAsJsonAsync($"/api/v1/sessions/{bigSession}/payments",
            new { amountMinorUnits = 2_000, method = "Balance" })));
        var (_, anonymous) = await EndedSessionAsync(location, 100, null);
        Assert.Equal("client_required", await Code(await cashier.PostAsJsonAsync($"/api/v1/sessions/{anonymous}/payments",
            new { amountMinorUnits = 100, method = "Balance" })));
        // Анонимную сессию можно оплатить с баланса, явно указав клиента.
        await cashier.PostJsonAsync($"/api/v1/sessions/{anonymous}/payments", new { amountMinorUnits = 100, method = "Balance", clientId });

        // Возврат администратором — на баланс клиента, с чьего баланса платили.
        await owner.PostJsonAsync($"/api/v1/sessions/{sessionId}/refunds",
            new { amountMinorUnits = 100, method = "Balance", reason = "Лагал ПК" });

        // Корректировка — только администратор, баланс не уходит в минус.
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/v1/clients/{clientId}/adjustments",
            new { amountMinorUnits = 50, reason = "Бонус" })).StatusCode);
        await owner.PostJsonAsync($"/api/v1/clients/{clientId}/adjustments", new { amountMinorUnits = 50, reason = "Бонус за отзыв" });
        Assert.Equal("balance_out_of_range", await Code(await owner.PostAsJsonAsync($"/api/v1/clients/{clientId}/adjustments",
            new { amountMinorUnits = -1_000_000, reason = "Ошибка" })));

        var card = await cashier.GetJsonAsync($"/api/v1/clients/{clientId}");
        // 1000 + 500 − 400 − 100 + 100 + 50
        Assert.Equal(1_150, Long(card.GetProperty("client"), "balanceMinorUnits"));
        var kinds = card.GetProperty("ledger").EnumerateArray().Select(e => e.Str("kind")).ToList();
        Assert.Equal(["Adjustment", "SessionRefund", "SessionPayment", "SessionPayment", "TopUp", "TopUp"], kinds);
        Assert.Equal(1_150, Long(card.GetProperty("ledger")[0], "balanceAfterMinorUnits"));

        // Касса: пополнения — в наличных и как аванс, выручка — оплаты сессий (в том числе с баланса).
        var totals = (await cashier.GetJsonAsync(desk)).GetProperty("shift").GetProperty("totals");
        Assert.Equal(1_000, Long(totals, "topUpCashMinorUnits"));
        Assert.Equal(500, Long(totals, "topUpCardMinorUnits"));
        Assert.Equal(500, Long(totals, "balancePaymentsMinorUnits"));
        Assert.Equal(100, Long(totals, "balanceRefundsMinorUnits"));
        Assert.Equal(1_000, Long(totals, "expectedCashMinorUnits"));
        Assert.Equal(400, Long(totals, "revenueMinorUnits"));

        // Блокировка — администратор; заблокированный клиент не тратит баланс.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await cashier.PostAsJsonAsync($"/api/v1/clients/{clientId}", new { isBlocked = true })).StatusCode);
        await owner.PostJsonAsync($"/api/v1/clients/{clientId}", new { isBlocked = true });
        var (_, blockedSession) = await EndedSessionAsync(location, 100, clientId);
        Assert.Equal("client_blocked", await Code(await cashier.PostAsJsonAsync($"/api/v1/sessions/{blockedSession}/payments",
            new { amountMinorUnits = 100, method = "Balance" })));

        // Отчёт: «с баланса» и «пополнения» отдельно, итог — оплаты минус возвраты.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dushanbe");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime).ToString("yyyy-MM-dd");
        var day = (await owner.GetJsonAsync($"/api/v1/reports/revenue?locationId={location.LocationId}&from={today}&to={today}"))
            .GetProperty("days")[0];
        Assert.Equal(500, Long(day, "balanceMinorUnits"));
        Assert.Equal(1_500, Long(day, "topUpsMinorUnits"));
        Assert.Equal(400, Long(day, "netMinorUnits"));

        // Журнал баланса нельзя изменить даже в БД; чужая организация клиента не видит.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => cloud.WithDb(db =>
            db.Database.ExecuteSqlRawAsync("""UPDATE client_ledger SET "AmountMinorUnits" = 0 WHERE "ClientId" = {0}""", clientId)));
        Assert.Contains("append-only", ex.Message);
        var (otherEmail, otherPassword) = await cloud.CreateOrganizationWithOwnerAsync();
        var other = await cloud.LoginAsync(otherEmail, otherPassword);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/clients/{clientId}")).StatusCode);
        Assert.DoesNotContain((await other.GetJsonAsync($"/api/v1/clients?query={phone[^7..]}")).EnumerateArray(),
            c => c.Str("clientId") == clientId);
    }

    [Fact]
    public async Task Session_can_be_started_for_a_client()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var (deviceId, _) = await EndedSessionAsync(location, 0, null);
        var client = await owner.PostJsonAsync("/api/v1/clients", new { phone = Phone(), displayName = "Гость", locationId = location.LocationId });

        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/v1/devices/{deviceId}/sessions",
            new { clientId = "cli_missing" })).StatusCode);
        var started = await owner.PostJsonAsync($"/api/v1/devices/{deviceId}/sessions",
            new { durationMinutes = 60, clientId = client.Str("clientId") });
        Assert.Equal(client.Str("clientId"), started.Str("clientId"));
        var sessions = await owner.GetJsonAsync($"/api/v1/devices/{deviceId}/sessions");
        Assert.Equal(client.Str("clientId"), sessions[0].Str("clientId"));
    }
}
