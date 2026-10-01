using System.Text.RegularExpressions;
using ClubOS.CloudApi.Auth;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.CloudApi.Security;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Api;

public sealed record ClientView(
    string ClientId,
    string Phone,
    string DisplayName,
    string Currency,
    long BalanceMinorUnits,
    bool IsBlocked,
    string? Note,
    DateTimeOffset CreatedAtUtc);

public sealed record ClientLedgerView(
    string EntryId,
    string Kind,
    long AmountMinorUnits,
    long BalanceAfterMinorUnits,
    string? LocationId,
    string? SessionId,
    string? Reason,
    string CreatedBy,
    string CreatedByName,
    DateTimeOffset CreatedAtUtc);

public sealed record CreateClientRequest(string? Phone, string? DisplayName, string? Note, string? LocationId);

public sealed record UpdateClientRequest(string? DisplayName, string? Note, bool? IsBlocked);

public sealed record ClientTopUpRequest(string? LocationId, long AmountMinorUnits, string? Method, string? IdempotencyKey);

public sealed record ClientAdjustmentRequest(long AmountMinorUnits, string? Reason);

/// <summary>
/// Клиенты клуба и их балансы (аванс). Пополнение — кассовая операция в открытой смене (наличные/карта):
/// деньги в кассе, но выручкой становятся при оплате сессии с баланса. Журнал баланса иммутабелен (триггер БД);
/// баланс не уходит в минус. Клиенты общие для организации (сеть клубов), операции — в локации.
/// </summary>
public static partial class ClientEndpoints
{
    public const int MaxSearchResults = 50;

    public static void MapClientEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").WithTags("Clients").RequirePermission(Permissions.CashOperate);
        api.MapGet("/clients", Search);
        api.MapPost("/clients", Create);
        api.MapGet("/clients/{clientId}", Get);
        api.MapPost("/clients/{clientId}", Update);
        api.MapPost("/clients/{clientId}/topups", TopUp);
        api.MapPost("/clients/{clientId}/adjustments", Adjust).RequirePermission(Permissions.CashRefund);
    }

    /// <summary>"+992 (90) 123-45-67" → "992901234567"; null — не телефон (7–15 цифр).</summary>
    public static string? NormalizePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !PhoneChars().IsMatch(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        return digits.Length is >= 7 and <= 15 ? digits : null;
    }

    [GeneratedRegex(@"^\+?[0-9 ()\-]+$")]
    private static partial Regex PhoneChars();

    public static ClientView ToView(this Client c) =>
        new(c.Id, c.Phone, c.DisplayName, c.Currency, c.BalanceMinorUnits, c.IsBlocked, c.Note, c.CreatedAtUtc);

    private static async Task<IResult> Search(string? query, HttpContext http, ClubOsDbContext db, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var q = db.Clients.AsNoTracking().Where(x => x.TenantId == staff.TenantId);
        var text = query?.Trim() ?? string.Empty;
        if (text.Length > 0)
        {
            var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
            var pattern = $"%{EscapeLike(text)}%";
            q = digits.Length >= 3
                ? q.Where(x => x.Phone.Contains(digits) || EF.Functions.ILike(x.DisplayName, pattern, "\\"))
                : q.Where(x => EF.Functions.ILike(x.DisplayName, pattern, "\\"));
        }

        var clients = await q.OrderBy(x => x.DisplayName).Take(MaxSearchResults).ToListAsync(ct);
        return Results.Ok(clients.Select(x => x.ToView()).ToList());
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static async Task<IResult> Create(CreateClientRequest request, HttpContext http, LocationScope scope, ClubOsDbContext db,
        AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var phone = NormalizePhone(request.Phone);
        if (phone is null)
        {
            return Problems.Validation("invalid_phone", "Телефон: 7–15 цифр с кодом страны, например +992 90 123 45 67.");
        }

        var name = request.DisplayName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 80)
        {
            return Problems.Validation("invalid_name", "Имя клиента 1–80 символов.");
        }

        var note = request.Note?.Trim();
        if (note is { Length: > 500 })
        {
            return Problems.Validation("invalid_note", "Заметка — не длиннее 500 символов.");
        }

        // Валюта баланса — валюта локации, где клиента завели.
        var location = request.LocationId is null ? null : await CashEndpoints.FindLocationAsync(db, scope, staff, request.LocationId, ct);
        if (location is null)
        {
            return Problems.NotFound("Локация");
        }

        if (await db.Clients.AnyAsync(x => x.TenantId == staff.TenantId && x.Phone == phone, ct))
        {
            return Problems.Conflict("client_exists", "Клиент с таким телефоном уже есть.");
        }

        var client = new Client
        {
            Id = Ids.New("cli"),
            TenantId = staff.TenantId,
            Phone = phone,
            DisplayName = name,
            Currency = location.Currency,
            Note = string.IsNullOrEmpty(note) ? null : note,
            CreatedBy = staff.Actor,
            CreatedAtUtc = time.GetUtcNow()
        };
        db.Clients.Add(client);
        audit.Write(staff.TenantId, location.Id, staff.Actor, "client.created", $"client:{client.Id}", AuditResults.Success,
            details: new { name, phone = MaskPhone(phone) });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Problems.Conflict("client_exists", "Клиент с таким телефоном уже есть.");
        }

        return Results.Created($"/api/v1/clients/{client.Id}", client.ToView());
    }

    /// <summary>В аудите телефон частично скрыт: журнал читают многие сотрудники.</summary>
    public static string MaskPhone(string phone) => phone.Length <= 4 ? phone : new string('•', phone.Length - 4) + phone[^4..];

    private static async Task<IResult> Get(string clientId, HttpContext http, ClubOsDbContext db, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(x => x.Id == clientId && x.TenantId == staff.TenantId, ct);
        if (client is null)
        {
            return Problems.NotFound("Клиент");
        }

        var entries = await db.ClientLedger.AsNoTracking().Where(x => x.ClientId == clientId)
            .OrderByDescending(x => x.CreatedAtUtc).Take(200).ToListAsync(ct);
        var userIds = entries.Select(e => e.CreatedBy).Where(a => a.StartsWith("user:", StringComparison.Ordinal))
            .Select(a => a["user:".Length..]).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => "user:" + x.Id, x => x.DisplayName, ct);
        return Results.Ok(new
        {
            client = client.ToView(),
            ledger = entries.Select(e => new ClientLedgerView(e.Id, e.Kind, e.AmountMinorUnits, e.BalanceAfterMinorUnits, e.LocationId,
                e.SessionId, e.Reason, e.CreatedBy, names.GetValueOrDefault(e.CreatedBy, e.CreatedBy), e.CreatedAtUtc)).ToList()
        });
    }

    private static async Task<IResult> Update(string clientId, UpdateClientRequest request, HttpContext http, ClubOsDbContext db,
        AuditWriter audit, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var client = await db.Clients.SingleOrDefaultAsync(x => x.Id == clientId && x.TenantId == staff.TenantId, ct);
        if (client is null)
        {
            return Problems.NotFound("Клиент");
        }

        var (oldName, oldNote) = (client.DisplayName, client.Note);
        if (request.DisplayName is not null)
        {
            var name = request.DisplayName.Trim();
            if (name.Length is 0 or > 80)
            {
                return Problems.Validation("invalid_name", "Имя клиента 1–80 символов.");
            }

            client.DisplayName = name;
        }

        if (request.Note is not null)
        {
            var note = request.Note.Trim();
            if (note.Length > 500)
            {
                return Problems.Validation("invalid_note", "Заметка — не длиннее 500 символов.");
            }

            client.Note = note.Length == 0 ? null : note;
        }

        if (request.IsBlocked is { } blocked && blocked != client.IsBlocked)
        {
            // Блокировка останавливает траты с баланса — решение администратора.
            if (!Permissions.Has(staff.Role, Permissions.CashRefund))
            {
                return Problems.Forbidden("Блокировать клиента может администратор.");
            }

            client.IsBlocked = blocked;
            audit.Write(staff.TenantId, null, staff.Actor, blocked ? "client.blocked" : "client.unblocked", $"client:{client.Id}",
                AuditResults.Success);
        }

        if (client.DisplayName != oldName || client.Note != oldNote)
        {
            audit.Write(staff.TenantId, null, staff.Actor, "client.updated", $"client:{client.Id}", AuditResults.Success,
                details: new { name = client.DisplayName, renamedFrom = client.DisplayName != oldName ? oldName : null });
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(client.ToView());
    }

    private static async Task<IResult> TopUp(string clientId, ClientTopUpRequest request, HttpContext http, LocationScope scope,
        ClubOsDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        if (!PaymentMethods.IsMoney(request.Method))
        {
            return Problems.Validation("invalid_method", "Пополнение — наличными (Cash) или картой (Card).");
        }

        if (CashEndpoints.ValidateAmount(request.AmountMinorUnits) is { } amountError)
        {
            return amountError;
        }

        if (CashEndpoints.ValidateKey(request.IdempotencyKey) is { } keyError)
        {
            return keyError;
        }

        var location = request.LocationId is null ? null : await CashEndpoints.FindLocationAsync(db, scope, staff, request.LocationId, ct);
        if (location is null)
        {
            return Problems.NotFound("Локация");
        }

        if (await CashEndpoints.ReplayAsync(db, staff.TenantId, request.IdempotencyKey, CashOperationKinds.BalanceTopUp, null, ct) is { } replay)
        {
            return replay;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var shift = await CashEndpoints.LockOpenShiftAsync(db, staff.TenantId, location.Id, ct);
        if (shift is null)
        {
            return CashEndpoints.ShiftNotOpen();
        }

        var (client, clientError) = await CashEndpoints.LockBalanceClientAsync(db, staff.TenantId, clientId, shift.Currency, ct);
        if (clientError is not null)
        {
            return clientError;
        }

        if (client!.BalanceMinorUnits + request.AmountMinorUnits > CashMath.MaxAmountMinorUnits)
        {
            return Problems.Validation("balance_limit", "Баланс клиента не может превышать 1 000 000.");
        }

        var operation = CashEndpoints.NewOperation(staff, shift, CashOperationKinds.BalanceTopUp, request.Method!, request.AmountMinorUnits,
            time, request.IdempotencyKey);
        operation.ClientId = client.Id;
        db.CashOperations.Add(operation);
        CashEndpoints.AppendLedger(db, client, ClientLedgerKinds.TopUp, request.AmountMinorUnits, staff, time, location.Id, null,
            operation.Id, reason: null);
        audit.Write(staff.TenantId, location.Id, staff.Actor, "client.topup", $"client:{client.Id}", AuditResults.Success,
            details: new { operationId = operation.Id, amount = request.AmountMinorUnits, request.Method, balance = client.BalanceMinorUnits });
        return await CashEndpoints.CommitAsync(db, tx, operation, staff.TenantId, request.IdempotencyKey, CashOperationKinds.BalanceTopUp,
            null, ct);
    }

    /// <summary>Ручная корректировка (бонус, исправление ошибки) — администратор, причина обязательна, в кассу не попадает.</summary>
    private static async Task<IResult> Adjust(string clientId, ClientAdjustmentRequest request, HttpContext http, ClubOsDbContext db,
        AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        if (request.AmountMinorUnits == 0 || Math.Abs(request.AmountMinorUnits) > CashMath.MaxAmountMinorUnits)
        {
            return Problems.Validation("invalid_amount", "Сумма корректировки не ноль и не больше 1 000 000 по модулю.");
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 3 or > 200)
        {
            return Problems.Validation("invalid_reason", "Укажите причину (3–200 символов).");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var client = (await db.Clients
            .FromSql($"""SELECT * FROM clients WHERE "Id" = {clientId} AND "TenantId" = {staff.TenantId} FOR UPDATE""")
            .ToListAsync(ct)).SingleOrDefault();
        if (client is null)
        {
            return Problems.NotFound("Клиент");
        }

        if (client.BalanceMinorUnits + request.AmountMinorUnits is < 0 or > CashMath.MaxAmountMinorUnits)
        {
            return Problems.Conflict("balance_out_of_range", "После корректировки баланс был бы отрицательным или слишком большим.");
        }

        var entry = CashEndpoints.AppendLedger(db, client, ClientLedgerKinds.Adjustment, request.AmountMinorUnits, staff, time, null, null,
            null, reason);
        audit.Write(staff.TenantId, null, staff.Actor, "client.adjustment", $"client:{client.Id}", AuditResults.Success,
            details: new { entryId = entry.Id, amount = request.AmountMinorUnits, reason, balance = client.BalanceMinorUnits });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(client.ToView());
    }
}
