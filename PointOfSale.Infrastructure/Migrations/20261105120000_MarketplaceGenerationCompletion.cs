using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261105120000_MarketplaceGenerationCompletion")]
public sealed class MarketplaceGenerationCompletionMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(MarketplaceGenerationCompletionSchema.Sql);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Generation delivery and reader history must not be discarded.");
}
