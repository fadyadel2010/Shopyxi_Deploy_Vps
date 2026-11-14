using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Infrastructure.Migrations;

// Candidate-only unmanaged schema. No application startup, promotion or shared DB
// execution is authorized. No EF entity change, so the model snapshot is unchanged.
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261101203000_MarketplaceGenerationBuildRegistry")]
public sealed class MarketplaceGenerationBuildRegistry : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if(ActiveProvider!="Npgsql.EntityFrameworkCore.PostgreSQL")throw new NotSupportedException("PostgreSQL required.");
        migrationBuilder.Sql(MarketplaceGenerationRegistry.SchemaSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Never discard retained generation input versions, tombstones or build ownership through automatic downgrade.");
}
