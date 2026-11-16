using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261116100000_CommerceHubSellerApplications")]
public sealed class CommerceHubSellerApplications : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CommerceHubSellerApplications",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ReferenceNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Revision = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                StoreDisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                BusinessCategory = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                BusinessDescription = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                ContactName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                ContactPhone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ContactEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                ProductCategoriesJson = table.Column<string>(type: "jsonb", nullable: false),
                StorefrontUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                SubmittedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ReviewedBySuperAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                ReviewerIdentity = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                RejectionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CommerceHubSellerApplications", x => x.Id);
                table.ForeignKey(
                    name: "FK_CommerceHubSellerApplications_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.CheckConstraint("CK_CommerceHubSellerApplications_Revision", "\"Revision\" > 0");
                table.CheckConstraint(
                    "CK_CommerceHubSellerApplications_ReviewState",
                    "(\"Status\" = 1 AND \"ReviewedAtUtc\" IS NULL AND \"ReviewedBySuperAdminId\" IS NULL) OR (\"Status\" IN (2, 3) AND \"ReviewedAtUtc\" IS NOT NULL AND \"ReviewedBySuperAdminId\" IS NOT NULL)");
                table.CheckConstraint(
                    "CK_CommerceHubSellerApplications_RejectionReason",
                    "\"Status\" <> 3 OR (\"RejectionReason\" IS NOT NULL AND length(trim(\"RejectionReason\")) >= 10)");
            });

        migrationBuilder.CreateIndex(
            name: "IX_CommerceHubSellerApplications_ReferenceNumber",
            table: "CommerceHubSellerApplications",
            column: "ReferenceNumber",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_CommerceHubSellerApplications_Status_SubmittedAtUtc",
            table: "CommerceHubSellerApplications",
            columns: new[] { "Status", "SubmittedAtUtc" });
        migrationBuilder.CreateIndex(
            name: "IX_CommerceHubSellerApplications_TenantId_Revision",
            table: "CommerceHubSellerApplications",
            columns: new[] { "TenantId", "Revision" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "UX_CommerceHubSellerApplications_OnePendingPerTenant",
            table: "CommerceHubSellerApplications",
            column: "TenantId",
            unique: true,
            filter: "\"Status\" = 1");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "CommerceHubSellerApplications");
}
