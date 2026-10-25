using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClubOS.CloudApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class StaffMfa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MfaEnabled",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MfaEnabledAtUtc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MfaLastUsedStep",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MfaPendingCreatedAtUtc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MfaPendingSecretProtected",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MfaSecretProtected",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mfa_challenges",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mfa_challenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mfa_challenges_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mfa_recovery_codes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    CodeHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mfa_recovery_codes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mfa_recovery_codes_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mfa_challenges_ExpiresAtUtc",
                table: "mfa_challenges",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_mfa_challenges_TokenHash",
                table: "mfa_challenges",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mfa_challenges_UserId",
                table: "mfa_challenges",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_mfa_recovery_codes_UserId_CodeHash",
                table: "mfa_recovery_codes",
                columns: new[] { "UserId", "CodeHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mfa_challenges");

            migrationBuilder.DropTable(
                name: "mfa_recovery_codes");

            migrationBuilder.DropColumn(
                name: "MfaEnabled",
                table: "users");

            migrationBuilder.DropColumn(
                name: "MfaEnabledAtUtc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "MfaLastUsedStep",
                table: "users");

            migrationBuilder.DropColumn(
                name: "MfaPendingCreatedAtUtc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "MfaPendingSecretProtected",
                table: "users");

            migrationBuilder.DropColumn(
                name: "MfaSecretProtected",
                table: "users");
        }
    }
}
