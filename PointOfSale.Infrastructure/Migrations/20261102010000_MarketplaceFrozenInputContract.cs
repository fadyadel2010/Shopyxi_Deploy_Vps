using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Infrastructure.Migrations;

// Candidate unmanaged metadata. Existing unknown exports remain version0, not relabelled.
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261102010000_MarketplaceFrozenInputContract")]
public sealed class MarketplaceFrozenInputContractMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if(ActiveProvider!="Npgsql.EntityFrameworkCore.PostgreSQL")throw new NotSupportedException("PostgreSQL required.");
        migrationBuilder.Sql(MarketplaceFrozenInputContract.BuildSchemaSql+MarketplaceFrozenInputContract.ExportSchemaSql);
    }
    protected override void Down(MigrationBuilder migrationBuilder)=>throw new NotSupportedException("Never discard frozen input contract lineage by automatic downgrade.");
}
