\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';
DO $admission$
DECLARE recorded text;
BEGIN
    IF (SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1)
        IS DISTINCT FROM '20261115112011_AffiliateManualPayoutReturn' THEN
        RAISE EXCEPTION 'Expected exact applied Affiliate return migration';
    END IF;
    IF to_regclass('public."AffiliateManualPayoutReturns"') IS NULL
        OR to_regclass('public."AffiliateManualPayoutReturnAllocations"') IS NULL THEN
        RAISE EXCEPTION 'Expected additive return audit schema';
    END IF;
    SELECT "Version" INTO STRICT recorded FROM "SystemSchemaVersions"
        WHERE "Component"='postgres' AND NOT "IsDeleted" FOR UPDATE;
    IF recorded NOT IN ('20261113140000_FinancialVoucherAuthority','20261115112011_AffiliateManualPayoutReturn') THEN
        RAISE EXCEPTION 'Unexpected operational schema metadata version';
    END IF;
    -- Operational schema-installation metadata only, never business/financial data.
    -- Production startup remains read-only and continues to reject a mismatch.
    UPDATE "SystemSchemaVersions" SET "Version"='20261115112011_AffiliateManualPayoutReturn',
        "AppliedAt"=CURRENT_TIMESTAMP, "UpdatedAt"=CURRENT_TIMESTAMP,
        "DetailsJson"=jsonb_build_object('appliedMigration','20261115112011_AffiliateManualPayoutReturn',
            'pendingMigrations','[]'::jsonb,'source','authorized-additive-affiliate-return-schema-release')::text
    WHERE "Component"='postgres' AND NOT "IsDeleted" AND "Version"='20261113140000_FinancialVoucherAuthority';
END $admission$;
COMMIT;
