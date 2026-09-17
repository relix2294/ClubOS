using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClubOS.CloudApi.Data;

/// <summary>
/// Фабрика для инструментов EF Core (`dotnet ef migrations add`, `database update`).
/// Строка подключения берётся из env CLUBOS_DB_CONNECTION или dev-default.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ClubOsDbContext>
{
    public ClubOsDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("CLUBOS_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=clubos;Username=clubos;Password=clubos_dev";

        var options = new DbContextOptionsBuilder<ClubOsDbContext>()
            .UseNpgsql(connection)
            .Options;

        return new ClubOsDbContext(options);
    }
}
