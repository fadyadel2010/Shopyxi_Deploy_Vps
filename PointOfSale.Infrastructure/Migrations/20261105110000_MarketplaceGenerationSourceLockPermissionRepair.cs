using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Migrations;

/// <summary>Also repairs databases where the earlier lifecycle migration was already applied.</summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261105110000_MarketplaceGenerationSourceLockPermissionRepair")]
public sealed class MarketplaceGenerationSourceLockPermissionRepair : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE OR REPLACE FUNCTION marketplace_generation.lock_source_revision(expected_journal uuid) RETURNS bigint
        LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
            SELECT revision FROM marketplace_journal.gate WHERE singleton AND journal_id=expected_journal FOR SHARE
        $$;
        REVOKE ALL ON FUNCTION marketplace_generation.lock_source_revision(uuid) FROM PUBLIC;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("The restricted source-lock permission boundary must not be removed.");
}
