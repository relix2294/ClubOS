using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClubOS.CloudApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class Revocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RevokedAtUtc",
                table: "edges",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevokedBy",
                table: "edges",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RevokedAtUtc",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevokedBy",
                table: "devices",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RevokedAtUtc",
                table: "edges");

            migrationBuilder.DropColumn(
                name: "RevokedBy",
                table: "edges");

            migrationBuilder.DropColumn(
                name: "RevokedAtUtc",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "RevokedBy",
                table: "devices");
        }
    }
}
