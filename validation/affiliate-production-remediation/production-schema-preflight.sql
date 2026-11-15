\set ON_ERROR_STOP on
BEGIN READ ONLY;
SELECT current_database() = 'app' AS expected_database;
SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 3;
SELECT to_regclass('public."AffiliateManualPayoutReturns"') IS NULL AS returns_absent,
       to_regclass('public."AffiliateManualPayoutReturnAllocations"') IS NULL AS allocations_absent;
SELECT count(*) = 2 AS expected_runtime_roles
FROM pg_roles WHERE rolname IN ('shopixy_api_runtime', 'shopixy_api_runtime_dml_v1')
  AND NOT rolsuper AND NOT rolcreaterole AND NOT rolcreatedb AND NOT rolbypassrls;
SELECT pg_has_role('shopixy_api_runtime', 'shopixy_api_runtime_dml_v1', 'MEMBER') AS expected_membership;
SELECT count(*) = 0 AS no_existing_return_rows
FROM "AffiliatePayoutRequests" WHERE "Status" = 6;
COMMIT;
