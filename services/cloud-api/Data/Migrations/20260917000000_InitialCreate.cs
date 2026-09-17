using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClubOS.CloudApi.Data.Migrations;

/// <summary>
/// Начальная схема M0 (ТЗ §25.2.3). Применяется на старте через Database.Migrate().
/// Имена колонок = имена свойств (EF default); имена таблиц — snake_case (ToTable).
/// </summary>
[Migration("20260917000000_InitialCreate")]
public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "organizations",
            columns: table => new
            {
                Id = table.Column<string>(nullable: false),
                Name = table.Column<string>(nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_organizations", x => x.Id));

        migrationBuilder.CreateTable(
            name: "locations",
            columns: table => new
            {
                Id = table.Column<string>(nullable: false),
                OrganizationId = table.Column<string>(nullable: false),
                Name = table.Column<string>(nullable: false),
                Timezone = table.Column<string>(nullable: false),
                Currency = table.Column<string>(nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_locations", x => x.Id));

        migrationBuilder.CreateTable(
            name: "zones",
            columns: table => new
            {
                Id = table.Column<string>(nullable: false),
                LocationId = table.Column<string>(nullable: false),
                Name = table.Column<string>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_zones", x => x.Id));

        migrationBuilder.CreateTable(
            name: "users",
            columns: table => new
            {
                Id = table.Column<string>(nullable: false),
                OrganizationId = table.Column<string>(nullable: false),
                Email = table.Column<string>(nullable: false),
                PasswordHash = table.Column<string>(nullable: false),
                Role = table.Column<string>(nullable: false),
                IsActive = table.Column<bool>(nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_users", x => x.Id));

        migrationBuilder.CreateTable(
            name: "edges",
            columns: table => new
            {
                Id = table.Column<string>(nullable: false),
                LocationId = table.Column<string>(nullable: false),
                Name = table.Column<string>(nullable: false),
                CertificatePem = table.Column<string>(nullable: true),
                EnrolledAtUtc = table.Column<DateTimeOffset>(nullable: true),
                LastSeenAtUtc = table.Column<DateTimeOffset>(nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_edges", x => x.Id));

        migrationBuilder.CreateTable(
            name: "enrollment_tokens",
            columns: table => new
            {
                Id = table.Column<string>(nullable: false),
                LocationId = table.Column<string>(nullable: false),
                ZoneId = table.Column<string>(nullable: false),
                DisplayName = table.Column<string>(nullable: false),
                TokenHash = table.Column<string>(nullable: false),
                CreatedBy = table.Column<string>(nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(nullable: false),
                ExpiresAtUtc = table.Column<DateTimeOffset>(nullable: false),
                UsedAtUtc = table.Column<DateTimeOffset>(nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_enrollment_tokens", x => x.Id));

        migrationBuilder.CreateTable(
            name: "devices",
            columns: table => new
            {
                Id = table.Column<string>(nullable: false),
                LocationId = table.Column<string>(nullable: false),
                ZoneId = table.Column<string>(nullable: false),
                DisplayName = table.Column<string>(nullable: false),
                Simulated = table.Column<bool>(nullable: false),
                Status = table.Column<string>(nullable: false),
                LastHeartbeatUtc = table.Column<DateTimeOffset>(nullable: true),
                Hostname = table.Column<string>(nullable: true),
                WindowsVersion = table.Column<string>(nullable: true),
                Cpu = table.Column<string>(nullable: true),
                RamMegabytes = table.Column<int>(nullable: true),
                Ipv4 = table.Column<string>(nullable: true),
                AgentVersion = table.Column<string>(nullable: true),
                CertificatePem = table.Column<string>(nullable: true),
                EnrolledAtUtc = table.Column<DateTimeOffset>(nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_devices", x => x.Id));

        migrationBuilder.CreateTable(
            name: "device_commands",
            columns: table => new
            {
                Id = table.Column<string>(nullable: false),
                DeviceId = table.Column<string>(nullable: false),
                LocationId = table.Column<string>(nullable: false),
                CommandType = table.Column<string>(nullable: false),
                PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                IssuedBy = table.Column<string>(nullable: false),
                IssuedAtUtc = table.Column<DateTimeOffset>(nullable: false),
                ExpiresAtUtc = table.Column<DateTimeOffset>(nullable: false),
                CorrelationId = table.Column<string>(nullable: false),
                State = table.Column<string>(nullable: false),
                Error = table.Column<string>(nullable: true),
                UpdatedAtUtc = table.Column<DateTimeOffset>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_device_commands", x => x.Id));

        migrationBuilder.CreateTable(
            name: "sessions",
            columns: table => new
            {
                Id = table.Column<string>(nullable: false),
                DeviceId = table.Column<string>(nullable: false),
                LocationId = table.Column<string>(nullable: false),
                State = table.Column<string>(nullable: false),
                StartedAtUtc = table.Column<DateTimeOffset>(nullable: false),
                EndedAtUtc = table.Column<DateTimeOffset>(nullable: true),
                PricePerHourMinorUnits = table.Column<long>(nullable: false),
                Currency = table.Column<string>(nullable: false),
                Rounding = table.Column<string>(nullable: false),
                RuleVersion = table.Column<int>(nullable: false),
                TotalMinorUnits = table.Column<long>(nullable: true),
                Actor = table.Column<string>(nullable: false),
                CorrelationId = table.Column<string>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_sessions", x => x.Id));

        migrationBuilder.CreateTable(
            name: "inbox_receipts",
            columns: table => new
            {
                EventId = table.Column<string>(nullable: false),
                TenantId = table.Column<string>(nullable: false),
                LocationId = table.Column<string>(nullable: false),
                EventType = table.Column<string>(nullable: false),
                Sequence = table.Column<long>(nullable: false),
                ReceivedAtUtc = table.Column<DateTimeOffset>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_inbox_receipts", x => x.EventId));

        migrationBuilder.CreateTable(
            name: "audit_events",
            columns: table => new
            {
                Id = table.Column<string>(nullable: false),
                TenantId = table.Column<string>(nullable: false),
                LocationId = table.Column<string>(nullable: true),
                OccurredAtUtc = table.Column<DateTimeOffset>(nullable: false),
                Actor = table.Column<string>(nullable: false),
                Action = table.Column<string>(nullable: false),
                Target = table.Column<string>(nullable: false),
                Result = table.Column<string>(nullable: false),
                CorrelationId = table.Column<string>(nullable: true),
                BeforeAfterJson = table.Column<string>(type: "jsonb", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_audit_events", x => x.Id));

        migrationBuilder.CreateIndex("IX_locations_OrganizationId", "locations", "OrganizationId");
        migrationBuilder.CreateIndex("IX_zones_LocationId", "zones", "LocationId");
        migrationBuilder.CreateIndex("IX_users_Email", "users", "Email", unique: true);
        migrationBuilder.CreateIndex("IX_users_OrganizationId", "users", "OrganizationId");
        migrationBuilder.CreateIndex("IX_edges_LocationId", "edges", "LocationId");
        migrationBuilder.CreateIndex("IX_enrollment_tokens_TokenHash", "enrollment_tokens", "TokenHash", unique: true);
        migrationBuilder.CreateIndex("IX_devices_LocationId", "devices", "LocationId");
        migrationBuilder.CreateIndex("IX_device_commands_DeviceId", "device_commands", "DeviceId");
        migrationBuilder.CreateIndex("IX_sessions_DeviceId", "sessions", "DeviceId");
        migrationBuilder.CreateIndex("IX_inbox_receipts_LocationId_Sequence", "inbox_receipts", ["LocationId", "Sequence"]);
        migrationBuilder.CreateIndex("IX_audit_events_TenantId", "audit_events", "TenantId");
        migrationBuilder.CreateIndex("IX_audit_events_OccurredAtUtc", "audit_events", "OccurredAtUtc");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("audit_events");
        migrationBuilder.DropTable("inbox_receipts");
        migrationBuilder.DropTable("sessions");
        migrationBuilder.DropTable("device_commands");
        migrationBuilder.DropTable("devices");
        migrationBuilder.DropTable("enrollment_tokens");
        migrationBuilder.DropTable("edges");
        migrationBuilder.DropTable("users");
        migrationBuilder.DropTable("zones");
        migrationBuilder.DropTable("locations");
        migrationBuilder.DropTable("organizations");
    }
}
