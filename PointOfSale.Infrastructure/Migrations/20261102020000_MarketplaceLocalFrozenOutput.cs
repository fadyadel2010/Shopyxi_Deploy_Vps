using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261102020000_MarketplaceLocalFrozenOutput")]
public sealed class MarketplaceLocalFrozenOutput : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if(ActiveProvider!="Npgsql.EntityFrameworkCore.PostgreSQL")throw new NotSupportedException("PostgreSQL required.");
        migrationBuilder.Sql(MarketplaceFrozenOutput.SchemaSql);
    }
    protected override void Down(MigrationBuilder migrationBuilder)=>throw new NotSupportedException("Never discard local output/work lineage through automatic downgrade.");
}
