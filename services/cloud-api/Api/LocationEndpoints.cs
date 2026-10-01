using System.Text.RegularExpressions;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.CloudApi.Security;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Api;

/// <summary>
/// Локации клубов, зоны и тарифы — только владелец (<see cref="Permissions.LocationsManage"/>).
/// Смена цены зоны увеличивает версию правила тарификации (ТЗ §12.4). Идущие сессии не затрагиваются:
/// у них снимок тарифа на старте. Edge получает новую цену при очередном обновлении конфигурации.
/// </summary>
public static partial class LocationEndpoints
{
    public const int MaxZones = 20;
    public const long MaxPricePerHourMinorUnits = 100_000_000; // 1 000 000,00 в валюте локации

    public static void MapLocationEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").WithTags("Locations").RequirePermission(Permissions.LocationsManage);
        api.MapPost("/locations", CreateLocation);
        api.MapPost("/locations/{locationId}/zones", AddZone);
        api.MapPost("/zones/{zoneId}", UpdateZone);
        api.MapPost("/zones/{zoneId}/periods", SetPeriods);
        api.MapPost("/zones/{zoneId}/packages", AddPackage);
        api.MapPost("/packages/{packageId}", UpdatePackage);
    }

    public const int MaxPackagesPerZone = 20;

    [GeneratedRegex(@"^(UTC|[A-Za-z]+(/[A-Za-z0-9_+\-]+){1,2})$")]
    private static partial Regex TimezonePattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    private static string? ValidateZone(ZoneInput zone)
    {
        var name = zone.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 40)
        {
            return "Название зоны 1–40 символов.";
        }

        return zone.PricePerHourMinorUnits is < 1 or > MaxPricePerHourMinorUnits
            ? "Цена за час должна быть больше нуля и не больше 1 000 000."
            : null;
    }

    private static async Task<IResult> CreateLocation(CreateLocationRequest request, HttpContext http, ClubOsDbContext db,
        AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var me = StaffContext.From(http.User);
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 80)
        {
            return Problems.Validation("invalid_name", "Название локации 1–80 символов.");
        }

        if (request.Timezone is null || !TimezonePattern().IsMatch(request.Timezone) ||
            CashEndpoints.FindZone(request.Timezone) is null)
        {
            return Problems.Validation("invalid_timezone", "Часовой пояс в формате IANA, например Asia/Dushanbe.");
        }

        if (request.Currency is null || !CurrencyPattern().IsMatch(request.Currency))
        {
            return Problems.Validation("invalid_currency", "Валюта — код ISO 4217 из трёх букв, например TJS.");
        }

        var zones = request.Zones ?? [];
        if (zones.Count is 0 or > MaxZones)
        {
            return Problems.Validation("invalid_zones", $"Нужна хотя бы одна зона (не больше {MaxZones}).");
        }

        if (zones.Select(ValidateZone).FirstOrDefault(e => e is not null) is { } zoneError)
        {
            return Problems.Validation("invalid_zone", zoneError);
        }

        if (zones.Select(z => z.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zones.Count)
        {
            return Problems.Validation("duplicate_zone", "Названия зон в локации не должны повторяться.");
        }

        if (await db.Locations.AnyAsync(x => x.OrganizationId == me.TenantId && x.Name == name, ct))
        {
            return Problems.Conflict("location_exists", "Локация с таким названием уже есть.");
        }

        var location = new Location
        {
            Id = Ids.New("loc"),
            OrganizationId = me.TenantId,
            Name = name,
            Timezone = request.Timezone,
            Currency = request.Currency,
            CreatedAtUtc = time.GetUtcNow()
        };
        db.Locations.Add(location);
        foreach (var zone in zones)
        {
            db.Zones.Add(new Zone
            {
                Id = Ids.New("zone"),
                LocationId = location.Id,
                Name = zone.Name.Trim(),
                PricePerHourMinorUnits = zone.PricePerHourMinorUnits
            });
        }

        audit.Write(me.TenantId, location.Id, me.Actor, "location.created", $"location:{location.Id}", AuditResults.Success,
            details: new { name, request.Timezone, request.Currency, zones = zones.Select(z => new { z.Name, z.PricePerHourMinorUnits }) });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/locations/{location.Id}", new { locationId = location.Id });
    }

    private static async Task<IResult> AddZone(string locationId, ZoneInput request, HttpContext http, ClubOsDbContext db,
        AuditWriter audit, CancellationToken ct)
    {
        var me = StaffContext.From(http.User);
        if (!await db.Locations.AnyAsync(x => x.Id == locationId && x.OrganizationId == me.TenantId, ct))
        {
            return Problems.NotFound("Локация");
        }

        if (ValidateZone(request) is { } error)
        {
            return Problems.Validation("invalid_zone", error);
        }

        var zones = await db.Zones.Where(x => x.LocationId == locationId).Select(x => x.Name).ToListAsync(ct);
        if (zones.Count >= MaxZones)
        {
            return Problems.Conflict("too_many_zones", $"В локации не больше {MaxZones} зон.");
        }

        var name = request.Name.Trim();
        if (zones.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return Problems.Conflict("duplicate_zone", "Зона с таким названием уже есть.");
        }

        var zone = new Zone { Id = Ids.New("zone"), LocationId = locationId, Name = name, PricePerHourMinorUnits = request.PricePerHourMinorUnits };
        db.Zones.Add(zone);
        audit.Write(me.TenantId, locationId, me.Actor, "zone.created", $"zone:{zone.Id}", AuditResults.Success,
            details: new { name, request.PricePerHourMinorUnits });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/zones/{zone.Id}", zone.ToView([]));
    }

    private static Task<Zone?> FindZoneAsync(ClubOsDbContext db, string tenantId, string zoneId, CancellationToken ct) =>
        (from z in db.Zones
         join l in db.Locations on z.LocationId equals l.Id
         where z.Id == zoneId && l.OrganizationId == tenantId
         select z).SingleOrDefaultAsync(ct);

    private static async Task<IResult> UpdateZone(string zoneId, ZoneInput request, HttpContext http, ClubOsDbContext db,
        AuditWriter audit, CancellationToken ct)
    {
        var me = StaffContext.From(http.User);
        var zone = await FindZoneAsync(db, me.TenantId, zoneId, ct);
        if (zone is null)
        {
            return Problems.NotFound("Зона");
        }

        if (ValidateZone(request) is { } error)
        {
            return Problems.Validation("invalid_zone", error);
        }

        var name = request.Name.Trim();
        if (await db.Zones.AnyAsync(x => x.LocationId == zone.LocationId && x.Id != zone.Id && x.Name == name, ct))
        {
            return Problems.Conflict("duplicate_zone", "Зона с таким названием уже есть.");
        }

        var oldPrice = zone.PricePerHourMinorUnits;
        zone.Name = name;
        if (oldPrice != request.PricePerHourMinorUnits)
        {
            zone.PricePerHourMinorUnits = request.PricePerHourMinorUnits;
            zone.RuleVersion++; // новая версия правила; снимки идущих сессий остаются прежними
        }

        audit.Write(me.TenantId, zone.LocationId, me.Actor, oldPrice != zone.PricePerHourMinorUnits ? "tariff.changed" : "zone.renamed",
            $"zone:{zone.Id}", AuditResults.Success,
            details: new { name, oldPrice, newPrice = zone.PricePerHourMinorUnits, zone.RuleVersion });
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ZoneViewAsync(db, zone, ct));
    }

    private static async Task<ZoneView> ZoneViewAsync(ClubOsDbContext db, Zone zone, CancellationToken ct) =>
        zone.ToView(await db.TariffPackages.AsNoTracking().Where(x => x.ZoneId == zone.Id).ToListAsync(ct));

    /// <summary>
    /// Заменить периоды цены зоны (ночь, выходные). Порядок важен: минута берёт цену первого подходящего периода.
    /// Новая версия правила; идущие сессии считаются по своему снимку.
    /// </summary>
    private static async Task<IResult> SetPeriods(string zoneId, ZonePeriodsRequest request, HttpContext http, ClubOsDbContext db,
        AuditWriter audit, CancellationToken ct)
    {
        var me = StaffContext.From(http.User);
        var zone = await FindZoneAsync(db, me.TenantId, zoneId, ct);
        if (zone is null)
        {
            return Problems.NotFound("Зона");
        }

        var periods = request.Periods ?? [];
        if (periods.Count > PricingJson.MaxPeriods)
        {
            return Problems.Validation("too_many_periods", $"Не больше {PricingJson.MaxPeriods} периодов в зоне.");
        }

        for (var i = 0; i < periods.Count; i++)
        {
            if (periods[i] is null)
            {
                return Problems.Validation("invalid_period", $"Период {i + 1}: пусто.");
            }

            if (periods[i].Validate() is { } error)
            {
                return Problems.Validation("invalid_period", $"Период {i + 1}: {error}");
            }

            if (periods[i].PricePerHourMinorUnits is < 1 or > MaxPricePerHourMinorUnits)
            {
                return Problems.Validation("invalid_period", $"Период {i + 1}: цена за час больше нуля и не больше 1 000 000.");
            }
        }

        // jsonb хранит JSON в своём виде — сравниваем разобранные периоды, не строки.
        var old = PricingJson.Read(zone.PeriodsJson);
        if (!old.SequenceEqual(periods))
        {
            zone.PeriodsJson = PricingJson.Write(periods);
            zone.RuleVersion++;
            audit.Write(me.TenantId, zone.LocationId, me.Actor, "tariff.periods_changed", $"zone:{zone.Id}", AuditResults.Success,
                details: new { zone.Name, oldPeriods = old, periods, zone.RuleVersion });
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(await ZoneViewAsync(db, zone, ct));
    }

    private static string? ValidatePackage(TariffPackageInput input)
    {
        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 40)
        {
            return "Название пакета 1–40 символов.";
        }

        if (input.DurationMinutes is < 1 or > ClubOS.Contracts.SessionLimits.MaxDurationMinutes)
        {
            return $"Длительность пакета от 1 до {ClubOS.Contracts.SessionLimits.MaxDurationMinutes} минут.";
        }

        if (input.PriceMinorUnits is < 0 or > MaxPricePerHourMinorUnits)
        {
            return "Цена пакета от 0 до 1 000 000.";
        }

        if (input.AvailableFromMinute is null != input.AvailableToMinute is null)
        {
            return "Окно начала: укажите и «с», и «до» — или ни то, ни другое.";
        }

        if (input.AvailableFromMinute is { } from && input.AvailableToMinute is { } to &&
            (from is < 0 or >= ClubOS.Contracts.PricePeriod.MinutesPerDay || to is < 1 or > ClubOS.Contracts.PricePeriod.MinutesPerDay || from == to))
        {
            return "Окно начала — время от 00:00 до 24:00, начало и конец разные.";
        }

        return null;
    }

    private static async Task<IResult> AddPackage(string zoneId, TariffPackageInput request, HttpContext http, ClubOsDbContext db,
        AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var me = StaffContext.From(http.User);
        var zone = await FindZoneAsync(db, me.TenantId, zoneId, ct);
        if (zone is null)
        {
            return Problems.NotFound("Зона");
        }

        if (ValidatePackage(request) is { } error)
        {
            return Problems.Validation("invalid_package", error);
        }

        var name = request.Name!.Trim();
        var existing = await db.TariffPackages.Where(x => x.ZoneId == zoneId).Select(x => x.Name).ToListAsync(ct);
        if (existing.Count >= MaxPackagesPerZone)
        {
            return Problems.Conflict("too_many_packages", $"В зоне не больше {MaxPackagesPerZone} пакетов.");
        }

        if (existing.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return Problems.Conflict("duplicate_package", "Пакет с таким названием в зоне уже есть.");
        }

        var package = new TariffPackage
        {
            Id = Ids.New("pkg"),
            TenantId = me.TenantId,
            LocationId = zone.LocationId,
            ZoneId = zone.Id,
            Name = name,
            DurationMinutes = request.DurationMinutes,
            PriceMinorUnits = request.PriceMinorUnits,
            AvailableFromMinute = request.AvailableFromMinute,
            AvailableToMinute = request.AvailableToMinute,
            IsActive = request.IsActive ?? true,
            CreatedAtUtc = time.GetUtcNow()
        };
        db.TariffPackages.Add(package);
        audit.Write(me.TenantId, zone.LocationId, me.Actor, "package.created", $"zone:{zone.Id}", AuditResults.Success,
            details: new { packageId = package.Id, name, request.DurationMinutes, request.PriceMinorUnits, request.AvailableFromMinute, request.AvailableToMinute });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/packages/{package.Id}", package.ToView());
    }

    /// <summary>Изменить или отключить пакет. Начатые по нему сессии считаются по своему снимку.</summary>
    private static async Task<IResult> UpdatePackage(string packageId, TariffPackageInput request, HttpContext http, ClubOsDbContext db,
        AuditWriter audit, CancellationToken ct)
    {
        var me = StaffContext.From(http.User);
        var package = await db.TariffPackages.SingleOrDefaultAsync(x => x.Id == packageId && x.TenantId == me.TenantId, ct);
        if (package is null)
        {
            return Problems.NotFound("Пакет");
        }

        if (ValidatePackage(request) is { } error)
        {
            return Problems.Validation("invalid_package", error);
        }

        var name = request.Name!.Trim();
        if (await db.TariffPackages.AnyAsync(x => x.ZoneId == package.ZoneId && x.Id != package.Id && x.Name == name, ct))
        {
            return Problems.Conflict("duplicate_package", "Пакет с таким названием в зоне уже есть.");
        }

        var before = package.ToView();
        package.Name = name;
        package.DurationMinutes = request.DurationMinutes;
        package.PriceMinorUnits = request.PriceMinorUnits;
        package.AvailableFromMinute = request.AvailableFromMinute;
        package.AvailableToMinute = request.AvailableToMinute;
        package.IsActive = request.IsActive ?? package.IsActive;
        audit.Write(me.TenantId, package.LocationId, me.Actor, "package.updated", $"zone:{package.ZoneId}", AuditResults.Success,
            details: new { packageId = package.Id, before, after = package.ToView() });
        await db.SaveChangesAsync(ct);
        return Results.Ok(package.ToView());
    }
}
