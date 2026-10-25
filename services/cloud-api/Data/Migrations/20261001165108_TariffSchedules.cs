using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClubOS.CloudApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class TariffSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PeriodsJson",
                table: "zones",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PackageId",
                table: "sessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PackageMinutes",
                table: "sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PackageName",
                table: "sessions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PackagePriceMinorUnits",
                table: "sessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PeriodsJson",
                table: "sessions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UtcOffsetMinutes",
                table: "sessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "tariff_packages",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    LocationId = table.Column<string>(type: "text", nullable: false),
                    ZoneId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    PriceMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    AvailableFromMinute = table.Column<int>(type: "integer", nullable: true),
                    AvailableToMinute = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tariff_packages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tariff_packages_organizations_TenantId",
                        column: x => x.TenantId,
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tariff_packages_zones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "zones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_PackageId",
                table: "sessions",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_tariff_packages_LocationId",
                table: "tariff_packages",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_tariff_packages_TenantId",
                table: "tariff_packages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_tariff_packages_ZoneId_Name",
                table: "tariff_packages",
                columns: new[] { "ZoneId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tariff_packages");

            migrationBuilder.DropIndex(
                name: "IX_sessions_PackageId",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "PeriodsJson",
                table: "zones");

            migrationBuilder.DropColumn(
                name: "PackageId",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "PackageMinutes",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "PackageName",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "PackagePriceMinorUnits",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "PeriodsJson",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "UtcOffsetMinutes",
                table: "sessions");
        }
    }
}
