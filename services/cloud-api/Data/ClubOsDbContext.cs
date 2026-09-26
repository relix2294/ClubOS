using ClubOS.CloudApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Data;

public sealed class ClubOsDbContext(DbContextOptions<ClubOsDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Edge> Edges => Set<Edge>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceCommand> DeviceCommands => Set<DeviceCommand>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<EdgeOutboxItem> EdgeOutbox => Set<EdgeOutboxItem>();
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
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Zone>(e =>
        {
            e.ToTable("zones");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.LocationId);
            e.Property(x => x.Rounding).HasConversion<string>();
            e.HasOne<Location>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.OrganizationId);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Edge>(e =>
        {
            e.ToTable("edges");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.LocationId);
            e.HasOne<Location>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
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
            e.HasIndex(x => new { x.TenantId, x.LocationId });
            e.Property(x => x.Status).HasConversion<string>();
            e.HasOne<Location>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Zone>().WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<DeviceCommand>(e =>
        {
            e.ToTable("device_commands");
            e.HasKey(x => x.Id); // UNIQUE(commandId) — повтор не создаёт вторую команду (CMD-002)
            e.HasIndex(x => new { x.DeviceId, x.IssuedAtUtc });
            e.HasIndex(x => new { x.State, x.ExpiresAtUtc });
            e.Property(x => x.CommandType).HasConversion<string>();
            e.Property(x => x.State).HasConversion<string>();
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.DeviceId, x.RequestedAtUtc });
            e.Property(x => x.State).HasConversion<string>();
            e.Property(x => x.Rounding).HasConversion<string>();
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<EdgeOutboxItem>(e =>
        {
            e.ToTable("edge_outbox");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.LocationId, x.AckedAtUtc, x.CreatedAtUtc });
            e.Property(x => x.Kind).HasConversion<string>();
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
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
            e.HasIndex(x => new { x.TenantId, x.OccurredAtUtc });
            e.HasIndex(x => x.Target);
            e.Property(x => x.DetailsJson).HasColumnType("jsonb");
        });
    }
}
