\set ON_ERROR_STOP on
BEGIN READ ONLY;
SELECT EXISTS (SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId"='20261115112011_AffiliateManualPayoutReturn');
SELECT to_regclass('public."AffiliateManualPayoutReturns"') IS NOT NULL;
SELECT to_regclass('public."AffiliateManualPayoutReturnAllocations"') IS NOT NULL;
SELECT count(*)=1 FROM "SystemSchemaVersions" WHERE "Component"='postgres' AND NOT "IsDeleted"
 AND "Version"='20261115112011_AffiliateManualPayoutReturn';
SELECT count(*)=4 FROM pg_trigger WHERE NOT tgisinternal AND tgenabled='O' AND tgname IN
 ('affiliate_manual_return_immutable','affiliate_manual_return_no_truncate','affiliate_manual_return_allocation_immutable','affiliate_manual_return_allocation_no_truncate');
SELECT bool_and(has_table_privilege('shopixy_api_runtime', format('public.%I', name), 'SELECT') AND
 has_table_privilege('shopixy_api_runtime', format('public.%I', name), 'INSERT') AND
 NOT has_table_privilege('shopixy_api_runtime', format('public.%I', name), 'UPDATE,DELETE,TRUNCATE'))
FROM (VALUES ('AffiliateManualPayoutReturns'),('AffiliateManualPayoutReturnAllocations')) t(name);
SELECT count(*) FROM "AffiliateManualPayoutReturns";
SELECT count(*) FROM "AffiliateManualPayoutReturnAllocations";
COMMIT;
