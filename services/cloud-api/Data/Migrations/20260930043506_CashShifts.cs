using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClubOS.CloudApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class CashShifts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cash_shifts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    LocationId = table.Column<string>(type: "text", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    OpenedBy = table.Column<string>(type: "text", nullable: false),
                    OpenedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OpeningCashMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    ClosedBy = table.Column<string>(type: "text", nullable: true),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpectedCashMinorUnits = table.Column<long>(type: "bigint", nullable: true),
                    CountedCashMinorUnits = table.Column<long>(type: "bigint", nullable: true),
                    CloseNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cash_shifts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cash_shifts_locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cash_operations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    LocationId = table.Column<string>(type: "text", nullable: false),
                    ShiftId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AmountMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    SessionId = table.Column<string>(type: "text", nullable: true),
                    DeviceId = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cash_operations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cash_operations_cash_shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "cash_shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cash_operations_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cash_operations_SessionId",
                table: "cash_operations",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_cash_operations_ShiftId_CreatedAtUtc",
                table: "cash_operations",
                columns: new[] { "ShiftId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_operations_TenantId_IdempotencyKey",
                table: "cash_operations",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_cash_operations_TenantId_LocationId_CreatedAtUtc",
                table: "cash_operations",
                columns: new[] { "TenantId", "LocationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_shifts_TenantId_LocationId_OpenedAtUtc",
                table: "cash_shifts",
                columns: new[] { "TenantId", "LocationId", "OpenedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_shifts_open_per_location",
                table: "cash_shifts",
                column: "LocationId",
                unique: true,
                filter: "\"ClosedAtUtc\" IS NULL");

            // Кассовые операции иммутабельны (ТЗ §12.2.1): исправление — только новой операцией.
            // Запрет UPDATE/DELETE на уровне БД защищает деньги и от ошибки в коде.
            migrationBuilder.Sql("""
                CREATE FUNCTION cash_operations_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'cash_operations is append-only';
                END;
                $$;
                CREATE TRIGGER cash_operations_no_update_delete
                    BEFORE UPDATE OR DELETE ON cash_operations
                    FOR EACH ROW EXECUTE FUNCTION cash_operations_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS cash_operations_no_update_delete ON cash_operations;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS cash_operations_immutable();");

            migrationBuilder.DropTable(
                name: "cash_operations");

            migrationBuilder.DropTable(
                name: "cash_shifts");
        }
    }
}
