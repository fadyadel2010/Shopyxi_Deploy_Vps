\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

DO $authority$
DECLARE affected integer;
BEGIN
    IF (SELECT MAX("MigrationId") FROM "__EFMigrationsHistory")
       <> '20261111160000_AutomatedTenantProvisioning' THEN
        RAISE EXCEPTION 'Latest migration authority does not match this release';
    END IF;

    UPDATE "SystemSchemaVersions"
       SET "Version" = '20261111160000_AutomatedTenantProvisioning',
           "AppliedAt" = now(),
           "UpdatedAt" = now()
     WHERE "Component" = 'postgres'
       AND "Version" = '20261105140000_MarketplaceGenerationGateway'
       AND NOT "IsDeleted";
    GET DIAGNOSTICS affected = ROW_COUNT;

    IF affected = 0 AND EXISTS (
        SELECT 1 FROM "SystemSchemaVersions"
        WHERE "Component" = 'postgres'
          AND "Version" = '20261111160000_AutomatedTenantProvisioning'
          AND NOT "IsDeleted"
    ) THEN
        RETURN; -- Idempotent repeat after successful advancement.
    END IF;
    IF affected <> 1 THEN
        RAISE EXCEPTION 'Expected exactly one predecessor schema metadata row, updated %', affected;
    END IF;
END $authority$;

COMMIT;
