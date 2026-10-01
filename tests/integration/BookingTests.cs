using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.CloudApi.Domain;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>
/// Бронирование ПК: пересечения запрещены БД, удержание ПК перед бронью, старт по брони, неявка, отмена,
/// конфликт с лимитом сессии, ближайшая бронь на плитке устройства, права и изоляция.
/// </summary>
[Collection(CloudCollection.Name)]
public class BookingTests(CloudFixture cloud)
{
    private static readonly TimeSpan Dushanbe = TimeSpan.FromHours(5);

    private static async Task<string> Code(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString()!;

    /// <summary>Местное время Душанбе «ГГГГ-ММ-ДДTчч:мм» через <paramref name="from"/> от сейчас (минуты — вниз до целой).</summary>
    private static string Local(TimeSpan from) =>
        DateTimeOffset.UtcNow.Add(from).ToOffset(Dushanbe).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    private async Task<string> DeviceAsync(TestLocation location)
    {
        var id = $"dev_{Guid.NewGuid():N}"[..20];
        await cloud.WithDb(async db =>
        {
            db.Devices.Add(new Device
            {
                Id = id,
                TenantId = location.OrganizationId,
                LocationId = location.LocationId,
                ZoneId = location.ZoneId,
                DisplayName = $"PC-{id[^4..]}",
                Simulated = true,
                CertificatePem = "test",
                EnrolledAtUtc = DateTimeOffset.UtcNow.AddHours(-1)
            });
            return await db.SaveChangesAsync();
        });
        return id;
    }

    [Fact]
    public async Task Bookings_do_not_overlap_hold_the_pc_and_start_a_session()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var deviceId = await DeviceAsync(location);
        var url = $"/api/v1/locations/{location.LocationId}/bookings";

        // Валидация.
        Assert.Equal("invalid_start", await Code(await owner.PostAsJsonAsync(url, new { deviceId, startsAt = "завтра", durationMinutes = 60, guestName = "A" })));
        Assert.Equal("start_in_past", await Code(await owner.PostAsJsonAsync(url, new { deviceId, startsAt = Local(TimeSpan.FromHours(-2)), durationMinutes = 60, guestName = "A" })));
        Assert.Equal("too_far", await Code(await owner.PostAsJsonAsync(url, new { deviceId, startsAt = Local(TimeSpan.FromDays(40)), durationMinutes = 60, guestName = "A" })));
        Assert.Equal("invalid_duration", await Code(await owner.PostAsJsonAsync(url, new { deviceId, startsAt = Local(TimeSpan.FromHours(3)), durationMinutes = 5, guestName = "A" })));
        Assert.Equal("invalid_name", await Code(await owner.PostAsJsonAsync(url, new { deviceId, startsAt = Local(TimeSpan.FromHours(3)), durationMinutes = 60 })));

        // Бронь через 3 часа на 2 часа; пересечение отклоняет БД, соседняя проходит.
        var startsAt = Local(TimeSpan.FromHours(3));
        var booking = await owner.PostJsonAsync(url, new { deviceId, startsAt, durationMinutes = 120, guestName = "Далер", guestPhone = "+992 90 111 22 33", note = "VIP" });
        Assert.Equal("Booked", booking.Str("status"));
        Assert.Equal("992901112233", booking.Str("guestPhone"));
        var startUtc = booking.GetProperty("startsAtUtc").GetDateTimeOffset();
        Assert.Equal(startsAt, startUtc.ToOffset(Dushanbe).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture));
        var overlap = await owner.PostAsJsonAsync(url, new { deviceId, startsAt = Local(TimeSpan.FromHours(4)), durationMinutes = 60, guestName = "Б" });
        Assert.Equal("booking_overlap", await Code(overlap));
        var adjacent = startUtc.AddHours(2).ToOffset(Dushanbe).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
        var next = await owner.PostJsonAsync(url, new { deviceId, startsAt = adjacent, durationMinutes = 30, guestName = "Следующий" });

        // Сессия с лимитом, которая зашла бы на бронь, — отказ с подсказкой; короткая — можно.
        var sessions = $"/api/v1/devices/{deviceId}/sessions";
        var tooLong = await owner.PostAsJsonAsync(sessions, new { durationMinutes = 240 });
        Assert.Equal("booking_conflict", await Code(tooLong));
        Assert.Contains("Далер", await tooLong.Content.ReadAsStringAsync());

        // Ближайшая бронь на плитке и в карточке устройства.
        var tile = (await owner.GetJsonAsync($"/api/v1/locations/{location.LocationId}/devices")).EnumerateArray().Single(d => d.Str("deviceId") == deviceId);
        Assert.Equal(booking.Str("bookingId"), tile.GetProperty("nextBooking").Str("bookingId"));
        Assert.Equal("Далер", (await owner.GetJsonAsync($"/api/v1/devices/{deviceId}")).GetProperty("nextBooking").Str("guestName"));

        // Рано начинать по брони; отмена; повторная отмена — конфликт.
        Assert.Equal("booking_not_due", await Code(await owner.PostAsync($"/api/v1/bookings/{booking.Str("bookingId")}/start", null)));
        var cancelled = await owner.PostJsonAsync($"/api/v1/bookings/{next.Str("bookingId")}/cancel", new { reason = "Передумал" });
        Assert.Equal("Cancelled", cancelled.Str("status"));
        Assert.Equal("booking_closed", await Code(await owner.PostAsJsonAsync($"/api/v1/bookings/{next.Str("bookingId")}/cancel", new { })));

        // Список дня брони.
        var day = startUtc.ToOffset(Dushanbe).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var list = await owner.GetJsonAsync($"{url}?date={day}");
        Assert.Contains(list.EnumerateArray(), b => b.Str("bookingId") == booking.Str("bookingId"));

        // Бронь через 10 минут держит ПК: обычный старт запрещён, «Начать по брони» — можно.
        var device2 = await DeviceAsync(location);
        var client = await owner.PostJsonAsync("/api/v1/clients", new { phone = "+992 93 " + Random.Shared.Next(1_000_000, 9_999_999), displayName = "Бронь-клиент", locationId = location.LocationId });
        var soon = await owner.PostJsonAsync(url, new { deviceId = device2, startsAt = Local(TimeSpan.FromMinutes(10)), durationMinutes = 60, clientId = client.Str("clientId") });
        Assert.Equal("Бронь-клиент", soon.Str("guestName")); // имя из клиента
        var held = await owner.PostAsJsonAsync($"/api/v1/devices/{device2}/sessions", new { });
        Assert.Equal("device_booked", await Code(held));
        var started = await owner.PostJsonAsync($"/api/v1/bookings/{soon.Str("bookingId")}/start");
        Assert.Equal(client.Str("clientId"), started.Str("clientId"));
        Assert.InRange(started.GetProperty("durationMinutes").GetInt32(), 59, 60); // раньше начала — полная длительность
        var row = await cloud.WithDb(db => db.Bookings.AsNoTracking().SingleAsync(b => b.Id == soon.Str("bookingId")));
        Assert.Equal(BookingStatuses.Started, row.Status);
        Assert.Equal(started.Str("sessionId"), row.SessionId);
        Assert.Equal("booking_closed", await Code(await owner.PostAsync($"/api/v1/bookings/{soon.Str("bookingId")}/start", null)));

        // Неявка: бронь 20 минут назад снимается, ПК свободен, на это время можно бронировать снова.
        var device3 = await DeviceAsync(location);
        var late = $"bkg_{Guid.NewGuid():N}"[..20];
        await cloud.WithDb(async db =>
        {
            db.Bookings.Add(new Booking
            {
                Id = late,
                TenantId = location.OrganizationId,
                LocationId = location.LocationId,
                DeviceId = device3,
                GuestName = "Опоздавший",
                StartsAtUtc = DateTimeOffset.UtcNow.AddMinutes(-20),
                EndsAtUtc = DateTimeOffset.UtcNow.AddMinutes(40),
                CreatedBy = "user:test",
                CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1)
            });
            return await db.SaveChangesAsync();
        });
        Assert.Equal("booking_expired", await Code(await owner.PostAsync($"/api/v1/bookings/{late}/start", null)));
        Assert.Equal(BookingStatuses.NoShow, await cloud.WithDb(db => db.Bookings.Where(b => b.Id == late).Select(b => b.Status).SingleAsync()));
        await owner.PostJsonAsync(url, new { deviceId = device3, startsAt = Local(TimeSpan.FromMinutes(1)), durationMinutes = 30, guestName = "Новый" });

        var actions = await cloud.WithDb(db => db.AuditEvents.Where(a => a.LocationId == location.LocationId).Select(a => a.Action).ToListAsync());
        Assert.Contains("booking.created", actions);
        Assert.Contains("booking.cancelled", actions);
    }

    [Fact]
    public async Task Parallel_bookings_of_the_same_slot_give_exactly_one()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var deviceId = await DeviceAsync(location);
        var startsAt = Local(TimeSpan.FromHours(5));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            owner.PostAsJsonAsync($"/api/v1/locations/{location.LocationId}/bookings",
                new { deviceId, startsAt, durationMinutes = 60 + i, guestName = $"Гость {i}" })));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
    }

    [Fact]
    public async Task Other_tenant_and_location_cannot_see_or_touch_bookings()
    {
        var owner = await cloud.LoginAsync();
        var location = await cloud.CreateLocationAsync();
        var deviceId = await DeviceAsync(location);
        var booking = await owner.PostJsonAsync($"/api/v1/locations/{location.LocationId}/bookings",
            new { deviceId, startsAt = Local(TimeSpan.FromHours(6)), durationMinutes = 60, guestName = "Свой" });

        var (email, password) = await cloud.CreateOrganizationWithOwnerAsync();
        var stranger = await cloud.LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/locations/{location.LocationId}/bookings")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/api/v1/bookings/{booking.Str("bookingId")}/cancel", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/v1/bookings/{booking.Str("bookingId")}/start", null)).StatusCode);

        // Устройство из другой локации той же организации — не в этой локации.
        var other = await cloud.CreateLocationAsync();
        var foreignDevice = await DeviceAsync(other);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/v1/locations/{location.LocationId}/bookings",
            new { deviceId = foreignDevice, startsAt = Local(TimeSpan.FromHours(6)), durationMinutes = 60, guestName = "X" })).StatusCode);
    }
}
