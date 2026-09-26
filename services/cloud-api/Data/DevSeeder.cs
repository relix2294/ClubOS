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

    /// <summary>Пароль dev Owner — только из env (CLUBOS_Seed__OwnerPassword), не из git.</summary>
    public string? OwnerPassword { get; set; }

    /// <summary>
    /// Только dev (docker compose): одноразовый Edge enrollment-токен, заранее известный Edge-контейнеру.
    /// Создаётся, если в локации ещё нет Edge. В пилоте токен выдаётся через Admin Web.
    /// </summary>
    public string? EdgeEnrollmentToken { get; set; }
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

    public static async Task SeedAsync(ClubOsDbContext db, SeedOptions options, TimeProvider time, ILogger logger,
        CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        if (!await db.Organizations.AnyAsync(x => x.Id == OrganizationId, ct))
        {
            db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Demo Club Group", CreatedAtUtc = now });
            db.Locations.Add(new Location
            {
                Id = LocationId,
                OrganizationId = OrganizationId,
                Name = "Dushanbe Pilot",
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
            DisplayName = "Dev Owner",
            PasswordHash = PasswordHasher.Hash(options.OwnerPassword),
            Role = Roles.Owner,
            CreatedAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seed: создан dev Owner {Email}", email);
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
