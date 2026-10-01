using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClubOS.CloudApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class Clients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                table: "sessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                table: "cash_operations",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "clients",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    Phone = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    BalanceMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    IsBlocked = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_clients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_clients_organizations_TenantId",
                        column: x => x.TenantId,
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "client_ledger",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    ClientId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AmountMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    BalanceAfterMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    LocationId = table.Column<string>(type: "text", nullable: true),
                    SessionId = table.Column<string>(type: "text", nullable: true),
                    CashOperationId = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_ledger", x => x.Id);
                    table.ForeignKey(
                        name: "FK_client_ledger_clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_ClientId",
                table: "sessions",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_cash_operations_ClientId",
                table: "cash_operations",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_client_ledger_ClientId_CreatedAtUtc",
                table: "client_ledger",
                columns: new[] { "ClientId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_client_ledger_TenantId_CreatedAtUtc",
                table: "client_ledger",
                columns: new[] { "TenantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_clients_TenantId_DisplayName",
                table: "clients",
                columns: new[] { "TenantId", "DisplayName" });

            migrationBuilder.CreateIndex(
                name: "IX_clients_TenantId_Phone",
                table: "clients",
                columns: new[] { "TenantId", "Phone" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_cash_operations_clients_ClientId",
                table: "cash_operations",
                column: "ClientId",
                principalTable: "clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sessions_clients_ClientId",
                table: "sessions",
                column: "ClientId",
                principalTable: "clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Журнал баланса иммутабелен, как кассовые операции: исправление — новой записью (корректировкой).
            migrationBuilder.Sql("""
                CREATE FUNCTION client_ledger_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'client_ledger is append-only';
                END;
                $$;
                CREATE TRIGGER client_ledger_no_update_delete
                    BEFORE UPDATE OR DELETE ON client_ledger
                    FOR EACH ROW EXECUTE FUNCTION client_ledger_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS client_ledger_no_update_delete ON client_ledger;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS client_ledger_immutable();");

            migrationBuilder.DropForeignKey(
                name: "FK_cash_operations_clients_ClientId",
                table: "cash_operations");

            migrationBuilder.DropForeignKey(
                name: "FK_sessions_clients_ClientId",
                table: "sessions");

            migrationBuilder.DropTable(
                name: "client_ledger");

            migrationBuilder.DropTable(
                name: "clients");

            migrationBuilder.DropIndex(
                name: "IX_sessions_ClientId",
                table: "sessions");

            migrationBuilder.DropIndex(
                name: "IX_cash_operations_ClientId",
                table: "cash_operations");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "cash_operations");
        }
    }
}
