using Microsoft.EntityFrameworkCore;

namespace ClubOS.EdgeController.Data;

/// <summary>
/// Локальная БД Edge на SQLite (WAL) — ТЗ §25.2.4, D-001. Схема создаётся через
/// EnsureCreated (без миграций для локальной dev-БД Edge).
/// </summary>
public sealed class EdgeDbContext(DbContextOptions<EdgeDbContext> options) : DbContext(options)
{
    public DbSet<OutboxEvent> Outbox => Set<OutboxEvent>();
    public DbSet<InboxReceipt> Inbox => Set<InboxReceipt>();
    public DbSet<EdgeSession> Sessions => Set<EdgeSession>();
    public DbSet<EdgeCommand> Commands => Set<EdgeCommand>();
    public DbSet<EdgeDeviceState> Devices => Set<EdgeDeviceState>();
    public DbSet<CachedConfig> Config => Set<CachedConfig>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<OutboxEvent>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptUtc });
            e.Property(x => x.Status).HasConversion<string>();
        });

        b.Entity<InboxReceipt>(e =>
        {
            e.ToTable("inbox");
            e.HasKey(x => x.Id);
        });

        b.Entity<EdgeSession>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DeviceId);
            e.Property(x => x.State).HasConversion<string>();
            e.Property(x => x.Rounding).HasConversion<string>();
        });

        b.Entity<EdgeCommand>(e =>
        {
            e.ToTable("commands");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DeviceId);
            e.Property(x => x.CommandType).HasConversion<string>();
            e.Property(x => x.State).HasConversion<string>();
        });

        b.Entity<EdgeDeviceState>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.DeviceId);
            e.Property(x => x.Status).HasConversion<string>();
        });

        b.Entity<CachedConfig>(e =>
        {
            e.ToTable("config");
            e.HasKey(x => x.Key);
        });
    }
}
