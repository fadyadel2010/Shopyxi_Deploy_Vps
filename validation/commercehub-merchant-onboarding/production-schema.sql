START TRANSACTION;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '60s';
SET LOCAL ROLE app;
CREATE TABLE "CommerceHubSellerApplications" (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "ReferenceNumber" character varying(40) NOT NULL,
    "Revision" integer NOT NULL,
    "Status" integer NOT NULL,
    "StoreDisplayName" character varying(160) NOT NULL,
    "BusinessCategory" character varying(120) NOT NULL,
    "BusinessDescription" character varying(1000) NOT NULL,
    "ContactName" character varying(160) NOT NULL,
    "ContactPhone" character varying(40) NOT NULL,
    "ContactEmail" character varying(320) NOT NULL,
    "ProductCategoriesJson" jsonb NOT NULL,
    "StorefrontUrl" character varying(500),
    "SubmittedByUserId" uuid NOT NULL,
    "SubmittedAtUtc" timestamp with time zone NOT NULL,
    "ReviewedBySuperAdminId" uuid,
    "ReviewerIdentity" character varying(320),
    "ReviewedAtUtc" timestamp with time zone,
    "RejectionReason" character varying(1000),
    "ConcurrencyToken" uuid NOT NULL,
    CONSTRAINT "PK_CommerceHubSellerApplications" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_CommerceHubSellerApplications_Revision" CHECK ("Revision" > 0),
    CONSTRAINT "CK_CommerceHubSellerApplications_ReviewState" CHECK (("Status" = 1 AND "ReviewedAtUtc" IS NULL AND "ReviewedBySuperAdminId" IS NULL) OR ("Status" IN (2, 3) AND "ReviewedAtUtc" IS NOT NULL AND "ReviewedBySuperAdminId" IS NOT NULL)),
    CONSTRAINT "CK_CommerceHubSellerApplications_RejectionReason" CHECK ("Status" <> 3 OR ("RejectionReason" IS NOT NULL AND length(trim("RejectionReason")) >= 10)),
    CONSTRAINT "FK_CommerceHubSellerApplications_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE RESTRICT
);

CREATE UNIQUE INDEX "IX_CommerceHubSellerApplications_ReferenceNumber" ON "CommerceHubSellerApplications" ("ReferenceNumber");

CREATE INDEX "IX_CommerceHubSellerApplications_Status_SubmittedAtUtc" ON "CommerceHubSellerApplications" ("Status", "SubmittedAtUtc");

CREATE UNIQUE INDEX "IX_CommerceHubSellerApplications_TenantId_Revision" ON "CommerceHubSellerApplications" ("TenantId", "Revision");

CREATE UNIQUE INDEX "UX_CommerceHubSellerApplications_OnePendingPerTenant" ON "CommerceHubSellerApplications" ("TenantId") WHERE "Status" = 1;

GRANT SELECT, INSERT, UPDATE ON TABLE "CommerceHubSellerApplications" TO shopixy_api_runtime_dml_v1;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261116100000_CommerceHubSellerApplications', '10.0.15');

UPDATE "SystemSchemaVersions"
SET "Version" = '20261116100000_CommerceHubSellerApplications'
WHERE "Component" = 'postgres' AND NOT "IsDeleted";

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM "SystemSchemaVersions"
        WHERE "Component" = 'postgres'
          AND NOT "IsDeleted"
          AND "Version" = '20261116100000_CommerceHubSellerApplications')
    THEN RAISE EXCEPTION 'postgres SystemSchemaVersions authority missing';
    END IF;
END $$;

COMMIT;
