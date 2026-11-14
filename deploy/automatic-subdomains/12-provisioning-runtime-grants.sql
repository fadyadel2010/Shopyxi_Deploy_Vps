\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

DO $authority$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_roles
        WHERE rolname = 'shopixy_api_runtime'
          AND NOT rolsuper AND NOT rolcreaterole AND NOT rolcreatedb
          AND NOT rolbypassrls
    ) THEN
        RAISE EXCEPTION 'Expected restricted existing API runtime role';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public'
          AND c.relname = 'TenantProvisioningOperations'
          AND c.relkind = 'r'
          AND pg_get_userbyid(c.relowner) = 'postgres'
    ) THEN
        RAISE EXCEPTION 'Expected separately provisioned PostgreSQL-owned operation table';
    END IF;
END $authority$;

-- Runtime converges durable work but never deletes its history or owns schema.
-- Do not broaden default privileges, existing tables, roles or memberships.
GRANT SELECT, INSERT, UPDATE
    ON TABLE public."TenantProvisioningOperations"
    TO shopixy_api_runtime;

COMMIT;
