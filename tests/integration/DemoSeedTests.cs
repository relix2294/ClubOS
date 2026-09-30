using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>Демо-зал VPS (профиль demo): локация, зоны и одноразовые токены из секрета; повторный seed не дублирует.</summary>
[Collection(CloudCollection.Name)]
public class DemoSeedTests(CloudFixture cloud)
{
    [Fact]
    public async Task Demo_hall_is_seeded_once_with_edge_and_device_tokens()
    {
        var secret = Guid.NewGuid().ToString("N");
        var options = new SeedOptions { Enabled = true, OwnerEmail = CloudFixture.OwnerEmail, DemoSecret = secret };

        for (var run = 0; run < 2; run++)
        {
            await cloud.WithDb(async db =>
            {
                await DevSeeder.SeedAsync(db, options, TimeProvider.System, NullLogger.Instance);
                return 0;
            });
        }

        await cloud.WithDb(async db =>
        {
            var location = await db.Locations.SingleAsync(x => x.Id == DevSeeder.DemoLocationId);
            Assert.Equal("Демо-зал (симулятор)", location.Name);
            var zones = await db.Zones.Where(x => x.LocationId == DevSeeder.DemoLocationId).ToDictionaryAsync(x => x.Name);
            Assert.Equal(12_000, zones["Standard"].PricePerHourMinorUnits);
            Assert.Equal(18_000, zones["VIP"].PricePerHourMinorUnits);

            var edgeHash = Ids.HashSecret(DemoEnrollment.EdgeToken(secret));
            var edgeToken = await db.EnrollmentTokens.SingleAsync(x => x.TokenHash == edgeHash);
            Assert.Equal(EnrollmentKinds.Edge, edgeToken.Kind);
            Assert.Equal(DevSeeder.DemoLocationId, edgeToken.LocationId);

            for (var i = 1; i <= DemoEnrollment.DeviceCount; i++)
            {
                var hash = Ids.HashSecret(DemoEnrollment.DeviceToken(secret, i));
                var token = await db.EnrollmentTokens.SingleAsync(x => x.TokenHash == hash);
                Assert.Equal(DemoEnrollment.DeviceName(i), token.DisplayName);
                Assert.True(token.Simulated);
                Assert.Equal(i <= 3 ? DevSeeder.DemoStandardZoneId : DevSeeder.DemoVipZoneId, token.ZoneId);
            }

            return 0;
        });

        // Короткий секрет демо-зал не создаёт (защита от угадываемых токенов).
        var shortSecret = new SeedOptions { Enabled = true, OwnerEmail = CloudFixture.OwnerEmail, DemoSecret = "too-short" };
        var before = await cloud.WithDb(db => db.EnrollmentTokens.CountAsync());
        await cloud.WithDb(async db =>
        {
            await DevSeeder.SeedAsync(db, shortSecret, TimeProvider.System, NullLogger.Instance);
            return 0;
        });
        Assert.Equal(before, await cloud.WithDb(db => db.EnrollmentTokens.CountAsync()));
    }
}
