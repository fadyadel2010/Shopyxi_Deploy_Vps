using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PointOfSale.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AffiliateAdminManualPayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AffiliateManualPayouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AffiliateAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AffiliateUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DestinationNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DestinationType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DestinationUpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DestinationCapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DestinationFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CashboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    TreasuryAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAccountCode = table.Column<string>(type: "text", nullable: false),
                    SourceAccountName = table.Column<string>(type: "text", nullable: false),
                    AdministratorId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdministratorRole = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExecutedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Attestation = table.Column<string>(type: "text", nullable: false),
                    InternalReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    BalanceLedgerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AllocationsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CommandFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AffiliateManualPayouts", x => x.Id);
                    table.CheckConstraint("CK_AffiliateManualPayout_Amount", "\"Amount\" > 0");
                    table.ForeignKey(
                        name: "FK_AffiliateManualPayouts_AffiliateAccounts_AffiliateAccountId",
                        column: x => x.AffiliateAccountId,
                        principalTable: "AffiliateAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AffiliateManualPayouts_AffiliateBalanceLedger_BalanceLedger~",
                        column: x => x.BalanceLedgerId,
                        principalTable: "AffiliateBalanceLedger",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AffiliateManualPayouts_AffiliatePayoutRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "AffiliatePayoutRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AffiliateManualPayouts_AffiliateUsers_AffiliateUserId",
                        column: x => x.AffiliateUserId,
                        principalTable: "AffiliateUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AffiliateManualPayouts_Cashboxes_CashboxId",
                        column: x => x.CashboxId,
                        principalTable: "Cashboxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AffiliateManualPayouts_LedgerAccounts_SourceAccountId",
                        column: x => x.SourceAccountId,
                        principalTable: "LedgerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AffiliateManualPayouts_TreasuryAccounts_TreasuryAccountId",
                        column: x => x.TreasuryAccountId,
                        principalTable: "TreasuryAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AffiliateManualPayouts_Users_AdministratorId",
                        column: x => x.AdministratorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayouts_AdministratorId",
                table: "AffiliateManualPayouts",
                column: "AdministratorId");

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayouts_AffiliateAccountId",
                table: "AffiliateManualPayouts",
                column: "AffiliateAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayouts_AffiliateUserId",
                table: "AffiliateManualPayouts",
                column: "AffiliateUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayouts_BalanceLedgerId",
                table: "AffiliateManualPayouts",
                column: "BalanceLedgerId");

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayouts_CashboxId",
                table: "AffiliateManualPayouts",
                column: "CashboxId");

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayouts_RequestId",
                table: "AffiliateManualPayouts",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayouts_SourceAccountId",
                table: "AffiliateManualPayouts",
                column: "SourceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayouts_StoreTenantId_AffiliateAccountId",
                table: "AffiliateManualPayouts",
                columns: new[] { "StoreTenantId", "AffiliateAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayouts_TreasuryAccountId",
                table: "AffiliateManualPayouts",
                column: "TreasuryAccountId");
            migrationBuilder.Sql("""
                CREATE FUNCTION p512_manual_payout_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'AffiliateManualPayout records are append-only'; END $$;
                CREATE TRIGGER affiliate_manual_payout_immutable BEFORE UPDATE OR DELETE ON "AffiliateManualPayouts"
                FOR EACH ROW EXECUTE FUNCTION p512_manual_payout_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Manual payout audit history must be retained; downgrade requires an explicit retention plan.");
        }
    }
}
