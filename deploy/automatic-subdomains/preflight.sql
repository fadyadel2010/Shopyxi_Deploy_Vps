\set ON_ERROR_STOP on

BEGIN TRANSACTION READ ONLY;

-- The additive migration must be applied only after its known predecessor.
SELECT 1 / CASE WHEN EXISTS (
    SELECT 1
    FROM "__EFMigrationsHistory"
    WHERE "MigrationId" = '20261105140000_MarketplaceGenerationGateway'
) THEN 1 ELSE 0 END AS predecessor_present;

SELECT 1 / CASE WHEN (
    SELECT MAX("MigrationId") FROM "__EFMigrationsHistory"
) IN ('20261105140000_MarketplaceGenerationGateway',
      '20261111160000_AutomatedTenantProvisioning')
THEN 1 ELSE 0 END AS exact_release_lineage;

-- Fail, rather than merely report, if the new case-insensitive uniqueness
-- authority cannot be installed safely.
SELECT 1 / CASE WHEN NOT EXISTS (
    SELECT 1
    FROM "Tenants"
    WHERE "Slug" IS NOT NULL
    GROUP BY LOWER("Slug")
    HAVING COUNT(*) > 1
) THEN 1 ELSE 0 END AS no_case_insensitive_slug_duplicates;

SELECT LOWER("Slug") AS normalized_slug, COUNT(*) AS duplicate_count
FROM "Tenants"
WHERE "Slug" IS NOT NULL
GROUP BY LOWER("Slug")
HAVING COUNT(*) > 1;

-- Canonical acceptance authority must remain active and published.
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
) = 1 THEN 1 ELSE 0 END AS canonical_tenant_ready;

SELECT
    t."Id" AS tenant_id,
    t."Slug" AS slug,
    t."Status" AS tenant_status,
    t."IsActive" AS tenant_is_active,
    t."DeletedAtUtc" AS tenant_deleted_at_utc,
    s."StorefrontStatus" AS storefront_status
FROM "Tenants" t
LEFT JOIN "StoreConfigs" s ON s."TenantId" = t."Id"
WHERE t."Id" = 'cdbe20e0-f84a-4416-b20c-044e6bcf04d0'
  AND LOWER(t."Slug") = 'ahram';

COMMIT;
