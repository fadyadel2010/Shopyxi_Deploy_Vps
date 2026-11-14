\set ON_ERROR_STOP on

BEGIN TRANSACTION READ ONLY;

SELECT 1 / CASE WHEN EXISTS (
    SELECT 1
    FROM "__EFMigrationsHistory"
    WHERE "MigrationId" = '20261111160000_AutomatedTenantProvisioning'
) THEN 1 ELSE 0 END AS migration_applied;

SELECT 1 / CASE WHEN
    has_table_privilege('shopixy_api_runtime', 'public."TenantProvisioningOperations"', 'SELECT')
    AND has_table_privilege('shopixy_api_runtime', 'public."TenantProvisioningOperations"', 'INSERT')
    AND has_table_privilege('shopixy_api_runtime', 'public."TenantProvisioningOperations"', 'UPDATE')
    AND NOT has_table_privilege('shopixy_api_runtime', 'public."TenantProvisioningOperations"', 'DELETE')
    AND NOT has_table_privilege('shopixy_api_runtime', 'public."TenantProvisioningOperations"', 'TRUNCATE')
THEN 1 ELSE 0 END AS minimum_runtime_table_authority;

SELECT 1 / CASE WHEN (
    SELECT COUNT(*) FROM "SystemSchemaVersions"
    WHERE "Component" = 'postgres'
      AND "Version" = '20261111160000_AutomatedTenantProvisioning'
      AND NOT "IsDeleted"
) = 1 THEN 1 ELSE 0 END AS runtime_schema_metadata_current;

SELECT 1 / CASE WHEN to_regclass('"TenantProvisioningOperations"') IS NOT NULL
    THEN 1 ELSE 0 END AS provisioning_authority_present;

SELECT 1 / CASE WHEN EXISTS (
    SELECT 1
    FROM pg_class index_relation
    JOIN pg_index index_state ON index_state.indexrelid = index_relation.oid
    WHERE index_relation.relname = 'UX_Tenants_Slug_CaseInsensitive'
      AND index_state.indisunique
      AND index_state.indisvalid
      AND index_state.indisready
) THEN 1 ELSE 0 END AS slug_index_ready;

SELECT 1 / CASE WHEN NOT EXISTS (
    SELECT 1
    FROM "Tenants"
    WHERE "Slug" IS NOT NULL
    GROUP BY LOWER("Slug")
    HAVING COUNT(*) > 1
) THEN 1 ELSE 0 END AS no_case_insensitive_slug_duplicates;

SELECT 1 / CASE WHEN (
    SELECT COUNT(*)
    FROM "Tenants" t
    JOIN "StoreConfigs" s ON s."TenantId" = t."Id"
    JOIN "Users" u ON u."TenantId" = t."Id"
    WHERE t."Id" = 'cdbe20e0-f84a-4416-b20c-044e6bcf04d0'
      AND LOWER(t."Slug") = 'ahram'
      AND t."Status" = 1
      AND t."IsActive"
      AND t."DeletedAtUtc" IS NULL
      AND s."StorefrontStatus" = 'live'
      AND s."Id" = '66159d8f-81af-4175-9c05-a981d6f5d3c8'
      AND u."Id" = '67778c97-586e-4cc1-99ba-b8e0194523ad'
      AND u."Role" = 0 AND u."IsActive" AND NOT u."IsDeleted"
      AND u."EmailVerificationStatus" = 1
) = 1 THEN 1 ELSE 0 END AS canonical_tenant_preserved;

SELECT "State", COUNT(*) AS operation_count
FROM "TenantProvisioningOperations"
GROUP BY "State"
ORDER BY "State";

COMMIT;
