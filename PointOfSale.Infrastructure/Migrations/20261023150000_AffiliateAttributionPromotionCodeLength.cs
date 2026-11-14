using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Migrations;

/// <summary>Widen only the immutable referral snapshot to the existing V2 link contract.
/// No financial row, identity, attribution value or historical lifecycle is rewritten.</summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261023150000_AffiliateAttributionPromotionCodeLength")]
public sealed class AffiliateAttributionPromotionCodeLength : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AlterColumn<string>(
        name: "ReferralCode", table: "AffiliateAttributions", type: "character varying(16)", maxLength: 16, nullable: false,
        oldClrType: typeof(string), oldType: "character varying(12)", oldMaxLength: 12);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Never truncate an admitted historical token during downgrade.
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM "AffiliateAttributions" WHERE length("ReferralCode") > 12) THEN
                    RAISE EXCEPTION 'Cannot narrow immutable affiliate attribution containing V2 codes longer than 12';
                END IF;
            END $$;
            """);
        migrationBuilder.AlterColumn<string>(name: "ReferralCode", table: "AffiliateAttributions", type: "character varying(12)",
            maxLength: 12, nullable: false, oldClrType: typeof(string), oldType: "character varying(16)", oldMaxLength: 16);
    }
}
