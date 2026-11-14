using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261102120000_MarketplaceExternalWriteBarrier")]
public sealed class MarketplaceExternalWriteBarrierMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") throw new NotSupportedException("PostgreSQL required.");
        migrationBuilder.Sql(MarketplaceExternalWriteBarrier.SchemaSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Never discard unresolved external operations through automatic downgrade.");
}
