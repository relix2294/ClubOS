using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.CloudApi.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>
/// Бар / POS: каталог и склад (права, приход, списание, инвентаризация), чек в смене (наличные, баланс),
/// остатки под блокировкой, идемпотентность, возврат чека целиком, итоги смены и отчёт, иммутабельный склад.
/// </summary>
[Collection(CloudCollection.Name)]
public class PosTests(CloudFixture cloud)
{
    private static async Task<string> Code(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString()!;

    private static long Long(JsonElement e, string name) => e.GetProperty(name).GetInt64();

    private async Task<HttpClient> OperatorAsync(HttpClient owner, string locationId)
    {
        var email = $"pos-op-{Guid.NewGuid():N}@club.test";
        var created = await owner.PostJsonAsync("/api/v1/staff",
            new { email, displayName = "Бармен", role = "Operator", locationIds = new[] { locationId } });
        var temp = created.Str("temporaryPassword");
        var client = cloud.Factory.CreateClient();
        var login = await client.PostJsonAsync("/api/v1/auth/login", new { email, password = temp });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Str("accessToken"));
        var changed = await client.PostJsonAsync("/api/v1/me/password", new { currentPassword = temp, newPassword = "Pos-Operator-2026!" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", changed.Str("accessToken"));
        return client;
    }

    [Fact]
    public async Task Catalog_stock_sale_balance_refund_totals_and_report()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var bar = await OperatorAsync(owner, location.LocationId);
        var products = $"/api/v1/locations/{location.LocationId}/products";

        // Каталог ведёт администратор.
        Assert.Equal(HttpStatusCode.Forbidden, (await bar.PostAsJsonAsync(products, new { name = "X", priceMinorUnits = 100 })).StatusCode);
        Assert.Equal("invalid_product", await Code(await owner.PostAsJsonAsync(products, new { name = "", priceMinorUnits = 100 })));
        var cola = await owner.PostJsonAsync(products, new { name = "Кола 0,5", category = "Напитки", priceMinorUnits = 1_000 });
        var tea = await owner.PostJsonAsync(products, new { name = "Чай", category = "Напитки", priceMinorUnits = 500, trackStock = false });
        var chips = await owner.PostJsonAsync(products, new { name = "Чипсы", category = "Снеки", priceMinorUnits = 800 });
        Assert.Equal("duplicate_product", await Code(await owner.PostAsJsonAsync(products, new { name = "Кола 0,5", priceMinorUnits = 1 })));
        var colaId = cola.Str("productId");

        // Склад: приход 10, списание без основания — нет, инвентаризация до 9.
        var stock = $"/api/v1/products/{colaId}/stock";
        Assert.Equal(10, (await owner.PostJsonAsync(stock, new { kind = "Receipt", quantity = 10 })).GetProperty("stockQuantity").GetInt32());
        Assert.Equal("invalid_reason", await Code(await owner.PostAsJsonAsync(stock, new { kind = "WriteOff", quantity = 1 })));
        Assert.Equal("insufficient_stock", await Code(await owner.PostAsJsonAsync(stock, new { kind = "WriteOff", quantity = 50, reason = "Бой" })));
        Assert.Equal(9, (await owner.PostJsonAsync(stock, new { kind = "Count", quantity = 9, reason = "Пересчёт" })).GetProperty("stockQuantity").GetInt32());
        Assert.Equal("stock_not_tracked", await Code(await owner.PostAsJsonAsync($"/api/v1/products/{tea.Str("productId")}/stock", new { kind = "Receipt", quantity = 1 })));
        Assert.Equal(HttpStatusCode.Forbidden, (await bar.PostAsJsonAsync(stock, new { kind = "Receipt", quantity = 1 })).StatusCode);

        // Витрина бармена: только активные.
        await owner.PostJsonAsync($"/api/v1/products/{chips.Str("productId")}", new { name = "Чипсы", category = "Снеки", priceMinorUnits = 800, isActive = false });
        var shelf = await bar.GetJsonAsync(products);
        Assert.Equal(2, shelf.GetArrayLength());
        Assert.Equal(3, (await owner.GetJsonAsync($"{products}?all=true")).GetArrayLength());

        // Чек: без смены — нет.
        var sales = $"/api/v1/locations/{location.LocationId}/sales";
        var items = new[] { new { productId = colaId, quantity = 1 }, new { productId = tea.Str("productId"), quantity = 1 }, new { productId = colaId, quantity = 1 } };
        Assert.Equal("shift_not_open", await Code(await bar.PostAsJsonAsync(sales, new { items, method = "Cash" })));
        await bar.PostJsonAsync($"/api/v1/locations/{location.LocationId}/cash/shifts", new { openingCashMinorUnits = 0 });

        var key = $"sale-{Guid.NewGuid():N}";
        var sale = await bar.PostJsonAsync(sales, new { items, method = "Cash", idempotencyKey = key });
        Assert.Equal(2_500, Long(sale, "totalMinorUnits")); // 2 × 10,00 + 5,00 (одинаковые строки — одна позиция)
        Assert.Equal(2, sale.GetProperty("items").GetArrayLength());
        var replay = await bar.PostJsonAsync(sales, new { items, method = "Cash", idempotencyKey = key });
        Assert.Equal(sale.Str("saleId"), replay.Str("saleId"));
        Assert.Equal(7, await cloud.WithDb(db => db.Products.Where(p => p.Id == colaId).Select(p => p.StockQuantity).SingleAsync()));

        Assert.Equal("insufficient_stock", await Code(await bar.PostAsJsonAsync(sales, new { items = new[] { new { productId = colaId, quantity = 8 } }, method = "Card" })));
        Assert.Equal("product_unavailable", await Code(await bar.PostAsJsonAsync(sales, new { items = new[] { new { productId = chips.Str("productId"), quantity = 1 } }, method = "Card" })));
        Assert.Equal("invalid_items", await Code(await bar.PostAsJsonAsync(sales, new { items = Array.Empty<object>(), method = "Card" })));

        // С баланса клиента.
        var client = await bar.PostJsonAsync("/api/v1/clients", new { phone = "+992 92 " + Random.Shared.Next(1_000_000, 9_999_999), displayName = "Гость бара", locationId = location.LocationId });
        var clientId = client.Str("clientId");
        await bar.PostJsonAsync($"/api/v1/clients/{clientId}/topups", new { locationId = location.LocationId, amountMinorUnits = 1_500, method = "Card" });
        var balanceItems = new[] { new { productId = colaId, quantity = 1 } };
        Assert.Equal("client_required", await Code(await bar.PostAsJsonAsync(sales, new { items = balanceItems, method = "Balance" })));
        var balanceSale = await bar.PostJsonAsync(sales, new { items = balanceItems, method = "Balance", clientId });
        Assert.Equal("Гость бара", balanceSale.Str("clientName"));
        Assert.Equal("insufficient_balance", await Code(await bar.PostAsJsonAsync(sales, new { items = balanceItems, method = "Balance", clientId })));
        var ledger = (await bar.GetJsonAsync($"/api/v1/clients/{clientId}")).GetProperty("ledger");
        Assert.Equal("ProductPayment", ledger[0].Str("kind"));
        Assert.Equal(500, Long(ledger[0], "balanceAfterMinorUnits"));

        // Итоги смены: бар — выручка; наличные — только наличный чек.
        var desk = $"/api/v1/locations/{location.LocationId}/cash";
        var totals = (await bar.GetJsonAsync(desk)).GetProperty("shift").GetProperty("totals");
        Assert.Equal(3_500, Long(totals, "productSalesMinorUnits"));
        Assert.Equal(3_500, Long(totals, "revenueMinorUnits"));
        Assert.Equal(2_500, Long(totals, "expectedCashMinorUnits"));

        // Возврат: только администратор, причина обязательна, целиком, один раз.
        var refundUrl = $"/api/v1/sales/{sale.Str("saleId")}/refund";
        Assert.Equal(HttpStatusCode.Forbidden, (await bar.PostAsJsonAsync(refundUrl, new { reason = "Брак" })).StatusCode);
        Assert.Equal("invalid_reason", await Code(await owner.PostAsJsonAsync(refundUrl, new { reason = "" })));
        var refunded = await owner.PostJsonAsync(refundUrl, new { reason = "Тёплая кола" });
        Assert.Equal("Refunded", refunded.Str("status"));
        Assert.Equal("sale_refunded", await Code(await owner.PostAsJsonAsync(refundUrl, new { reason = "Ещё раз" })));
        Assert.Equal(8, await cloud.WithDb(db => db.Products.Where(p => p.Id == colaId).Select(p => p.StockQuantity).SingleAsync()));
        await owner.PostJsonAsync($"/api/v1/sales/{balanceSale.Str("saleId")}/refund", new { reason = "Отказ" });
        Assert.Equal(1_500, Long((await bar.GetJsonAsync($"/api/v1/clients/{clientId}")).GetProperty("client"), "balanceMinorUnits"));

        totals = (await bar.GetJsonAsync(desk)).GetProperty("shift").GetProperty("totals");
        Assert.Equal(3_500, Long(totals, "productRefundsMinorUnits"));
        Assert.Equal(0, Long(totals, "revenueMinorUnits"));
        Assert.Equal(0, Long(totals, "expectedCashMinorUnits"));
        var list = await bar.GetJsonAsync(sales);
        Assert.Equal(2, list.GetArrayLength());
        Assert.All(list.EnumerateArray(), s => Assert.Equal("Refunded", s.Str("status")));

        // Отчёт: бар за день — продажи минус возвраты.
        var today = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5)).ToString("yyyy-MM-dd");
        var report = await owner.GetJsonAsync($"/api/v1/reports/revenue?locationId={location.LocationId}&from={today}&to={today}");
        Assert.Equal(0, Long(report.GetProperty("totals"), "productsMinorUnits"));

        // Журнал склада: приход, инвентаризация, две продажи и два возврата (кола была в обоих чеках); изменить нельзя.
        var movements = await owner.GetJsonAsync($"/api/v1/products/{colaId}/movements");
        Assert.Equal(["Return", "Return", "Sale", "Sale", "Count", "Receipt"], movements.EnumerateArray().Select(m => m.Str("kind")).ToArray());
        var error = await Assert.ThrowsAnyAsync<Exception>(() => cloud.WithDb(db =>
            db.Database.ExecuteSqlRawAsync("UPDATE stock_movements SET \"Quantity\" = 100 WHERE \"ProductId\" = {0}", colaId)));
        Assert.Contains("append-only", error.ToString());

        // Чужая организация не видит товары и чеки.
        var (email, password) = await cloud.CreateOrganizationWithOwnerAsync();
        var stranger = await cloud.LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(products)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync(refundUrl, new { reason = "Чужой" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync(stock, new { kind = "Receipt", quantity = 1 })).StatusCode);
    }

    [Fact]
    public async Task Parallel_sales_never_oversell_stock()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var product = await owner.PostJsonAsync($"/api/v1/locations/{location.LocationId}/products", new { name = "Последние", priceMinorUnits = 100 });
        await owner.PostJsonAsync($"/api/v1/products/{product.Str("productId")}/stock", new { kind = "Receipt", quantity = 3 });
        await owner.PostJsonAsync($"/api/v1/locations/{location.LocationId}/cash/shifts", new { openingCashMinorUnits = 0 });

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            owner.PostAsJsonAsync($"/api/v1/locations/{location.LocationId}/sales",
                new { items = new[] { new { productId = product.Str("productId"), quantity = 1 } }, method = "Card" })));
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(0, await cloud.WithDb(db => db.Products.Where(p => p.Id == product.Str("productId")).Select(p => p.StockQuantity).SingleAsync()));
    }
}
