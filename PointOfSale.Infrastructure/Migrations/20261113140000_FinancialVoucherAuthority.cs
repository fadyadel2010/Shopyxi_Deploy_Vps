using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261113140000_FinancialVoucherAuthority")]
public sealed class FinancialVoucherAuthority : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE SEQUENCE IF NOT EXISTS public.financial_voucher_number_seq AS bigint START WITH 1 INCREMENT BY 1 NO CYCLE;
        CREATE TABLE "FinancialVouchers" (
            "Id" uuid NOT NULL, "TenantId" uuid NOT NULL, "Number" varchar(60) NOT NULL, "RequestFingerprint" varchar(64) NOT NULL,
            "Kind" integer NOT NULL, "SourcePaymentId" uuid NOT NULL, "PartyId" uuid NOT NULL, "PartyName" varchar(500) NOT NULL,
            "Amount" numeric(18,4) NOT NULL, "Currency" varchar(3) NOT NULL, "PaymentDate" timestamptz NOT NULL,
            "JournalId" uuid NOT NULL, "Reason" varchar(1000) NOT NULL, "Version" bigint NOT NULL, "Status" integer NOT NULL,
            "ReversalJournalId" uuid, "ReversedBy" uuid, "ReversedAtUtc" timestamptz, "ReversalReason" varchar(1000),
            "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz, "CreatedBy" uuid, "UpdatedBy" uuid, "IsDeleted" boolean NOT NULL,
            CONSTRAINT "PK_FinancialVouchers" PRIMARY KEY ("Id"),
            CONSTRAINT "AK_FinancialVouchers_TenantId_Id" UNIQUE ("TenantId", "Id"),
            CONSTRAINT "CK_FinancialVoucher_Amount" CHECK ("Amount">0),
            CONSTRAINT "CK_FinancialVoucher_Kind" CHECK ("Kind" IN (0,1)),
            CONSTRAINT "CK_FinancialVoucher_Status" CHECK ("Status" IN (0,1)),
            CONSTRAINT "CK_FinancialVoucher_Retained" CHECK (NOT "IsDeleted"),
            CONSTRAINT "FK_FinancialVouchers_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_FinancialVouchers_LedgerJournalEntries_JournalId" FOREIGN KEY ("JournalId") REFERENCES "LedgerJournalEntries" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_FinancialVouchers_LedgerJournalEntries_ReversalJournalId" FOREIGN KEY ("ReversalJournalId") REFERENCES "LedgerJournalEntries" ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_FinancialVouchers_TenantId_Number" ON "FinancialVouchers" ("TenantId", "Number");
        CREATE UNIQUE INDEX "IX_FinancialVouchers_TenantId_Kind_SourcePaymentId" ON "FinancialVouchers" ("TenantId", "Kind", "SourcePaymentId");
        CREATE INDEX "IX_FinancialVouchers_JournalId" ON "FinancialVouchers" ("JournalId");
        CREATE INDEX "IX_FinancialVouchers_ReversalJournalId" ON "FinancialVouchers" ("ReversalJournalId");
        CREATE TABLE "FinancialVoucherEvents" (
            "Id" uuid NOT NULL, "TenantId" uuid NOT NULL, "FinancialVoucherId" uuid NOT NULL, "Version" bigint NOT NULL,
            "Action" varchar(20) NOT NULL, "Reason" varchar(1000) NOT NULL, "JournalId" uuid NOT NULL,
            "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz, "CreatedBy" uuid, "UpdatedBy" uuid, "IsDeleted" boolean NOT NULL,
            CONSTRAINT "PK_FinancialVoucherEvents" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_FinancialVoucherEvent_Voucher" FOREIGN KEY ("TenantId", "FinancialVoucherId")
                REFERENCES "FinancialVouchers" ("TenantId", "Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_FinancialVoucherEvents_TenantId_FinancialVoucherId_Version" ON "FinancialVoucherEvents" ("TenantId", "FinancialVoucherId", "Version");
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Issued financial vouchers and compensations must be retained.");
}
