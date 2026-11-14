using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Infrastructure.Migrations;

// Candidate-only unmanaged schema. No shared/production execution or startup API.
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261102000000_MarketplaceFrozenGenerationInputs")]
public sealed class MarketplaceFrozenGenerationInputs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if(ActiveProvider!="Npgsql.EntityFrameworkCore.PostgreSQL")throw new NotSupportedException("PostgreSQL required.");
        migrationBuilder.Sql(MarketplaceGenerationInputExport.SchemaSql);
    }
    protected override void Down(MigrationBuilder migrationBuilder)=>
        throw new NotSupportedException("Never discard frozen source-version/tombstone manifests through automatic downgrade.");
}
