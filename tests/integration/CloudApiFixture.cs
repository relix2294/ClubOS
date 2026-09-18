using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>
/// Поднимает PostgreSQL в Testcontainers и Cloud API через WebApplicationFactory.
/// Требует Docker (на CI ubuntu он есть; локально — блокер B2). Миграции и seed
/// выполняются на старте приложения.
/// </summary>
public sealed class CloudApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
        .WithImage("postgres:18")
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = default!;

    public HttpClient Client { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await _db.StartAsync();

        Environment.SetEnvironmentVariable("CLUBOS_DB_CONNECTION", _db.GetConnectionString());
        Environment.SetEnvironmentVariable("CLUBOS_JWT_KEY", "integration-tests-signing-key-0123456789abc");
        Environment.SetEnvironmentVariable("CLUBOS_DEV_OWNER_PASSWORD", "Test1234!");

        Factory = new WebApplicationFactory<Program>();
        Client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await _db.DisposeAsync();
    }

    /// <summary>Создаёт устройство в первой seeded локации, возвращает его идентификаторы.</summary>
    public async Task<(string LocationId, string ZoneId, string DeviceId)> CreateDeviceAsync(bool simulated = true)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubOsDbContext>();

        var location = await db.Locations.FirstAsync();
        var zone = await db.Zones.FirstAsync(z => z.LocationId == location.Id);

        var device = new Device
        {
            Id = Guid.NewGuid().ToString("N"),
            LocationId = location.Id,
            ZoneId = zone.Id,
            DisplayName = "IT-" + Guid.NewGuid().ToString("N")[..6],
            Simulated = simulated,
            Status = DeviceStatus.Idle,
        };

        db.Devices.Add(device);
        await db.SaveChangesAsync();

        return (location.Id, zone.Id, device.Id);
    }

    /// <summary>Создаёт отдельную организацию с локацией и устройством (для проверки изоляции).</summary>
    public async Task<(string OrganizationId, string LocationId, string DeviceId)> CreateForeignDeviceAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubOsDbContext>();

        var org = new Organization { Id = Guid.NewGuid().ToString("N"), Name = "Foreign Org", CreatedAtUtc = DateTimeOffset.UtcNow };
        var location = new Location
        {
            Id = Guid.NewGuid().ToString("N"),
            OrganizationId = org.Id,
            Name = "Foreign Location",
            Timezone = "Asia/Dushanbe",
            Currency = "TJS",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var zone = new Zone { Id = Guid.NewGuid().ToString("N"), LocationId = location.Id, Name = "Standard" };
        var device = new Device
        {
            Id = Guid.NewGuid().ToString("N"),
            LocationId = location.Id,
            ZoneId = zone.Id,
            DisplayName = "FOREIGN-PC",
            Simulated = true,
            Status = DeviceStatus.Idle,
        };

        db.Organizations.Add(org);
        db.Locations.Add(location);
        db.Zones.Add(zone);
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        return (org.Id, location.Id, device.Id);
    }
}

[CollectionDefinition("cloud")]
public sealed class CloudCollection : ICollectionFixture<CloudApiFixture>;
