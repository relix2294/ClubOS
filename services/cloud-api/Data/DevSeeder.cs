using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.CloudApi.Security;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Data;

public sealed class SeedOptions
{
    public const string Section = "Seed";

    public bool Enabled { get; set; }
    public string OwnerEmail { get; set; } = "owner@demo.clubos.local";

    /// <summary>Отображаемые имена при первом запуске (ID постоянные). Меняются только до первого seed.</summary>
    public string OrganizationName { get; set; } = "Demo Club Group";
    public string LocationName { get; set; } = "Dushanbe Pilot";
    public string OwnerDisplayName { get; set; } = "Dev Owner";

    /// <summary>Пароль dev Owner — только из env (CLUBOS_Seed__OwnerPassword), не из git.</summary>
    public string? OwnerPassword { get; set; }

    /// <summary>
    /// Только dev (docker compose): одноразовый Edge enrollment-токен, заранее известный Edge-контейнеру.
    /// Создаётся, если в локации ещё нет Edge. В пилоте токен выдаётся через Admin Web.
    /// </summary>
    public string? EdgeEnrollmentToken { get; set; }

    /// <summary>
    /// VPS, профиль demo: секрет (от 24 символов) для отдельной локации «демо-зал» с Edge и симулированными ПК.
    /// Токены — <see cref="ClubOS.Contracts.DemoEnrollment"/>. Пусто — демо-зал не создаётся.
    /// </summary>
    public string? DemoSecret { get; set; }

    public string DemoLocationName { get; set; } = "Демо-зал (симулятор)";
}

/// <summary>
/// Dev bootstrap (ТЗ §25.2.1): Demo Club Group / Dushanbe Pilot / Asia/Dushanbe / TJS /
/// зоны Standard и VIP по 120 TJS/час / dev Owner. Идемпотентен: повторный запуск ничего не дублирует.
/// </summary>
public static class DevSeeder
{
    public const string OrganizationId = "org_demo";
    public const string LocationId = "loc_dushanbe_pilot";
    public const string StandardZoneId = "zone_standard";
    public const string VipZoneId = "zone_vip";
    public const long PricePerHourMinorUnits = 12_000; // 120,00 TJS/час

    public const string DemoLocationId = "loc_demo_hall";
    public const string DemoStandardZoneId = "zone_demo_standard";
    public const string DemoVipZoneId = "zone_demo_vip";
    public const long DemoVipPricePerHourMinorUnits = 18_000; // 180,00 TJS/час

    public static async Task SeedAsync(ClubOsDbContext db, SeedOptions options, TimeProvider time, ILogger logger,
        CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        if (!await db.Organizations.AnyAsync(x => x.Id == OrganizationId, ct))
        {
            db.Organizations.Add(new Organization { Id = OrganizationId, Name = options.OrganizationName, CreatedAtUtc = now });
            db.Locations.Add(new Location
            {
                Id = LocationId,
                OrganizationId = OrganizationId,
                Name = options.LocationName,
                Timezone = "Asia/Dushanbe",
                Currency = "TJS",
                CreatedAtUtc = now
            });
            db.Zones.Add(new Zone
            {
                Id = StandardZoneId,
                LocationId = LocationId,
                Name = "Standard",
                PricePerHourMinorUnits = PricePerHourMinorUnits
            });
            db.Zones.Add(new Zone
            {
                Id = VipZoneId,
                LocationId = LocationId,
                Name = "VIP",
                PricePerHourMinorUnits = PricePerHourMinorUnits
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seed: создана организация Demo Club Group / Dushanbe Pilot");
        }

        await SeedEdgeTokenAsync(db, options, now, logger, ct);
        await SeedDemoHallAsync(db, options, now, logger, ct);

        var email = options.OwnerEmail.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(x => x.Email == email, ct))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.OwnerPassword) || options.OwnerPassword.Length < 10)
        {
            logger.LogWarning("Seed: dev Owner не создан — задайте CLUBOS_Seed__OwnerPassword (не короче 10 символов).");
            return;
        }

        db.Users.Add(new User
        {
            Id = "usr_demo_owner",
            OrganizationId = OrganizationId,
            Email = email,
            DisplayName = options.OwnerDisplayName,
            PasswordHash = PasswordHasher.Hash(options.OwnerPassword),
            Role = Roles.Owner,
            CreatedAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seed: создан dev Owner {Email}", email);
    }

    /// <summary>
    /// Демо-зал: отдельная локация (Standard 120 и VIP 180 TJS/час), токен её Edge и токены 5 симулированных ПК.
    /// Отдельная локация нужна потому, что на локацию один Edge: основной занимает настоящий Edge клуба.
    /// Токены одноразовые; уже использованные или существующие не пересоздаются.
    /// </summary>
    private static async Task SeedDemoHallAsync(ClubOsDbContext db, SeedOptions options, DateTimeOffset now, ILogger logger,
        CancellationToken ct)
    {
        var secret = options.DemoSecret?.Trim();
        if (string.IsNullOrEmpty(secret) || secret.Length < ClubOS.Contracts.DemoEnrollment.MinSecretLength)
        {
            return;
        }

        if (!await db.Locations.AnyAsync(x => x.Id == DemoLocationId, ct))
        {
            db.Locations.Add(new Location
            {
                Id = DemoLocationId,
                OrganizationId = OrganizationId,
                Name = options.DemoLocationName,
                Timezone = "Asia/Dushanbe",
                Currency = "TJS",
                CreatedAtUtc = now
            });
            db.Zones.Add(new Zone
            {
                Id = DemoStandardZoneId,
                LocationId = DemoLocationId,
                Name = "Standard",
                PricePerHourMinorUnits = PricePerHourMinorUnits
            });
            db.Zones.Add(new Zone
            {
                Id = DemoVipZoneId,
                LocationId = DemoLocationId,
                Name = "VIP",
                PricePerHourMinorUnits = DemoVipPricePerHourMinorUnits
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seed: создана локация {Name}", options.DemoLocationName);
        }

        var tokens = new List<(string Token, string Kind, string Name, string? ZoneId)>();
        if (!await db.Edges.AnyAsync(x => x.LocationId == DemoLocationId, ct))
        {
            tokens.Add((ClubOS.Contracts.DemoEnrollment.EdgeToken(secret), EnrollmentKinds.Edge, "Demo Edge", null));
        }

        for (var i = 1; i <= ClubOS.Contracts.DemoEnrollment.DeviceCount; i++)
        {
            tokens.Add((ClubOS.Contracts.DemoEnrollment.DeviceToken(secret, i), EnrollmentKinds.Device,
                ClubOS.Contracts.DemoEnrollment.DeviceName(i), i <= 3 ? DemoStandardZoneId : DemoVipZoneId));
        }

        var added = 0;
        foreach (var (token, kind, name, zoneId) in tokens)
        {
            var hash = Ids.HashSecret(token);
            if (await db.EnrollmentTokens.AnyAsync(x => x.TokenHash == hash, ct))
            {
                continue;
            }

            db.EnrollmentTokens.Add(new EnrollmentToken
            {
                Id = Ids.New("enr"),
                TenantId = OrganizationId,
                Kind = kind,
                LocationId = DemoLocationId,
                ZoneId = zoneId,
                DisplayName = name,
                Simulated = kind == EnrollmentKinds.Device,
                TokenHash = hash,
                CreatedBy = "system:seed-demo",
                CreatedAtUtc = now,
                // Демо можно включить не сразу после развёртывания; токены всё равно одноразовые.
                ExpiresAtUtc = now.AddDays(365)
            });
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seed: демо-зал — создано {Count} одноразовых enrollment-токенов", added);
        }
    }

    private static async Task SeedEdgeTokenAsync(ClubOsDbContext db, SeedOptions options, DateTimeOffset now, ILogger logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.EdgeEnrollmentToken) || options.EdgeEnrollmentToken.Length < 24)
        {
            return;
        }

        var hash = Ids.HashSecret(options.EdgeEnrollmentToken.Trim());
        if (await db.Edges.AnyAsync(x => x.LocationId == LocationId, ct) ||
            await db.EnrollmentTokens.AnyAsync(x => x.TokenHash == hash, ct))
        {
            return;
        }

        db.EnrollmentTokens.Add(new EnrollmentToken
        {
            Id = Ids.New("enr"),
            TenantId = OrganizationId,
            Kind = EnrollmentKinds.Edge,
            LocationId = LocationId,
            DisplayName = "Dev Edge",
            TokenHash = hash,
            CreatedBy = "system:seed",
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(7)
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seed: создан dev Edge enrollment-токен (одноразовый)");
    }
}
