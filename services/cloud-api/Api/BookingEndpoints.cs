using System.Globalization;
using ClubOS.CloudApi.Auth;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.CloudApi.Security;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Api;

public sealed record BookingView(
    string BookingId,
    string LocationId,
    string DeviceId,
    string DeviceName,
    string? ClientId,
    string GuestName,
    string? GuestPhone,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    string Status,
    string? Note,
    string? SessionId,
    string CreatedByName,
    DateTimeOffset CreatedAtUtc,
    string? CancelReason);

/// <summary>Ближайшая бронь ПК — для плитки и карточки устройства.</summary>
public sealed record BookingBrief(string BookingId, string GuestName, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc);

/// <param name="StartsAt">Местное время локации «ГГГГ-ММ-ДДTчч:мм».</param>
public sealed record CreateBookingRequest(
    string? DeviceId,
    string? StartsAt,
    int DurationMinutes,
    string? ClientId,
    string? GuestName,
    string? GuestPhone,
    string? Note);

public sealed record CancelBookingRequest(string? Reason);

/// <summary>
/// Бронирование ПК (D-020). Пересечения броней одного ПК запрещает ограничение БД (exclusion constraint по
/// интервалу), не код. За <see cref="HoldMinutes"/> минут до начала ПК держится: обычную сессию на нём начать
/// нельзя, только «Начать по брони». Гость, не пришедший за <see cref="GraceMinutes"/> минут, — «не пришёл»,
/// ПК освобождается. Только Cloud: Edge о бронях не знает (локальный старт на Edge бронь не проверяет).
/// </summary>
public static class BookingEndpoints
{
    public const int HoldMinutes = 15;
    public const int GraceMinutes = 15;
    public const int MinDurationMinutes = 15;
    public const int MaxDurationMinutes = 24 * 60;
    public const int MaxDaysAhead = 30;

    public static void MapBookingEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").WithTags("Bookings").RequirePermission(Permissions.SessionsManage);
        api.MapGet("/locations/{locationId}/bookings", List);
        api.MapPost("/locations/{locationId}/bookings", Create);
        api.MapPost("/bookings/{bookingId}/start", Start);
        api.MapPost("/bookings/{bookingId}/cancel", Cancel);
    }

    /// <summary>Неявка: брони, чьё льготное время истекло, освобождают ПК (до чтения, проверки и вставки).</summary>
    public static Task<int> ExpireNoShowsAsync(ClubOsDbContext db, string locationId, DateTimeOffset now, CancellationToken ct)
    {
        var deadline = now.AddMinutes(-GraceMinutes);
        return db.Bookings
            .Where(x => x.LocationId == locationId && x.Status == BookingStatuses.Booked && x.StartsAtUtc < deadline)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, BookingStatuses.NoShow)
                .SetProperty(x => x.ClosedBy, "system")
                .SetProperty(x => x.ClosedAtUtc, now), ct);
    }

    /// <summary>Бронь, которая держит ПК сейчас (начало в пределах <see cref="HoldMinutes"/> минут или уже идёт).</summary>
    public static Task<Booking?> HoldingBookingAsync(ClubOsDbContext db, string deviceId, DateTimeOffset now, CancellationToken ct)
    {
        var holdFrom = now.AddMinutes(HoldMinutes);
        return db.Bookings.AsNoTracking()
            .Where(x => x.DeviceId == deviceId && x.Status == BookingStatuses.Booked && x.StartsAtUtc <= holdFrom && x.EndsAtUtc > now)
            .OrderBy(x => x.StartsAtUtc).FirstOrDefaultAsync(ct);
    }

    /// <summary>Ближайшие брони ПК (не закончившиеся) — по одной на устройство.</summary>
    public static async Task<Dictionary<string, BookingBrief>> NextBookingsAsync(ClubOsDbContext db, IReadOnlyCollection<string> deviceIds,
        DateTimeOffset now, CancellationToken ct)
    {
        var graceStart = now.AddMinutes(-GraceMinutes);
        var rows = await db.Bookings.AsNoTracking()
            .Where(x => deviceIds.Contains(x.DeviceId) && x.Status == BookingStatuses.Booked && x.EndsAtUtc > now && x.StartsAtUtc >= graceStart)
            .OrderBy(x => x.StartsAtUtc).ToListAsync(ct);
        return rows.GroupBy(x => x.DeviceId)
            .ToDictionary(g => g.Key, g => g.Select(b => new BookingBrief(b.Id, b.GuestName, b.StartsAtUtc, b.EndsAtUtc)).First());
    }

    public static string LocalClock(DateTimeOffset utc, string timezone) =>
        CashEndpoints.FindZone(timezone) is { } zone
            ? TimeZoneInfo.ConvertTime(utc, zone).ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)
            : utc.ToString("dd.MM HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static async Task<Location?> AccessibleLocationAsync(string locationId, StaffContext staff, LocationScope scope, ClubOsDbContext db,
        CancellationToken ct)
    {
        var location = await db.Locations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == locationId && x.OrganizationId == staff.TenantId, ct);
        return location is not null && await scope.CanAccessAsync(locationId, ct) ? location : null;
    }

    private static async Task<List<BookingView>> ViewsAsync(ClubOsDbContext db, IReadOnlyList<Booking> bookings, CancellationToken ct)
    {
        var deviceIds = bookings.Select(x => x.DeviceId).Distinct().ToList();
        var devices = await db.Devices.AsNoTracking().Where(x => deviceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        var userIds = bookings.Select(x => x.CreatedBy).Where(a => a.StartsWith("user:", StringComparison.Ordinal))
            .Select(a => a["user:".Length..]).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => "user:" + x.Id, x => x.DisplayName, ct);
        return bookings.Select(b => new BookingView(b.Id, b.LocationId, b.DeviceId, devices.GetValueOrDefault(b.DeviceId, b.DeviceId), b.ClientId,
            b.GuestName, b.GuestPhone, b.StartsAtUtc, b.EndsAtUtc, b.Status, b.Note, b.SessionId,
            names.GetValueOrDefault(b.CreatedBy, b.CreatedBy), b.CreatedAtUtc, b.CancelReason)).ToList();
    }

    /// <summary>Брони местного дня локации (<c>?date=ГГГГ-ММ-ДД</c>, по умолчанию сегодня), включая отменённые.</summary>
    private static async Task<IResult> List(string locationId, string? date, HttpContext http, LocationScope scope, ClubOsDbContext db,
        TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var location = await AccessibleLocationAsync(locationId, staff, scope, db, ct);
        if (location is null)
        {
            return Problems.NotFound("Локация");
        }

        if (CashEndpoints.FindZone(location.Timezone) is not { } zone)
        {
            return Problems.Validation("invalid_timezone", $"Часовой пояс локации «{location.Timezone}» неизвестен серверу.");
        }

        var now = time.GetUtcNow();
        DateOnly day;
        if (date is null)
        {
            day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        }
        else if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
        {
            return Problems.Validation("invalid_date", "Дата в формате ГГГГ-ММ-ДД.");
        }

        await ExpireNoShowsAsync(db, locationId, now, ct);
        var from = CashEndpoints.LocalMidnightUtc(day, zone);
        var to = CashEndpoints.LocalMidnightUtc(day.AddDays(1), zone);
        var bookings = await db.Bookings.AsNoTracking()
            .Where(x => x.LocationId == locationId && x.TenantId == staff.TenantId && x.StartsAtUtc < to && x.EndsAtUtc > from)
            .OrderBy(x => x.StartsAtUtc).ToListAsync(ct);
        return Results.Ok(await ViewsAsync(db, bookings, ct));
    }

    private static async Task<IResult> Create(string locationId, CreateBookingRequest request, HttpContext http, LocationScope scope,
        ClubOsDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var location = await AccessibleLocationAsync(locationId, staff, scope, db, ct);
        if (location is null)
        {
            return Problems.NotFound("Локация");
        }

        var device = await db.Devices.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.DeviceId && x.LocationId == locationId && x.TenantId == staff.TenantId && x.RevokedAtUtc == null, ct);
        if (device is null)
        {
            return Problems.NotFound("Устройство");
        }

        if (request.DurationMinutes is < MinDurationMinutes or > MaxDurationMinutes)
        {
            return Problems.Validation("invalid_duration", $"Длительность брони от {MinDurationMinutes} минут до 24 часов.");
        }

        if (CashEndpoints.FindZone(location.Timezone) is not { } zone)
        {
            return Problems.Validation("invalid_timezone", $"Часовой пояс локации «{location.Timezone}» неизвестен серверу.");
        }

        if (request.StartsAt is null ||
            !DateTime.TryParseExact(request.StartsAt, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) ||
            zone.IsInvalidTime(local))
        {
            return Problems.Validation("invalid_start", "Начало брони — местное время локации «ГГГГ-ММ-ДДTчч:мм».");
        }

        var now = time.GetUtcNow();
        var startsAt = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
        if (startsAt < now.AddMinutes(-5))
        {
            return Problems.Validation("start_in_past", "Начало брони уже прошло.");
        }

        if (startsAt > now.AddDays(MaxDaysAhead))
        {
            return Problems.Validation("too_far", $"Бронировать можно не дальше чем на {MaxDaysAhead} дней вперёд.");
        }

        Client? client = null;
        if (request.ClientId is { } clientId)
        {
            client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(x => x.Id == clientId && x.TenantId == staff.TenantId, ct);
            if (client is null)
            {
                return Problems.NotFound("Клиент");
            }

            if (client.IsBlocked)
            {
                return Problems.Conflict("client_blocked", "Клиент заблокирован.");
            }
        }

        var name = (request.GuestName?.Trim() is { Length: > 0 } given ? given : client?.DisplayName) ?? string.Empty;
        if (name.Length is 0 or > 80)
        {
            return Problems.Validation("invalid_name", "Имя гостя 1–80 символов (или выберите клиента).");
        }

        string? phone = null;
        if (!string.IsNullOrWhiteSpace(request.GuestPhone))
        {
            phone = ClientEndpoints.NormalizePhone(request.GuestPhone);
            if (phone is null)
            {
                return Problems.Validation("invalid_phone", "Телефон: 7–15 цифр с кодом страны.");
            }
        }

        phone ??= client?.Phone;
        var note = request.Note?.Trim();
        if (note?.Length > 200)
        {
            return Problems.Validation("invalid_note", "Комментарий — не длиннее 200 символов.");
        }

        var endsAt = startsAt.AddMinutes(request.DurationMinutes);
        // Идущая сессия с лимитом, которая закончится позже начала брони, — конфликт; открытую сессию сотрудник
        // завершит сам (на плитке будет видна бронь).
        var running = await db.Sessions.AsNoTracking()
            .Where(x => x.DeviceId == device.Id && (x.State == ClubOS.Contracts.SessionState.Created || x.State == ClubOS.Contracts.SessionState.Active))
            .Select(x => x.PlannedEndAtUtc).ToListAsync(ct);
        if (running.Any(planned => planned is { } p && p > startsAt))
        {
            return Problems.Conflict("session_overlap", "На ПК идёт сессия, которая закончится позже начала брони.");
        }

        await ExpireNoShowsAsync(db, locationId, now, ct);
        var booking = new Booking
        {
            Id = Ids.New("bkg"),
            TenantId = staff.TenantId,
            LocationId = locationId,
            DeviceId = device.Id,
            ClientId = client?.Id,
            GuestName = name,
            GuestPhone = phone,
            StartsAtUtc = startsAt,
            EndsAtUtc = endsAt,
            Note = string.IsNullOrEmpty(note) ? null : note,
            CreatedBy = staff.Actor,
            CreatedAtUtc = now
        };
        db.Bookings.Add(booking);
        audit.Write(staff.TenantId, locationId, staff.Actor, "booking.created", $"device:{device.Id}", AuditResults.Success,
            details: new { bookingId = booking.Id, name, startsAt, endsAt, clientId = client?.Id });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.ExclusionViolation })
        {
            return Problems.Conflict("booking_overlap", "На это время ПК уже забронирован.");
        }

        return Results.Created($"/api/v1/bookings/{booking.Id}", (await ViewsAsync(db, [booking], ct))[0]);
    }

    /// <summary>
    /// Гость пришёл: начать сессию по брони (за <see cref="HoldMinutes"/> минут до начала и до конца льготного времени).
    /// Лимит — до конца брони (раньше начала — полная длительность). Клиент брони — клиент сессии.
    /// </summary>
    private static async Task<IResult> Start(string bookingId, HttpContext http, LocationScope scope, ClubOsDbContext db, AuditWriter audit,
        TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var booking = await db.Bookings.SingleOrDefaultAsync(x => x.Id == bookingId && x.TenantId == staff.TenantId, ct);
        if (booking is null || !await scope.CanAccessAsync(booking.LocationId, ct))
        {
            return Problems.NotFound("Бронь");
        }

        var now = time.GetUtcNow();
        if (booking.Status == BookingStatuses.Booked && now > booking.StartsAtUtc.AddMinutes(GraceMinutes))
        {
            await ExpireNoShowsAsync(db, booking.LocationId, now, ct);
            return Problems.Conflict("booking_expired", "Гость опоздал больше чем на 15 минут — бронь снята.");
        }

        if (booking.Status != BookingStatuses.Booked)
        {
            return Problems.Conflict("booking_closed", "Бронь уже начата, отменена или снята.");
        }

        if (now < booking.StartsAtUtc.AddMinutes(-HoldMinutes))
        {
            return Problems.Conflict("booking_not_due", $"Начать по брони можно не раньше чем за {HoldMinutes} минут до начала.");
        }

        var from = now > booking.StartsAtUtc ? now : booking.StartsAtUtc;
        var minutes = (int)Math.Ceiling((booking.EndsAtUtc - from).TotalMinutes);
        if (now < booking.StartsAtUtc)
        {
            minutes = (int)Math.Round((booking.EndsAtUtc - booking.StartsAtUtc).TotalMinutes);
        }

        minutes = Math.Clamp(minutes, ClubOS.Contracts.SessionLimits.MinDurationMinutes, ClubOS.Contracts.SessionLimits.MaxDurationMinutes);
        return await StaffEndpoints.StartSessionAsync(booking.DeviceId, new StartSessionRequestBody(minutes, booking.ClientId), http, scope, db,
            audit, time, ct, booking);
    }

    private static async Task<IResult> Cancel(string bookingId, CancelBookingRequest? request, HttpContext http, LocationScope scope,
        ClubOsDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var booking = await db.Bookings.SingleOrDefaultAsync(x => x.Id == bookingId && x.TenantId == staff.TenantId, ct);
        if (booking is null || !await scope.CanAccessAsync(booking.LocationId, ct))
        {
            return Problems.NotFound("Бронь");
        }

        if (booking.Status != BookingStatuses.Booked)
        {
            return Problems.Conflict("booking_closed", "Бронь уже начата, отменена или снята.");
        }

        var reason = request?.Reason?.Trim();
        if (reason?.Length > 200)
        {
            return Problems.Validation("invalid_reason", "Причина — не длиннее 200 символов.");
        }

        booking.Status = BookingStatuses.Cancelled;
        booking.ClosedBy = staff.Actor;
        booking.ClosedAtUtc = time.GetUtcNow();
        booking.CancelReason = string.IsNullOrEmpty(reason) ? null : reason;
        audit.Write(staff.TenantId, booking.LocationId, staff.Actor, "booking.cancelled", $"device:{booking.DeviceId}", AuditResults.Success,
            details: new { bookingId = booking.Id, booking.GuestName, booking.StartsAtUtc, reason = booking.CancelReason });
        await db.SaveChangesAsync(ct);
        return Results.Ok((await ViewsAsync(db, [booking], ct))[0]);
    }
}
