using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261105140000_MarketplaceGenerationGateway")]
public sealed class MarketplaceGenerationGateway : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(MarketplaceGenerationGatewaySchema.Sql);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Gateway request evidence and epoch safety cannot be removed by rollback.");
}
