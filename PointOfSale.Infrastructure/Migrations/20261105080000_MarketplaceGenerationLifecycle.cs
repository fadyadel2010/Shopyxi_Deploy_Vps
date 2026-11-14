using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261105080000_MarketplaceGenerationLifecycle")]
public sealed class MarketplaceGenerationLifecycleMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(MarketplaceGenerationLifecycleSchema.Sql);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Generation lifecycle audit history must not be discarded by a down migration.");
}
