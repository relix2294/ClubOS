using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClubOS.CloudApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class Pos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SaleId",
                table: "cash_operations",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    LocationId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PriceMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    TrackStock = table.Column<bool>(type: "boolean", nullable: false),
                    StockQuantity = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_products", x => x.Id);
                    table.ForeignKey(
                        name: "FK_products_locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_products_organizations_TenantId",
                        column: x => x.TenantId,
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    LocationId = table.Column<string>(type: "text", nullable: false),
                    ShiftId = table.Column<string>(type: "text", nullable: false),
                    CashOperationId = table.Column<string>(type: "text", nullable: false),
                    Method = table.Column<string>(type: "text", nullable: false),
                    ClientId = table.Column<string>(type: "text", nullable: true),
                    TotalMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RefundOperationId = table.Column<string>(type: "text", nullable: true),
                    RefundedBy = table.Column<string>(type: "text", nullable: true),
                    RefundedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RefundReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sales_cash_shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "cash_shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_movements",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    LocationId = table.Column<string>(type: "text", nullable: false),
                    ProductId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    QuantityAfter = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    SaleId = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_movements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_stock_movements_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sale_items",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    SaleId = table.Column<string>(type: "text", nullable: false),
                    ProductId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    PriceMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    TotalMinorUnits = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sale_items_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sale_items_sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "sales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_products_LocationId_Name",
                table: "products",
                columns: new[] { "LocationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_products_TenantId",
                table: "products",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_items_ProductId",
                table: "sale_items",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_items_SaleId",
                table: "sale_items",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_sales_CashOperationId",
                table: "sales",
                column: "CashOperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_ClientId",
                table: "sales",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_sales_LocationId_CreatedAtUtc",
                table: "sales",
                columns: new[] { "LocationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_ShiftId_CreatedAtUtc",
                table: "sales",
                columns: new[] { "ShiftId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_movements_LocationId_CreatedAtUtc",
                table: "stock_movements",
                columns: new[] { "LocationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_movements_ProductId_CreatedAtUtc",
                table: "stock_movements",
                columns: new[] { "ProductId", "CreatedAtUtc" });

            // Склад и позиции чеков — журнал: исправление только новым движением или возвратом чека.
            migrationBuilder.Sql("""
                CREATE FUNCTION stock_movements_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'stock_movements is append-only'; END; $$;
                CREATE TRIGGER stock_movements_no_update_delete BEFORE UPDATE OR DELETE ON stock_movements
                FOR EACH ROW EXECUTE FUNCTION stock_movements_immutable();
                CREATE FUNCTION sale_items_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'sale_items is append-only'; END; $$;
                CREATE TRIGGER sale_items_no_update_delete BEFORE UPDATE OR DELETE ON sale_items
                FOR EACH ROW EXECUTE FUNCTION sale_items_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS stock_movements_no_update_delete ON stock_movements;
                DROP FUNCTION IF EXISTS stock_movements_immutable();
                DROP TRIGGER IF EXISTS sale_items_no_update_delete ON sale_items;
                DROP FUNCTION IF EXISTS sale_items_immutable();
                """);

            migrationBuilder.DropTable(
                name: "sale_items");

            migrationBuilder.DropTable(
                name: "stock_movements");

            migrationBuilder.DropTable(
                name: "sales");

            migrationBuilder.DropTable(
                name: "products");

            migrationBuilder.DropColumn(
                name: "SaleId",
                table: "cash_operations");
        }
    }
}
