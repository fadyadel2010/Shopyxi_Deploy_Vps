\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '60s';
SET LOCAL ROLE app;
ALTER TABLE "AffiliateAttributions" ADD COLUMN "ReferralClickId" uuid NULL;
ALTER TABLE "AffiliateAttributions" ADD COLUMN "ReferralProductId" uuid NULL;
CREATE UNIQUE INDEX "UQ_AffiliateAttributions_Tenant_Click"
  ON "AffiliateAttributions" ("TenantId", "ReferralClickId")
  WHERE "ReferralClickId" IS NOT NULL;
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261115213516_AffiliateAnalyticsConversionBridge', '10.0.15');
-- Production/private startup admission is read-only. Prepare its exact metadata
-- without changing the existing application timestamp/audit columns.
UPDATE "SystemSchemaVersions"
SET "Version" = '20261115213516_AffiliateAnalyticsConversionBridge'
WHERE "Component" = 'postgres' AND NOT "IsDeleted";
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM "SystemSchemaVersions" WHERE "Component"='postgres' AND NOT "IsDeleted" AND "Version"='20261115213516_AffiliateAnalyticsConversionBridge')
  THEN RAISE EXCEPTION 'postgres SystemSchemaVersions authority missing'; END IF;
END $$;
COMMIT;
