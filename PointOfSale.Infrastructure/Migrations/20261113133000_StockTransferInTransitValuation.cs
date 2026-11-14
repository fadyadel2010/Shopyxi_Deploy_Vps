using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261113133000_StockTransferInTransitValuation")]
public sealed class StockTransferInTransitValuation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE "StockTransferItems" ADD COLUMN "DispatchedUnitCost" numeric(18,4);
        ALTER TABLE "StockTransferItems" ADD CONSTRAINT "CK_StockTransferItem_DispatchedCost"
            CHECK ("DispatchedUnitCost" IS NULL OR "DispatchedUnitCost" >= 0);
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Frozen dispatch valuations are retained audit evidence.");
}
