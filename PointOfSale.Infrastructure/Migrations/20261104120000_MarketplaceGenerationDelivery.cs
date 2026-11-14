using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261104120000_MarketplaceGenerationDelivery")]
public sealed class MarketplaceGenerationDelivery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(MarketplaceGenerationDeliveryProtocol.SchemaSql);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Never discard unknown dispatches or retained refresh observations through automatic downgrade.");
}
