using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PointOfSale.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AffiliateAnalyticsConversionBridge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReferralClickId",
                table: "AffiliateAttributions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReferralProductId",
                table: "AffiliateAttributions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UQ_AffiliateAttributions_Tenant_Click",
                table: "AffiliateAttributions",
                columns: new[] { "TenantId", "ReferralClickId" },
                unique: true,
                filter: "\"ReferralClickId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_AffiliateAttributions_Tenant_Click",
                table: "AffiliateAttributions");

            migrationBuilder.DropColumn(
                name: "ReferralClickId",
                table: "AffiliateAttributions");

            migrationBuilder.DropColumn(
                name: "ReferralProductId",
                table: "AffiliateAttributions");
        }
    }
}
