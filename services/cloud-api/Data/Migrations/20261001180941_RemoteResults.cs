using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClubOS.CloudApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoteResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResultJson",
                table: "device_commands",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResultJson",
                table: "device_commands");
        }
    }
}
