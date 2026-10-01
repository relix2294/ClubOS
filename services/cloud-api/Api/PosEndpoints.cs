using ClubOS.CloudApi.Auth;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.CloudApi.Security;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Api;

public sealed record ProductView(
    string ProductId,
    string LocationId,
    string Name,
    string? Category,
    long PriceMinorUnits,
    bool TrackStock,
    int StockQuantity,
    bool IsActive);

public sealed record ProductInput(string? Name, string? Category, long PriceMinorUnits, bool? TrackStock, bool? IsActive);

/// <param name="Kind">Receipt (приход), WriteOff (списание), Count (инвентаризация: Quantity — пересчитанный остаток).</param>
public sealed record StockRequest(string? Kind, int Quantity, string? Reason);

public sealed record StockMovementView(
    string MovementId,
    string Kind,
    int Quantity,
    int QuantityAfter,
    string? Reason,
    string? SaleId,
    string CreatedByName,
    DateTimeOffset CreatedAtUtc);

public sealed record SaleItemInput(string? ProductId, int Quantity);

public sealed record SaleRequest(IReadOnlyList<SaleItemInput>? Items, string? Method, string? ClientId, string? IdempotencyKey);

public sealed record SaleItemView(string ProductId, string Name, long PriceMinorUnits, int Quantity, long TotalMinorUnits);

public sealed record SaleView(
    string SaleId,
    string LocationId,
    string ShiftId,
    string Method,
    string? ClientId,
    string? ClientName,
    long TotalMinorUnits,
    string Currency,
    string Status,
    IReadOnlyList<SaleItemView> Items,
    string CreatedByName,
    DateTimeOffset CreatedAtUtc,
    string? RefundReason,
    DateTimeOffset? RefundedAtUtc);

public sealed record SaleRefundRequest(string? Reason, string? IdempotencyKey);

/// <summary>
/// Бар / POS (D-021). Каталог и склад ведёт администратор (<see cref="Permissions.CashRefund"/>), продаёт кассир
/// (<see cref="Permissions.CashOperate"/>) в открытой смене: чек — одна кассовая операция ProductSale и позиции
/// с ценой на момент продажи; товары с учётом остатка списываются под блокировкой строк (смена → товары по Id →
/// клиент). Возврат чека — целиком, отдельной операцией ProductRefund с возвратом на склад.
/// Журнал склада иммутабелен (триггер БД).
/// </summary>
public static class PosEndpoints
{
    public const int MaxItemsPerSale = 50;
    public const int MaxQuantity = 999;
    public const int MaxStock = 1_000_000;

    public static void MapPosEndpoints(this IEndpointRouteBuilder app)
    {
        var sell = app.MapGroup("/api/v1").WithTags("POS").RequirePermission(Permissions.CashOperate);
        sell.MapGet("/locations/{locationId}/products", ListProducts);
        sell.MapPost("/locations/{locationId}/sales", CreateSale);
        sell.MapGet("/locations/{locationId}/sales", ListSales);

        var manage = app.MapGroup("/api/v1").WithTags("POS").RequirePermission(Permissions.CashRefund);
        manage.MapPost("/locations/{locationId}/products", CreateProduct);
        manage.MapPost("/products/{productId}", UpdateProduct);
        manage.MapPost("/products/{productId}/stock", ChangeStock);
        manage.MapGet("/products/{productId}/movements", ListMovements);
        manage.MapPost("/sales/{saleId}/refund", RefundSale);
    }

    private static ProductView ToView(this Product p) =>
        new(p.Id, p.LocationId, p.Name, p.Category, p.PriceMinorUnits, p.TrackStock, p.StockQuantity, p.IsActive);

    private static async Task<List<SaleView>> SaleViewsAsync(ClubOsDbContext db, IReadOnlyList<Sale> sales, CancellationToken ct)
    {
        var ids = sales.Select(x => x.Id).ToList();
        var items = (await db.SaleItems.AsNoTracking().Where(x => ids.Contains(x.SaleId)).ToListAsync(ct)).ToLookup(x => x.SaleId);
        var clientIds = sales.Select(x => x.ClientId).OfType<string>().Distinct().ToList();
        var clients = await db.Clients.AsNoTracking().Where(x => clientIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        var userIds = sales.Select(x => x.CreatedBy).Where(a => a.StartsWith("user:", StringComparison.Ordinal))
            .Select(a => a["user:".Length..]).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => "user:" + x.Id, x => x.DisplayName, ct);
        return sales.Select(s => new SaleView(s.Id, s.LocationId, s.ShiftId, s.Method, s.ClientId,
            s.ClientId is null ? null : clients.GetValueOrDefault(s.ClientId), s.TotalMinorUnits, s.Currency, s.Status,
            items[s.Id].OrderBy(i => i.Name).Select(i => new SaleItemView(i.ProductId, i.Name, i.PriceMinorUnits, i.Quantity, i.TotalMinorUnits)).ToList(),
            names.GetValueOrDefault(s.CreatedBy, s.CreatedBy), s.CreatedAtUtc, s.RefundReason, s.RefundedAtUtc)).ToList();
    }

    private static string? ValidateProduct(ProductInput input)
    {
        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 60)
        {
            return "Название товара 1–60 символов.";
        }

        if (input.Category?.Trim().Length > 40)
        {
            return "Категория — не длиннее 40 символов.";
        }

        return input.PriceMinorUnits is < 0 or > CashMath.MaxAmountMinorUnits ? "Цена от 0 до 1 000 000." : null;
    }

    private static async Task<IResult> ListProducts(string locationId, bool? all, HttpContext http, LocationScope scope, ClubOsDbContext db,
        CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        if (await CashEndpoints.FindLocationAsync(db, scope, staff, locationId, ct) is null)
        {
            return Problems.NotFound("Локация");
        }

        var includeInactive = all == true && Permissions.Has(staff.Role, Permissions.CashRefund);
        var products = await db.Products.AsNoTracking()
            .Where(x => x.LocationId == locationId && x.TenantId == staff.TenantId && (includeInactive || x.IsActive))
            .OrderBy(x => x.Category).ThenBy(x => x.Name).ToListAsync(ct);
        return Results.Ok(products.Select(p => p.ToView()).ToList());
    }

    private static async Task<IResult> CreateProduct(string locationId, ProductInput request, HttpContext http, LocationScope scope,
        ClubOsDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        if (await CashEndpoints.FindLocationAsync(db, scope, staff, locationId, ct) is null)
        {
            return Problems.NotFound("Локация");
        }

        if (ValidateProduct(request) is { } error)
        {
            return Problems.Validation("invalid_product", error);
        }

        var product = new Product
        {
            Id = Ids.New("prd"),
            TenantId = staff.TenantId,
            LocationId = locationId,
            Name = request.Name!.Trim(),
            Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim(),
            PriceMinorUnits = request.PriceMinorUnits,
            TrackStock = request.TrackStock ?? true,
            IsActive = request.IsActive ?? true,
            CreatedAtUtc = time.GetUtcNow()
        };
        db.Products.Add(product);
        audit.Write(staff.TenantId, locationId, staff.Actor, "product.created", $"product:{product.Id}", AuditResults.Success,
            details: new { product.Name, product.Category, product.PriceMinorUnits, product.TrackStock });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (CashEndpoints.IsUniqueViolation(ex))
        {
            return Problems.Conflict("duplicate_product", "Товар с таким названием уже есть.");
        }

        return Results.Created($"/api/v1/products/{product.Id}", product.ToView());
    }

    private static async Task<Product?> FindProductAsync(ClubOsDbContext db, LocationScope scope, StaffContext staff, string productId,
        CancellationToken ct)
    {
        var product = await db.Products.SingleOrDefaultAsync(x => x.Id == productId && x.TenantId == staff.TenantId, ct);
        return product is not null && await scope.CanAccessAsync(product.LocationId, ct) ? product : null;
    }

    private static async Task<IResult> UpdateProduct(string productId, ProductInput request, HttpContext http, LocationScope scope,
        ClubOsDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var product = await FindProductAsync(db, scope, staff, productId, ct);
        if (product is null)
        {
            return Problems.NotFound("Товар");
        }

        if (ValidateProduct(request) is { } error)
        {
            return Problems.Validation("invalid_product", error);
        }

        var before = product.ToView();
        product.Name = request.Name!.Trim();
        product.Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim();
        product.PriceMinorUnits = request.PriceMinorUnits;
        product.TrackStock = request.TrackStock ?? product.TrackStock;
        product.IsActive = request.IsActive ?? product.IsActive;
        audit.Write(staff.TenantId, product.LocationId, staff.Actor, "product.updated", $"product:{product.Id}", AuditResults.Success,
            details: new { before, after = product.ToView() });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (CashEndpoints.IsUniqueViolation(ex))
        {
            return Problems.Conflict("duplicate_product", "Товар с таким названием уже есть.");
        }

        return Results.Ok(product.ToView());
    }

    private static StockMovement NewMovement(Product product, string kind, int quantity, string? reason, string? saleId, StaffContext staff,
        DateTimeOffset now)
    {
        product.StockQuantity += quantity;
        return new StockMovement
        {
            Id = Ids.New("stm"),
            TenantId = product.TenantId,
            LocationId = product.LocationId,
            ProductId = product.Id,
            Kind = kind,
            Quantity = quantity,
            QuantityAfter = product.StockQuantity,
            Reason = reason,
            SaleId = saleId,
            CreatedBy = staff.Actor,
            CreatedAtUtc = now
        };
    }

    private static async Task<List<Product>> LockProductsAsync(ClubOsDbContext db, string tenantId, IReadOnlyCollection<string> ids,
        CancellationToken ct)
    {
        // Порядок блокировки по Id — параллельные чеки с общими товарами не взаимоблокируются.
        var sorted = ids.Distinct().Order(StringComparer.Ordinal).ToArray();
        return await db.Products
            .FromSql($"""SELECT * FROM products WHERE "TenantId" = {tenantId} AND "Id" = ANY({sorted}) ORDER BY "Id" FOR UPDATE""")
            .ToListAsync(ct);
    }

    /// <summary>Приход, списание или инвентаризация. Остаток не уходит в минус.</summary>
    private static async Task<IResult> ChangeStock(string productId, StockRequest request, HttpContext http, LocationScope scope,
        ClubOsDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        if (request.Kind is not (StockMovementKinds.Receipt or StockMovementKinds.WriteOff or StockMovementKinds.Count))
        {
            return Problems.Validation("invalid_kind", "Операция склада — Receipt, WriteOff или Count.");
        }

        if (request.Quantity is < 0 or > MaxStock || (request.Kind != StockMovementKinds.Count && request.Quantity == 0))
        {
            return Problems.Validation("invalid_quantity", "Количество от 1 до 1 000 000 (инвентаризация — от 0).");
        }

        var reason = request.Reason?.Trim();
        if (request.Kind != StockMovementKinds.Receipt && (reason is null || reason.Length < 3) || reason?.Length > 200)
        {
            return Problems.Validation("invalid_reason", "Укажите основание (3–200 символов).");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var product = (await LockProductsAsync(db, staff.TenantId, [productId], ct)).SingleOrDefault();
        if (product is null || !await scope.CanAccessAsync(product.LocationId, ct))
        {
            return Problems.NotFound("Товар");
        }

        if (!product.TrackStock)
        {
            return Problems.Conflict("stock_not_tracked", "Для этого товара остаток не ведётся.");
        }

        var delta = request.Kind switch
        {
            StockMovementKinds.Receipt => request.Quantity,
            StockMovementKinds.WriteOff => -request.Quantity,
            _ => request.Quantity - product.StockQuantity
        };
        if (product.StockQuantity + delta is < 0 or > MaxStock)
        {
            return Problems.Conflict("insufficient_stock", $"На складе {product.StockQuantity} шт. — списать {request.Quantity} нельзя.");
        }

        if (delta == 0)
        {
            return Results.Ok(product.ToView());
        }

        var movement = NewMovement(product, request.Kind, delta, string.IsNullOrEmpty(reason) ? null : reason, null, staff, time.GetUtcNow());
        db.StockMovements.Add(movement);
        audit.Write(staff.TenantId, product.LocationId, staff.Actor, $"stock.{request.Kind.ToLowerInvariant()}", $"product:{product.Id}",
            AuditResults.Success, details: new { product.Name, quantity = delta, after = product.StockQuantity, reason });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(product.ToView());
    }

    private static async Task<IResult> ListMovements(string productId, HttpContext http, LocationScope scope, ClubOsDbContext db,
        CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var product = await FindProductAsync(db, scope, staff, productId, ct);
        if (product is null)
        {
            return Problems.NotFound("Товар");
        }

        var movements = await db.StockMovements.AsNoTracking().Where(x => x.ProductId == productId)
            .OrderByDescending(x => x.CreatedAtUtc).Take(200).ToListAsync(ct);
        var userIds = movements.Select(x => x.CreatedBy).Where(a => a.StartsWith("user:", StringComparison.Ordinal))
            .Select(a => a["user:".Length..]).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => "user:" + x.Id, x => x.DisplayName, ct);
        return Results.Ok(movements.Select(m => new StockMovementView(m.Id, m.Kind, m.Quantity, m.QuantityAfter, m.Reason, m.SaleId,
            names.GetValueOrDefault(m.CreatedBy, m.CreatedBy), m.CreatedAtUtc)).ToList());
    }

    /// <summary>Повтор чека с тем же ключом — тот же чек (200); ключ от другой операции — 409.</summary>
    private static async Task<IResult?> ReplaySaleAsync(ClubOsDbContext db, string tenantId, string? key, string kind, CancellationToken ct)
    {
        if (key is null)
        {
            return null;
        }

        var operation = await db.CashOperations.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == key, ct);
        if (operation is null)
        {
            return null;
        }

        if (operation.Kind != kind || operation.SaleId is null)
        {
            return Problems.Conflict("idempotency_key_reused", "Ключ идемпотентности уже использован для другой операции.");
        }

        var sale = await db.Sales.AsNoTracking().SingleAsync(x => x.Id == operation.SaleId, ct);
        return Results.Ok((await SaleViewsAsync(db, [sale], ct))[0]);
    }

    private static async Task<IResult> CreateSale(string locationId, SaleRequest request, HttpContext http, LocationScope scope,
        ClubOsDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var items = request.Items ?? [];
        if (items.Count is 0 or > MaxItemsPerSale)
        {
            return Problems.Validation("invalid_items", $"В чеке от 1 до {MaxItemsPerSale} позиций.");
        }

        if (items.Any(i => string.IsNullOrEmpty(i.ProductId) || i.Quantity is < 1 or > MaxQuantity))
        {
            return Problems.Validation("invalid_items", $"Количество каждой позиции от 1 до {MaxQuantity}.");
        }

        if (!PaymentMethods.IsValid(request.Method))
        {
            return Problems.Validation("invalid_method", "Способ оплаты — Cash, Card или Balance.");
        }

        if (CashEndpoints.ValidateKey(request.IdempotencyKey) is { } keyError)
        {
            return keyError;
        }

        if (await CashEndpoints.FindLocationAsync(db, scope, staff, locationId, ct) is null)
        {
            return Problems.NotFound("Локация");
        }

        if (await ReplaySaleAsync(db, staff.TenantId, request.IdempotencyKey, CashOperationKinds.ProductSale, ct) is { } replay)
        {
            return replay;
        }

        // Одинаковые товары в нескольких строках — одна позиция.
        var lines = items.GroupBy(i => i.ProductId!).Select(g => (ProductId: g.Key, Quantity: g.Sum(i => i.Quantity))).ToList();
        if (lines.Any(l => l.Quantity > MaxQuantity))
        {
            return Problems.Validation("invalid_items", $"Количество каждой позиции не больше {MaxQuantity}.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var shift = await CashEndpoints.LockOpenShiftAsync(db, staff.TenantId, locationId, ct);
        if (shift is null)
        {
            return CashEndpoints.ShiftNotOpen();
        }

        var products = (await LockProductsAsync(db, staff.TenantId, lines.Select(l => l.ProductId).ToList(), ct)).ToDictionary(p => p.Id);
        foreach (var line in lines)
        {
            if (!products.TryGetValue(line.ProductId, out var product) || product.LocationId != locationId || !product.IsActive)
            {
                return Problems.Conflict("product_unavailable", "Товар не найден или снят с продажи — обновите витрину.");
            }

            if (product.TrackStock && product.StockQuantity < line.Quantity)
            {
                return Problems.Conflict("insufficient_stock", $"«{product.Name}»: на складе {product.StockQuantity} шт.");
            }
        }

        var total = lines.Sum(l => products[l.ProductId].PriceMinorUnits * l.Quantity);
        if (total is < 1 or > CashMath.MaxAmountMinorUnits)
        {
            return Problems.Validation("invalid_amount", "Сумма чека должна быть больше нуля и не больше 1 000 000.");
        }

        Client? client = null;
        if (request.Method == PaymentMethods.Balance)
        {
            (client, var clientError) = await CashEndpoints.LockBalanceClientAsync(db, staff.TenantId, request.ClientId, shift.Currency, ct);
            if (clientError is not null)
            {
                return clientError;
            }

            if (client!.BalanceMinorUnits < total)
            {
                return Problems.Conflict("insufficient_balance",
                    $"На балансе {client.BalanceMinorUnits / 100m:0.00} {client.Currency} — меньше суммы чека.");
            }
        }

        var now = time.GetUtcNow();
        var operation = CashEndpoints.NewOperation(staff, shift, CashOperationKinds.ProductSale, request.Method!, total, time, request.IdempotencyKey);
        var sale = new Sale
        {
            Id = Ids.New("sal"),
            TenantId = staff.TenantId,
            LocationId = locationId,
            ShiftId = shift.Id,
            CashOperationId = operation.Id,
            Method = request.Method!,
            ClientId = client?.Id,
            TotalMinorUnits = total,
            Currency = shift.Currency,
            CreatedBy = staff.Actor,
            CreatedAtUtc = now
        };
        operation.SaleId = sale.Id;
        operation.ClientId = client?.Id;
        operation.Reason = string.Join(", ", lines.Select(l => $"{products[l.ProductId].Name} × {l.Quantity}"));
        if (operation.Reason.Length > 200)
        {
            operation.Reason = operation.Reason[..197] + "…";
        }

        db.Sales.Add(sale);
        db.CashOperations.Add(operation);
        foreach (var line in lines)
        {
            var product = products[line.ProductId];
            db.SaleItems.Add(new SaleItem
            {
                Id = Ids.New("sli"),
                SaleId = sale.Id,
                ProductId = product.Id,
                Name = product.Name,
                PriceMinorUnits = product.PriceMinorUnits,
                Quantity = line.Quantity,
                TotalMinorUnits = product.PriceMinorUnits * line.Quantity
            });
            if (product.TrackStock)
            {
                db.StockMovements.Add(NewMovement(product, StockMovementKinds.Sale, -line.Quantity, null, sale.Id, staff, now));
            }
        }

        if (client is not null)
        {
            CashEndpoints.AppendLedger(db, client, ClientLedgerKinds.ProductPayment, -total, staff, time, locationId, null, operation.Id,
                reason: null);
        }

        audit.Write(staff.TenantId, locationId, staff.Actor, "pos.sale", $"sale:{sale.Id}", AuditResults.Success,
            details: new { total, request.Method, clientId = client?.Id, items = lines.Select(l => new { l.ProductId, l.Quantity }) });
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (CashEndpoints.IsUniqueViolation(ex) && request.IdempotencyKey is not null)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return await ReplaySaleAsync(db, staff.TenantId, request.IdempotencyKey, CashOperationKinds.ProductSale, ct)
                   ?? Problems.Conflict("idempotency_key_reused", "Ключ идемпотентности уже использован.");
        }

        return Results.Created($"/api/v1/sales/{sale.Id}", (await SaleViewsAsync(db, [sale], ct))[0]);
    }

    /// <summary>Чеки смены: по умолчанию открытой (<c>?shiftId=</c> — другой), свежие сверху.</summary>
    private static async Task<IResult> ListSales(string locationId, string? shiftId, HttpContext http, LocationScope scope, ClubOsDbContext db,
        CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        if (await CashEndpoints.FindLocationAsync(db, scope, staff, locationId, ct) is null)
        {
            return Problems.NotFound("Локация");
        }

        shiftId ??= await db.CashShifts.AsNoTracking()
            .Where(x => x.LocationId == locationId && x.TenantId == staff.TenantId && x.ClosedAtUtc == null)
            .Select(x => x.Id).SingleOrDefaultAsync(ct);
        if (shiftId is null)
        {
            return Results.Ok(Array.Empty<SaleView>());
        }

        var sales = await db.Sales.AsNoTracking()
            .Where(x => x.ShiftId == shiftId && x.LocationId == locationId && x.TenantId == staff.TenantId)
            .OrderByDescending(x => x.CreatedAtUtc).Take(200).ToListAsync(ct);
        return Results.Ok(await SaleViewsAsync(db, sales, ct));
    }

    /// <summary>Возврат чека целиком в открытой смене: деньги тем же способом, товары с учётом остатка — на склад.</summary>
    private static async Task<IResult> RefundSale(string saleId, SaleRefundRequest request, HttpContext http, LocationScope scope,
        ClubOsDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 3 or > 200)
        {
            return Problems.Validation("invalid_reason", "Укажите причину возврата (3–200 символов).");
        }

        if (CashEndpoints.ValidateKey(request.IdempotencyKey) is { } keyError)
        {
            return keyError;
        }

        var found = await db.Sales.AsNoTracking().SingleOrDefaultAsync(x => x.Id == saleId && x.TenantId == staff.TenantId, ct);
        if (found is null || !await scope.CanAccessAsync(found.LocationId, ct))
        {
            return Problems.NotFound("Чек");
        }

        if (await ReplaySaleAsync(db, staff.TenantId, request.IdempotencyKey, CashOperationKinds.ProductRefund, ct) is { } replay)
        {
            return replay;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var shift = await CashEndpoints.LockOpenShiftAsync(db, staff.TenantId, found.LocationId, ct);
        if (shift is null)
        {
            return CashEndpoints.ShiftNotOpen();
        }

        var sale = (await db.Sales.FromSql($"""SELECT * FROM sales WHERE "Id" = {saleId} FOR UPDATE""").ToListAsync(ct)).Single();
        if (sale.Status != SaleStatuses.Paid)
        {
            return Problems.Conflict("sale_refunded", "Чек уже возвращён.");
        }

        if (sale.Method == PaymentMethods.Cash && await CashEndpoints.CashOnHandAsync(db, shift, ct) < sale.TotalMinorUnits)
        {
            return Problems.Conflict("insufficient_cash", "В кассе меньше наличных, чем сумма возврата.");
        }

        var lines = await db.SaleItems.AsNoTracking().Where(x => x.SaleId == sale.Id).ToListAsync(ct);
        var products = (await LockProductsAsync(db, staff.TenantId, lines.Select(l => l.ProductId).ToList(), ct)).ToDictionary(p => p.Id);
        Client? client = null;
        if (sale.Method == PaymentMethods.Balance)
        {
            (client, var clientError) = await CashEndpoints.LockBalanceClientAsync(db, staff.TenantId, sale.ClientId, shift.Currency, ct);
            if (clientError is not null)
            {
                return clientError;
            }
        }

        var now = time.GetUtcNow();
        var operation = CashEndpoints.NewOperation(staff, shift, CashOperationKinds.ProductRefund, sale.Method, -sale.TotalMinorUnits, time,
            request.IdempotencyKey);
        operation.SaleId = sale.Id;
        operation.ClientId = sale.ClientId;
        operation.Reason = reason;
        db.CashOperations.Add(operation);
        foreach (var line in lines)
        {
            if (products.TryGetValue(line.ProductId, out var product) && product.TrackStock)
            {
                db.StockMovements.Add(NewMovement(product, StockMovementKinds.Return, line.Quantity, reason, sale.Id, staff, now));
            }
        }

        if (client is not null)
        {
            CashEndpoints.AppendLedger(db, client, ClientLedgerKinds.ProductRefund, sale.TotalMinorUnits, staff, time, sale.LocationId, null,
                operation.Id, reason);
        }

        sale.Status = SaleStatuses.Refunded;
        sale.RefundOperationId = operation.Id;
        sale.RefundedBy = staff.Actor;
        sale.RefundedAtUtc = now;
        sale.RefundReason = reason;
        audit.Write(staff.TenantId, sale.LocationId, staff.Actor, "pos.refund", $"sale:{sale.Id}", AuditResults.Success,
            details: new { total = sale.TotalMinorUnits, sale.Method, reason });
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (CashEndpoints.IsUniqueViolation(ex) && request.IdempotencyKey is not null)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return await ReplaySaleAsync(db, staff.TenantId, request.IdempotencyKey, CashOperationKinds.ProductRefund, ct)
                   ?? Problems.Conflict("idempotency_key_reused", "Ключ идемпотентности уже использован.");
        }

        return Results.Ok((await SaleViewsAsync(db, [sale], ct))[0]);
    }
}
