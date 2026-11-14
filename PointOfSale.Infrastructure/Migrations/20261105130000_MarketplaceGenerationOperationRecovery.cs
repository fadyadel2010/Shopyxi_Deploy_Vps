using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261105130000_MarketplaceGenerationOperationRecovery")]
public sealed class MarketplaceGenerationOperationRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(MarketplaceGenerationOperationSchema.Sql);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Immutable operation evidence requires explicit reviewed retention, not automatic rollback deletion.");
}
