using ClubOS.CloudApi.Common;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Security;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Data;

/// <summary>
/// Создание схемы и dev-seed (ТЗ §25.2.3). На M0 схема создаётся из модели через
/// EnsureCreated (D-009) — без EF-миграций. Seed идемпотентен: выполняется только если
/// БД пуста. Демо-набор: Demo Club Group / Dushanbe Pilot / Asia/Dushanbe / TJS /
/// зоны Standard, VIP / dev Owner.
/// </summary>
public static class DatabaseBootstrapper
{
    public const string DemoOwnerEmail = "owner@demo.clubos";

    public static async Task MigrateAndSeedAsync(
        ClubOsDbContext db,
        string ownerPassword,
        CancellationToken cancellationToken = default)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken);

        if (await db.Organizations.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        var org = new Organization
        {
            Id = Ids.New(),
            Name = "Demo Club Group",
            CreatedAtUtc = now,
        };

        var location = new Location
        {
            Id = Ids.New(),
            OrganizationId = org.Id,
            Name = "Dushanbe Pilot",
            Timezone = "Asia/Dushanbe",
            Currency = "TJS",
            CreatedAtUtc = now,
        };

        var standard = new Zone { Id = Ids.New(), LocationId = location.Id, Name = "Standard" };
        var vip = new Zone { Id = Ids.New(), LocationId = location.Id, Name = "VIP" };

        var owner = new User
        {
            Id = Ids.New(),
            OrganizationId = org.Id,
            Email = DemoOwnerEmail,
            PasswordHash = PasswordHasher.Hash(ownerPassword),
            Role = "Owner",
            IsActive = true,
            CreatedAtUtc = now,
        };

        db.Organizations.Add(org);
        db.Locations.Add(location);
        db.Zones.AddRange(standard, vip);
        db.Users.Add(owner);

        await db.SaveChangesAsync(cancellationToken);
    }
}
