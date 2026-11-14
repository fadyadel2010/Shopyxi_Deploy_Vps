using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261113130000_StockTransferDispatchReceiptAuthority")]
public sealed class StockTransferDispatchReceiptAuthority : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE "StockTransfers"
            ADD COLUMN "WorkflowVersion" bigint NOT NULL DEFAULT 1,
            ADD COLUMN "DispatchedById" uuid,
            ADD COLUMN "DispatchedAt" timestamptz,
            ADD COLUMN "ReversedById" uuid,
            ADD COLUMN "ReversedAt" timestamptz,
            ADD COLUMN "ReversalReason" varchar(1000),
            ADD COLUMN "CancellationReason" varchar(1000);
        CREATE TABLE "StockTransferEvents" (
            "Id" uuid NOT NULL, "TenantId" uuid NOT NULL, "StockTransferId" uuid NOT NULL,
            "Version" bigint NOT NULL, "Action" varchar(20) NOT NULL, "Reason" varchar(1000) NOT NULL,
            "Status" integer NOT NULL, "DetailsJson" jsonb NOT NULL,
            "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz,
            "CreatedBy" uuid, "UpdatedBy" uuid, "IsDeleted" boolean NOT NULL,
            CONSTRAINT "PK_StockTransferEvents" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_StockTransferEvents_StockTransfers_StockTransferId" FOREIGN KEY ("StockTransferId") REFERENCES "StockTransfers" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_StockTransferEvents_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE RESTRICT
        );
        CREATE INDEX "IX_StockTransferEvents_StockTransferId" ON "StockTransferEvents" ("StockTransferId");
        CREATE UNIQUE INDEX "IX_StockTransferEvents_TenantId_StockTransferId_Version" ON "StockTransferEvents" ("TenantId", "StockTransferId", "Version");
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Transfer dispatch, receipt and compensation history must be retained. Roll back application images, not business history.");
}
