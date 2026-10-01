using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClubOS.CloudApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class DisklessDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HardwareId",
                table: "devices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "pending_diskless_devices",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    LocationId = table.Column<string>(type: "text", nullable: false),
                    EdgeId = table.Column<string>(type: "text", nullable: false),
                    HardwareId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MacAddresses = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Hostname = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Ipv4 = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    Simulated = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_diskless_devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pending_diskless_devices_locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_devices_active_hardware",
                table: "devices",
                columns: new[] { "TenantId", "HardwareId" },
                unique: true,
                filter: "\"HardwareId\" IS NOT NULL AND \"RevokedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_pending_diskless_devices_LocationId_HardwareId",
                table: "pending_diskless_devices",
                columns: new[] { "LocationId", "HardwareId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_diskless_devices");

            migrationBuilder.DropIndex(
                name: "IX_devices_active_hardware",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "HardwareId",
                table: "devices");
        }
    }
}
