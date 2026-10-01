using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Security;
using ClubOS.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace ClubOS.Integration.Tests;

// В .NET 10 обе веб-сборки (Cloud и Edge) генерируют публичный Program, поэтому точка входа
// для WebApplicationFactory указывается через любой тип из нужной сборки.
using CloudApiAssembly = ClubOS.CloudApi.Auth.AuthOptions;

[CollectionDefinition(Name)]
public sealed class CloudCollection : ICollectionFixture<CloudFixture>
{
    public const string Name = "cloud";
}

/// <summary>PostgreSQL 18 в контейнере + Cloud API in-process (одна БД на весь прогон).</summary>
public sealed class CloudFixture : IAsyncLifetime
{
    public const string OwnerEmail = "owner@demo.clubos.local";
    public const string OwnerPassword = "integration-owner-pass";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("clubos")
        .WithUsername("clubos")
        .WithPassword("clubos_test")
        .Build();

    private readonly string _caPath = Path.Combine(Path.GetTempPath(), "clubos-it-ca-" + Guid.NewGuid().ToString("N"));

    public WebApplicationFactory<CloudApiAssembly> Factory { get; private set; } = null!;

    public static JsonSerializerOptions Json => ContractJson.Options;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Factory = new WebApplicationFactory<CloudApiAssembly>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:ClubOs", _postgres.GetConnectionString());
            b.UseSetting("Auth:SigningKey", "integration-tests-signing-key-0123456789abcdef");
            b.UseSetting("DevCa:Path", _caPath);
            b.UseSetting("Seed:Enabled", "true");
            b.UseSetting("Seed:OwnerPassword", OwnerPassword);
            b.UseSetting("RateLimits:AuthPerMinute", "10000");
            // MFA добровольная для общих тестов; обязательную проверяет MfaTests на отдельном хосте.
            b.UseSetting("Auth:MfaRequiredRoles", "");
        });
        _ = Factory.Server; // старт: миграции + seed
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
        try
        {
            Directory.Delete(_caPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public HttpClient Anonymous() => Factory.CreateClient();

    public async Task<HttpClient> LoginAsync(string email = OwnerEmail, string password = OwnerPassword)
    {
        var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return client;
    }

    /// <summary>Отдельная локация на тест (своя очередь Edge) внутри демо-организации или новой.</summary>
    public async Task<TestLocation> CreateLocationAsync(string organizationId = DevSeeder.OrganizationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubOsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var location = new Location
        {
            Id = $"loc_{suffix}",
            OrganizationId = organizationId,
            Name = $"Test {suffix}",
            Timezone = "Asia/Dushanbe",
            Currency = "TJS",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var zone = new Zone { Id = $"zone_{suffix}", LocationId = location.Id, Name = "Standard", PricePerHourMinorUnits = 12_000 };
        db.Locations.Add(location);
        db.Zones.Add(zone);
        await db.SaveChangesAsync();
        return new TestLocation(organizationId, location.Id, zone.Id);
    }

    public async Task<(string Email, string Password)> CreateOrganizationWithOwnerAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubOsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var orgId = $"org_{suffix}";
        db.Organizations.Add(new Organization { Id = orgId, Name = $"Other {suffix}", CreatedAtUtc = DateTimeOffset.UtcNow });
        var email = $"owner-{suffix}@other.test";
        db.Users.Add(new User
        {
            Id = $"usr_{suffix}",
            OrganizationId = orgId,
            Email = email,
            DisplayName = "Other owner",
            PasswordHash = PasswordHasher.Hash(OwnerPassword),
            Role = Roles.Owner,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return (email, OwnerPassword);
    }

    public async Task<T> WithDb<T>(Func<ClubOsDbContext, Task<T>> work)
    {
        using var scope = Factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<ClubOsDbContext>());
    }
}

public sealed record TestLocation(string OrganizationId, string LocationId, string ZoneId);

