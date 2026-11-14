using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Migrations;

/// <summary>Additive count documents only. No account seeds or historical stock/journal rewrites.</summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261113120000_StockCountSessionAuthority")]
public sealed class StockCountSessionAuthority : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE SEQUENCE IF NOT EXISTS public.stock_count_number_seq AS bigint START WITH 1 INCREMENT BY 1 NO CYCLE;
        CREATE TABLE "StockCountSessions" (
            "Id" uuid NOT NULL, "TenantId" uuid NOT NULL, "Number" varchar(60) NOT NULL,
            "RequestFingerprint" varchar(64) NOT NULL, "WarehouseId" uuid NOT NULL, "CountDate" date NOT NULL,
            "SnapshotAtUtc" timestamptz NOT NULL, "Status" integer NOT NULL, "Version" bigint NOT NULL,
            "Reason" varchar(1000) NOT NULL, "CountedBy" uuid, "CountedAtUtc" timestamptz,
            "PostedBy" uuid, "PostedAtUtc" timestamptz, "PostingReason" varchar(1000), "VarianceAccountId" uuid,
            "JournalId" uuid, "ReversalJournalId" uuid, "ReversedBy" uuid, "ReversedAtUtc" timestamptz,
            "ReversalReason" varchar(1000), "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz,
            "CreatedBy" uuid, "UpdatedBy" uuid, "IsDeleted" boolean NOT NULL,
            CONSTRAINT "PK_StockCountSessions" PRIMARY KEY ("Id"),
            CONSTRAINT "AK_StockCountSessions_TenantId_Id" UNIQUE ("TenantId", "Id"),
            CONSTRAINT "CK_StockCountSession_Status" CHECK ("Status" IN (0,1,2,3)),
            CONSTRAINT "CK_StockCountSession_Retained" CHECK (NOT "IsDeleted"),
            CONSTRAINT "FK_StockCountSessions_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_StockCountSessions_Warehouses_WarehouseId" FOREIGN KEY ("WarehouseId") REFERENCES "Warehouses" ("Id") ON DELETE RESTRICT
        );
        CREATE TABLE "StockCountLines" (
            "Id" uuid NOT NULL, "TenantId" uuid NOT NULL, "StockCountSessionId" uuid NOT NULL,
            "ProductId" uuid NOT NULL, "VariantId" uuid NOT NULL, "ProductName" varchar(500) NOT NULL, "Sku" varchar(200) NOT NULL,
            "SystemQuantity" numeric(18,4) NOT NULL, "ReservedSnapshot" numeric(18,4) NOT NULL,
            "DamagedSnapshot" numeric(18,4) NOT NULL, "StockVersion" bigint NOT NULL, "UnitCost" numeric(18,4) NOT NULL,
            "CountedQuantity" numeric(18,4), "VarianceReason" varchar(1000), "CountedBy" uuid, "CountedAtUtc" timestamptz,
            "PostedValue" numeric(18,4) NOT NULL, "MovementId" uuid, "ReversalMovementId" uuid,
            "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz, "CreatedBy" uuid, "UpdatedBy" uuid, "IsDeleted" boolean NOT NULL,
            CONSTRAINT "PK_StockCountLines" PRIMARY KEY ("Id"),
            CONSTRAINT "CK_StockCountLine_Count" CHECK ("CountedQuantity" IS NULL OR "CountedQuantity" >= 0),
            CONSTRAINT "CK_StockCountLine_Cost" CHECK ("UnitCost" >= 0),
            CONSTRAINT "CK_StockCountLine_Retained" CHECK (NOT "IsDeleted"),
            CONSTRAINT "FK_StockCountLine_Session" FOREIGN KEY ("TenantId", "StockCountSessionId")
                REFERENCES "StockCountSessions" ("TenantId", "Id") ON DELETE RESTRICT
        );
        CREATE TABLE "StockCountEvents" (
            "Id" uuid NOT NULL, "TenantId" uuid NOT NULL, "StockCountSessionId" uuid NOT NULL,
            "Version" bigint NOT NULL, "Action" varchar(20) NOT NULL, "Reason" varchar(1000) NOT NULL, "Status" integer NOT NULL,
            "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz, "CreatedBy" uuid, "UpdatedBy" uuid, "IsDeleted" boolean NOT NULL,
            CONSTRAINT "PK_StockCountEvents" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_StockCountEvent_Session" FOREIGN KEY ("TenantId", "StockCountSessionId")
                REFERENCES "StockCountSessions" ("TenantId", "Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_StockCountSessions_TenantId_Number" ON "StockCountSessions" ("TenantId", "Number");
        CREATE INDEX "IX_StockCountSessions_TenantId_WarehouseId_Status" ON "StockCountSessions" ("TenantId", "WarehouseId", "Status");
        CREATE INDEX "IX_StockCountSessions_WarehouseId" ON "StockCountSessions" ("WarehouseId");
        CREATE UNIQUE INDEX "IX_StockCountLines_TenantId_StockCountSessionId_VariantId" ON "StockCountLines" ("TenantId", "StockCountSessionId", "VariantId");
        CREATE UNIQUE INDEX "IX_StockCountEvents_TenantId_StockCountSessionId_Version" ON "StockCountEvents" ("TenantId", "StockCountSessionId", "Version");
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Stock count history is retained. Roll back application images, not accepted business documents.");
}
