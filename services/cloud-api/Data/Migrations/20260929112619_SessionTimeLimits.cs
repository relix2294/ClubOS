using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClubOS.CloudApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class SessionTimeLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DurationMinutes",
                table: "sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EndReason",
                table: "sessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PlannedEndAtUtc",
                table: "sessions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationMinutes",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "EndReason",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "PlannedEndAtUtc",
                table: "sessions");
        }
    }
}
