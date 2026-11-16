\set ON_ERROR_STOP on
BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SELECT current_database() = 'app';
SELECT NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='AffiliateAttributions' AND column_name IN ('ReferralClickId','ReferralProductId'));
SELECT NOT EXISTS (SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId"='20261115213516_AffiliateAnalyticsConversionBridge');
SELECT count(*)=0 FROM "AffiliateAttributions";
SELECT count(*)=1 FROM "SystemSchemaVersions" WHERE "Component"='postgres' AND NOT "IsDeleted";
ROLLBACK;
