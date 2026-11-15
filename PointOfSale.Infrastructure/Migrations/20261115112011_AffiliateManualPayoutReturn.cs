using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PointOfSale.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AffiliateManualPayoutReturn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AffiliateManualPayoutReturns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AffiliateAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManualPayoutId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalJournalId = table.Column<Guid>(type: "uuid", nullable: false),
                    BalanceLedgerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdministratorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Attestation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ReceiptReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CommandFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AffiliateManualPayoutReturns", x => x.Id);
                    table.CheckConstraint("CK_AffiliatePayoutReturn_Amount", "\"Amount\" > 0");
                    table.ForeignKey(
                        name: "FK_AffiliateManualPayoutReturns_AffiliateAccounts_AffiliateAcc~",
                        column: x => x.AffiliateAccountId,
                        principalTable: "AffiliateAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AffiliateManualPayoutReturns_AffiliateBalanceLedger_Balance~",
                        column: x => x.BalanceLedgerId,
                        principalTable: "AffiliateBalanceLedger",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AffiliateManualPayoutReturns_AffiliateManualPayouts_ManualP~",
                        column: x => x.ManualPayoutId,
                        principalTable: "AffiliateManualPayouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AffiliateManualPayoutReturns_AffiliatePayoutRequests_Reques~",
                        column: x => x.RequestId,
                        principalTable: "AffiliatePayoutRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AffiliateManualPayoutReturns_LedgerJournalEntries_OriginalJ~",
                        column: x => x.OriginalJournalId,
                        principalTable: "LedgerJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AffiliateManualPayoutReturns_Users_AdministratorId",
                        column: x => x.AdministratorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AffiliateManualPayoutReturnAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaidEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AffiliateManualPayoutReturnAllocations", x => x.Id);
                    table.CheckConstraint("CK_AffiliatePayoutReturnAllocation_Amount", "\"Amount\" > 0");
                    table.ForeignKey(
                        name: "FK_AffiliateManualPayoutReturnAllocations_AffiliateCommissionL~",
                        column: x => x.PaidEntryId,
                        principalTable: "AffiliateCommissionLedger",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AffiliateManualPayoutReturnAllocations_AffiliateManualPayou~",
                        column: x => x.ReturnId,
                        principalTable: "AffiliateManualPayoutReturns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayoutReturnAllocations_PaidEntryId",
                table: "AffiliateManualPayoutReturnAllocations",
                column: "PaidEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayoutReturnAllocations_ReturnId",
                table: "AffiliateManualPayoutReturnAllocations",
                column: "ReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayoutReturns_AdministratorId",
                table: "AffiliateManualPayoutReturns",
                column: "AdministratorId");

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayoutReturns_AffiliateAccountId",
                table: "AffiliateManualPayoutReturns",
                column: "AffiliateAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayoutReturns_BalanceLedgerId",
                table: "AffiliateManualPayoutReturns",
                column: "BalanceLedgerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayoutReturns_ManualPayoutId",
                table: "AffiliateManualPayoutReturns",
                column: "ManualPayoutId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayoutReturns_OriginalJournalId",
                table: "AffiliateManualPayoutReturns",
                column: "OriginalJournalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayoutReturns_RequestId",
                table: "AffiliateManualPayoutReturns",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AffiliateManualPayoutReturns_StoreTenantId_AffiliateAccount~",
                table: "AffiliateManualPayoutReturns",
                columns: new[] { "StoreTenantId", "AffiliateAccountId" });
            migrationBuilder.Sql("""
                CREATE FUNCTION affiliate_manual_return_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Affiliate manual payout return history is append-only'; END $$;
                CREATE TRIGGER affiliate_manual_return_immutable
                  BEFORE UPDATE OR DELETE ON "AffiliateManualPayoutReturns"
                  FOR EACH ROW EXECUTE FUNCTION affiliate_manual_return_immutable();
                CREATE TRIGGER affiliate_manual_return_no_truncate
                  BEFORE TRUNCATE ON "AffiliateManualPayoutReturns"
                  FOR EACH STATEMENT EXECUTE FUNCTION affiliate_manual_return_immutable();
                CREATE TRIGGER affiliate_manual_return_allocation_immutable
                  BEFORE UPDATE OR DELETE ON "AffiliateManualPayoutReturnAllocations"
                  FOR EACH ROW EXECUTE FUNCTION affiliate_manual_return_immutable();
                CREATE TRIGGER affiliate_manual_return_allocation_no_truncate
                  BEFORE TRUNCATE ON "AffiliateManualPayoutReturnAllocations"
                  FOR EACH STATEMENT EXECUTE FUNCTION affiliate_manual_return_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Manual payout return audit history must be retained; downgrade requires an explicit retention plan.");
        }
    }
}
