using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClubOS.CloudApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class StaffLocationAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllLocations",
                table: "users",
                type: "boolean",
                nullable: false,
                // Существующие сотрудники сохраняют доступ ко всем локациям, как до M1.
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "staff_location_access",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    LocationId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_location_access", x => new { x.UserId, x.LocationId });
                    table.ForeignKey(
                        name: "FK_staff_location_access_locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_staff_location_access_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_staff_location_access_LocationId",
                table: "staff_location_access",
                column: "LocationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "staff_location_access");

            migrationBuilder.DropColumn(
                name: "AllLocations",
                table: "users");
        }
    }
}
