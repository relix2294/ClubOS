using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClubOS.CloudApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class Bookings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bookings",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    LocationId = table.Column<string>(type: "text", nullable: false),
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    ClientId = table.Column<string>(type: "text", nullable: true),
                    GuestName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    GuestPhone = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    StartsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    SessionId = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedBy = table.Column<string>(type: "text", nullable: true),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bookings_clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bookings_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bookings_organizations_TenantId",
                        column: x => x.TenantId,
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bookings_ClientId",
                table: "bookings",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_bookings_DeviceId_Status_StartsAtUtc",
                table: "bookings",
                columns: new[] { "DeviceId", "Status", "StartsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_bookings_LocationId_StartsAtUtc",
                table: "bookings",
                columns: new[] { "LocationId", "StartsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_bookings_TenantId",
                table: "bookings",
                column: "TenantId");

            // Брони одного ПК не пересекаются, пока ждут гостя: проверяет БД, не код (гонка двух администраторов).
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");
            migrationBuilder.Sql("""
                ALTER TABLE bookings ADD CONSTRAINT ex_bookings_device_time
                EXCLUDE USING gist ("DeviceId" WITH =, tstzrange("StartsAtUtc", "EndsAtUtc") WITH &&)
                WHERE ("Status" = 'Booked');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bookings");
        }
    }
}
