using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Marketplace.Journal;

namespace PointOfSale.Infrastructure.Migrations;

/// <summary>
/// Candidate-only, PostgreSQL unmanaged journal schema. No EF entity is introduced, so
/// the entity-model snapshot is intentionally unchanged. APP.5 certification and separately
/// authorized APP.6 execution are required; application startup must not apply this artifact.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261101190000_MarketplaceTransactionalSourceJournal")]
public sealed class MarketplaceTransactionalSourceJournal : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
            throw new NotSupportedException("Marketplace source capture requires PostgreSQL.");
        // The complete DDL executes in the migration transaction, including all source
        // trigger installation. A missing table or conflicting trigger aborts atomically.
        migrationBuilder.Sql(MarketplaceSourceJournal.SchemaSql.Replace("@journal_id", "gen_random_uuid()", StringComparison.Ordinal));
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Journal retirement requires independently verified consumer/snapshot/generation drain; never discard durable source history through automatic downgrade.");
}
