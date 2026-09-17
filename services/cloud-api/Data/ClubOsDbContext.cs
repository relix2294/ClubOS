using ClubOS.CloudApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Data;

public sealed class ClubOsDbContext(DbContextOptions<ClubOsDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Edge> Edges => Set<Edge>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceCommand> DeviceCommands => Set<DeviceCommand>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<InboxReceipt> InboxReceipts => Set<InboxReceipt>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Organization>(e =>
        {
            e.ToTable("organizations");
            e.HasKey(x => x.Id);
        });

        b.Entity<Location>(e =>
        {
            e.ToTable("locations");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OrganizationId);
        });

        b.Entity<Zone>(e =>
        {
            e.ToTable("zones");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.LocationId);
        });

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.OrganizationId);
        });

        b.Entity<Edge>(e =>
        {
            e.ToTable("edges");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.LocationId);
        });

        b.Entity<EnrollmentToken>(e =>
        {
            e.ToTable("enrollment_tokens");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
        });

        b.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.LocationId);
            e.Property(x => x.Status).HasConversion<string>();
        });

        b.Entity<DeviceCommand>(e =>
        {
            e.ToTable("device_commands");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DeviceId);
            e.Property(x => x.CommandType).HasConversion<string>();
            e.Property(x => x.State).HasConversion<string>();
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
        });

        b.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DeviceId);
            e.Property(x => x.State).HasConversion<string>();
            e.Property(x => x.Rounding).HasConversion<string>();
        });

        b.Entity<InboxReceipt>(e =>
        {
            e.ToTable("inbox_receipts");
            e.HasKey(x => x.EventId); // UNIQUE(eventId) => идемпотентность sync (ТЗ §4)
            e.HasIndex(x => new { x.LocationId, x.Sequence });
        });

        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => x.OccurredAtUtc);
            e.Property(x => x.BeforeAfterJson).HasColumnType("jsonb");
        });
    }
}
