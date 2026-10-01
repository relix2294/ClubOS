using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>
/// Касса (M1): смена, оплата сессий (частичная, картой, идемпотентная), внесение/изъятие, возвраты с правами,
/// закрытие с расхождением, отчёт по выручке, изоляция tenant и иммутабельность операций в БД.
/// </summary>
[Collection(CloudCollection.Name)]
public class CashTests(CloudFixture cloud)
{
    /// <summary>Устройство и завершённая сессия прямо в БД — кассе не важно, как сессия прошла через Edge.</summary>
    private async Task<(string DeviceId, string SessionId)> EndedSessionAsync(TestLocation location, long total,
        DateTimeOffset? endedAt = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var end = endedAt ?? DateTimeOffset.UtcNow;
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
                EnrolledAtUtc = end.AddHours(-2)
            });
            db.Sessions.Add(new Session
            {
                Id = $"ses_{suffix}",
                TenantId = location.OrganizationId,
                DeviceId = $"dev_{suffix}",
                LocationId = location.LocationId,
                State = SessionState.Ended,
                Origin = "cloud",
                RequestedAtUtc = end.AddHours(-1),
                StartedAtUtc = end.AddHours(-1),
                EndedAtUtc = end,
                PricePerHourMinorUnits = 12_000,
                Currency = "TJS",
                Rounding = RoundingRule.CeilingPerMinute,
                RuleVersion = 1,
                TotalMinorUnits = total,
                StartedBy = "user:test",
                CorrelationId = $"cor_{suffix}"
            });
            return await db.SaveChangesAsync();
        });
        return ($"dev_{suffix}", $"ses_{suffix}");
    }

    private async Task<HttpClient> OperatorAsync(HttpClient owner, string locationId)
    {
        var email = $"cashier-{Guid.NewGuid():N}@club.test";
        var created = await owner.PostJsonAsync("/api/v1/staff",
            new { email, displayName = "Кассир", role = "Operator", locationIds = new[] { locationId } });
        var temp = created.Str("temporaryPassword");
        var client = cloud.Factory.CreateClient();
        var login = await client.PostJsonAsync("/api/v1/auth/login", new { email, password = temp });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Str("accessToken"));
        var changed = await client.PostJsonAsync("/api/v1/me/password", new { currentPassword = temp, newPassword = "Cashier-Pass-2026!" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", changed.Str("accessToken"));
        return client;
    }

    private static async Task<string> Code(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString()!;

    private static long Long(JsonElement e, string name) => e.GetProperty(name).GetInt64();

    [Fact]
    public async Task Shift_payments_movements_refunds_and_close_with_discrepancy()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var (deviceId, sessionId) = await EndedSessionAsync(location, total: 400);
        var cashier = await OperatorAsync(owner, location.LocationId);
        var desk = $"/api/v1/locations/{location.LocationId}/cash";

        // Без открытой смены деньги не принимаются.
        var early = await cashier.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/payments", new { amountMinorUnits = 400, method = "Cash" });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("shift_not_open", await Code(early));

        var shift = await cashier.PostJsonAsync($"{desk}/shifts", new { openingCashMinorUnits = 10_000 });
        var shiftId = shift.Str("shiftId");
        Assert.Equal("Кассир", shift.Str("openedByName"));
        var second = await owner.PostAsJsonAsync($"{desk}/shifts", new { openingCashMinorUnits = 0 });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var payable = (await cashier.GetJsonAsync(desk)).GetProperty("payable").EnumerateArray().Single(p => p.Str("sessionId") == sessionId);
        Assert.Equal(400, Long(payable, "dueMinorUnits"));

        // Больше долга — нельзя; частичная оплата наличными, остаток картой (идемпотентно).
        var over = await cashier.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/payments", new { amountMinorUnits = 500, method = "Cash" });
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        Assert.Equal("amount_exceeds_due", await Code(over));
        await cashier.PostJsonAsync($"/api/v1/sessions/{sessionId}/payments", new { amountMinorUnits = 200, method = "Cash" });
        var key = $"pay-{Guid.NewGuid():N}";
        var card = await cashier.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/payments",
            new { amountMinorUnits = 200, method = "Card", idempotencyKey = key });
        Assert.Equal(HttpStatusCode.Created, card.StatusCode);
        var repeat = await cashier.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/payments",
            new { amountMinorUnits = 200, method = "Card", idempotencyKey = key });
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal((await card.JsonAsync()).Str("operationId"), (await repeat.JsonAsync()).Str("operationId"));
        var paidAgain = await cashier.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/payments", new { amountMinorUnits = 1, method = "Cash" });
        Assert.Equal("session_paid", await Code(paidAgain));

        // Изъятие: не больше, чем есть в кассе.
        var tooMuch = await cashier.PostAsJsonAsync($"{desk}/movements", new { kind = "CashOut", amountMinorUnits = 1_000_000, reason = "Инкассация" });
        Assert.Equal("insufficient_cash", await Code(tooMuch));
        await cashier.PostJsonAsync($"{desk}/movements", new { kind = "CashOut", amountMinorUnits = 5_000, reason = "Инкассация" });

        var state = await cashier.GetJsonAsync(desk);
        Assert.DoesNotContain(state.GetProperty("payable").EnumerateArray(), p => p.Str("sessionId") == sessionId);
        var totals = state.GetProperty("shift").GetProperty("totals");
        Assert.Equal(200, Long(totals, "cashPaymentsMinorUnits"));
        Assert.Equal(200, Long(totals, "cardPaymentsMinorUnits"));
        Assert.Equal(5_000, Long(totals, "cashOutMinorUnits"));
        Assert.Equal(10_000 + 200 - 5_000, Long(totals, "expectedCashMinorUnits"));
        Assert.Equal(400, Long(totals, "revenueMinorUnits"));
        Assert.Equal(3, state.GetProperty("operations").GetArrayLength());

        // Возврат оплаченного (не переплаты) — только администратор; причина обязательна.
        var denied = await cashier.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/refunds",
            new { amountMinorUnits = 100, method = "Cash", reason = "Клиент недоволен" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var noReason = await owner.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/refunds", new { amountMinorUnits = 100, method = "Cash" });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        var tooBig = await owner.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/refunds",
            new { amountMinorUnits = 401, method = "Cash", reason = "Ошибка" });
        Assert.Equal("amount_exceeds_paid", await Code(tooBig));
        await owner.PostJsonAsync($"/api/v1/sessions/{sessionId}/refunds", new { amountMinorUnits = 100, method = "Cash", reason = "Клиент недоволен" });
        var afterRefund = (await cashier.GetJsonAsync(desk)).GetProperty("payable").EnumerateArray().Single(p => p.Str("sessionId") == sessionId);
        Assert.Equal(100, Long(afterRefund, "dueMinorUnits")); // снова долг: возврат — отдельная операция

        // Закрытие: пересчитали на 1,00 меньше учёта.
        var expected = 10_000 + 200 - 5_000 - 100;
        var closed = await cashier.PostJsonAsync($"/api/v1/cash/shifts/{shiftId}/close",
            new { countedCashMinorUnits = expected - 100, note = "Не хватает 1 сомони" });
        Assert.Equal(expected, Long(closed.GetProperty("totals"), "expectedCashMinorUnits"));
        Assert.Equal(-100, Long(closed, "discrepancyMinorUnits"));
        Assert.Equal("Кассир", closed.Str("closedByName"));
        var again = await cashier.PostAsJsonAsync($"/api/v1/cash/shifts/{shiftId}/close", new { countedCashMinorUnits = 0 });
        Assert.Equal("shift_closed", await Code(again));
        var afterClose = await cashier.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/payments", new { amountMinorUnits = 100, method = "Cash" });
        Assert.Equal("shift_not_open", await Code(afterClose));
        Assert.Equal(JsonValueKind.Null, (await cashier.GetJsonAsync(desk)).GetProperty("shift").ValueKind);

        // История смен — отчёт администратора; оператору недоступна.
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"{desk}/shifts")).StatusCode);
        var history = await owner.GetJsonAsync($"{desk}/shifts");
        Assert.Equal(-100, Long(history.EnumerateArray().Single(s => s.Str("shiftId") == shiftId), "discrepancyMinorUnits"));
        var detail = await owner.GetJsonAsync($"/api/v1/cash/shifts/{shiftId}");
        Assert.Equal(4, detail.GetProperty("operations").GetArrayLength()); // 2 оплаты, изъятие, возврат; повтор по ключу не создал пятую

        // Аудит на устройстве и смене.
        var audit = await owner.GetJsonAsync($"/api/v1/audit?target=device:{deviceId}");
        Assert.Equal(2, audit.EnumerateArray().Count(a => a.Str("action") == "cash.payment"));
        Assert.Contains(audit.EnumerateArray(), a => a.Str("action") == "cash.refund");
        var shiftAudit = await owner.GetJsonAsync($"/api/v1/audit?target=shift:{shiftId}");
        Assert.Contains(shiftAudit.EnumerateArray(), a => a.Str("action") == "cash.shift_closed");

        // Операции нельзя изменить или удалить даже напрямую в БД.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => cloud.WithDb(db =>
            db.Database.ExecuteSqlRawAsync("""UPDATE cash_operations SET "AmountMinorUnits" = 1 WHERE "ShiftId" = {0}""", shiftId)));
        Assert.Contains("append-only", ex.Message);
        await Assert.ThrowsAnyAsync<Exception>(() => cloud.WithDb(db =>
            db.Database.ExecuteSqlRawAsync("""DELETE FROM cash_operations WHERE "ShiftId" = {0}""", shiftId)));

        // Чужая организация не видит кассу и не может платить.
        var (otherEmail, otherPassword) = await cloud.CreateOrganizationWithOwnerAsync();
        var other = await cloud.LoginAsync(otherEmail, otherPassword);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(desk)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/payments", new { amountMinorUnits = 100, method = "Cash" })).StatusCode);
    }

    [Fact]
    public async Task Concurrent_payments_never_exceed_the_debt()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var (_, sessionId) = await EndedSessionAsync(location, total: 1_000);
        await owner.PostJsonAsync($"/api/v1/locations/{location.LocationId}/cash/shifts", new { openingCashMinorUnits = 0 });

        // Десять кассиров одновременно принимают полную сумму: пройти должна ровно одна оплата.
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            owner.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/payments", new { amountMinorUnits = 1_000, method = "Cash" })));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created),
            r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));

        var paid = await cloud.WithDb(db => db.CashOperations.Where(x => x.SessionId == sessionId).SumAsync(x => x.AmountMinorUnits));
        Assert.Equal(1_000, paid);
    }

    [Fact]
    public async Task Revenue_report_by_local_day()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var (_, paidSession) = await EndedSessionAsync(location, total: 600);
        await EndedSessionAsync(location, total: 300); // не оплачена
        var (_, yesterday) = await EndedSessionAsync(location, total: 900, DateTimeOffset.UtcNow.AddDays(-1));
        var desk = $"/api/v1/locations/{location.LocationId}/cash";
        await owner.PostJsonAsync($"{desk}/shifts", new { openingCashMinorUnits = 0 });
        await owner.PostJsonAsync($"/api/v1/sessions/{paidSession}/payments", new { amountMinorUnits = 400, method = "Cash" });
        await owner.PostJsonAsync($"/api/v1/sessions/{paidSession}/payments", new { amountMinorUnits = 200, method = "Card" });
        await owner.PostJsonAsync($"/api/v1/sessions/{yesterday}/payments", new { amountMinorUnits = 900, method = "Card" });
        await owner.PostJsonAsync($"/api/v1/sessions/{yesterday}/refunds", new { amountMinorUnits = 100, method = "Card", reason = "Жалоба" });

        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dushanbe");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        var from = today.AddDays(-1).ToString("yyyy-MM-dd");
        var to = today.ToString("yyyy-MM-dd");
        var report = await owner.GetJsonAsync($"/api/v1/reports/revenue?locationId={location.LocationId}&from={from}&to={to}");
        Assert.Equal("TJS", report.Str("currency"));
        var days = report.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(2, days.Count);

        var todayRow = days.Single(d => d.Str("date") == to);
        Assert.Equal(2, todayRow.GetProperty("sessionsEnded").GetInt32());
        Assert.Equal(900, Long(todayRow, "chargedMinorUnits"));
        // Деньги считаются по дню операции: вчерашнюю сессию оплатили сегодня.
        Assert.Equal(400, Long(todayRow, "cashMinorUnits"));
        Assert.Equal(1_100, Long(todayRow, "cardMinorUnits"));
        Assert.Equal(100, Long(todayRow, "refundsMinorUnits"));
        Assert.Equal(1_400, Long(todayRow, "netMinorUnits"));
        Assert.Equal(900, Long(days.Single(d => d.Str("date") == from), "chargedMinorUnits"));

        var totals = report.GetProperty("totals");
        Assert.Equal(3, totals.GetProperty("sessionsEnded").GetInt32());
        Assert.Equal(1_800, Long(totals, "chargedMinorUnits"));
        // Не оплачено: 300 по неоплаченной и 100 после возврата по вчерашней.
        Assert.Equal(400, Long(report, "unpaidMinorUnits"));

        var bad = await owner.GetAsync($"/api/v1/reports/revenue?locationId={location.LocationId}&from={to}&to={from}");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var tooLong = await owner.GetAsync($"/api/v1/reports/revenue?locationId={location.LocationId}&from=2026-01-01&to=2026-06-01");
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var cashier = await OperatorAsync(owner, location.LocationId);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await cashier.GetAsync($"/api/v1/reports/revenue?locationId={location.LocationId}&from={from}&to={to}")).StatusCode);
    }

    /// <summary>
    /// Живой сценарий через Edge: предоплата сессии с лимитом → продление → доплата → ранний конец →
    /// кассир возвращает переплату без администратора.
    /// </summary>
    [Fact]
    public async Task Limited_session_prepay_extend_topup_and_overpayment_refund()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        await using var edge = new EdgeHost(cloud, AuthAndTenancyTests.TempPath(), await owner.EdgeTokenAsync(location));
        await using var agent = new AgentHost(edge, AuthAndTenancyTests.TempPath(), await owner.DeviceTokenAsync(location));
        var deviceId = await Wait.ForAsync(async () => agent.Runtime.DeviceId, what: "enrollment агента");
        await Wait.UntilAsync(async () =>
            (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}")).Str("status") == "Idle", what: "устройство online");

        var cashier = await OperatorAsync(owner, location.LocationId);
        var desk = $"/api/v1/locations/{location.LocationId}/cash";
        await cashier.PostJsonAsync($"{desk}/shifts", new { openingCashMinorUnits = 50_000 });

        var sessionId = (await cashier.PostJsonAsync($"/api/v1/devices/{deviceId}/sessions", new { durationMinutes = 30 })).Str("sessionId");

        async Task<JsonElement> Payable(Func<JsonElement, bool> condition, string what) =>
            (JsonElement)await Wait.ForAsync(async () =>
            {
                var row = (await cashier.GetJsonAsync(desk)).GetProperty("payable").EnumerateArray()
                    .FirstOrDefault(p => p.Str("sessionId") == sessionId);
                return row.ValueKind == JsonValueKind.Object && condition(row) ? (object)row : null;
            }, what: what);

        var prepay = await Payable(p => p.Str("state") == "Active", "предоплата 30 минут");
        Assert.Equal(6_000, Long(prepay, "dueMinorUnits")); // 120 TJS/час × 30 мин
        await cashier.PostJsonAsync($"/api/v1/sessions/{sessionId}/payments", new { amountMinorUnits = 6_000, method = "Cash" });

        await cashier.PostAsync($"/api/v1/sessions/{sessionId}/extend", JsonContent.Create(new { minutes = 15 }));
        var topUp = await Payable(p => Long(p, "chargeMinorUnits") == 9_000, "доплата за продление");
        Assert.Equal(3_000, Long(topUp, "dueMinorUnits"));
        await cashier.PostJsonAsync($"/api/v1/sessions/{sessionId}/payments", new { amountMinorUnits = 3_000, method = "Card" });

        // Клиент ушёл раньше: итог по факту (минуты, округлённые вверх), остальное — переплата.
        await cashier.PostAsync($"/api/v1/sessions/{sessionId}/end", null);
        var overpaid = await Payable(p => p.Str("state") == "Ended", "переплата после раннего конца");
        var charge = Long(overpaid, "chargeMinorUnits");
        Assert.InRange(charge, 200, 1_000);
        Assert.Equal(charge - 9_000, Long(overpaid, "dueMinorUnits"));
        var refund = 9_000 - charge;

        // Кассир возвращает переплату сам, но не больше неё.
        var tooMuch = await cashier.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/refunds",
            new { amountMinorUnits = refund + 100, method = "Cash", reason = "Возврат переплаты" });
        Assert.Equal(HttpStatusCode.Forbidden, tooMuch.StatusCode);
        await cashier.PostJsonAsync($"/api/v1/sessions/{sessionId}/refunds",
            new { amountMinorUnits = refund, method = "Cash", reason = "Возврат переплаты" });
        var final = await cashier.GetJsonAsync(desk);
        Assert.DoesNotContain(final.GetProperty("payable").EnumerateArray(), p => p.Str("sessionId") == sessionId);
        Assert.Equal(charge, Long(final.GetProperty("shift").GetProperty("totals"), "revenueMinorUnits"));
        Assert.Equal(50_000 + 6_000 - refund, Long(final.GetProperty("shift").GetProperty("totals"), "expectedCashMinorUnits"));
    }
}
