\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

DO $authority$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles
        WHERE rolname = 'shopixy_api_runtime_dml_v1'
          AND NOT rolsuper AND NOT rolcreaterole AND NOT rolcreatedb AND NOT rolbypassrls)
       OR NOT pg_has_role('shopixy_api_runtime', 'shopixy_api_runtime_dml_v1', 'MEMBER') THEN
        RAISE EXCEPTION 'Expected existing restricted API DML authority';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public' AND c.relname = 'AffiliateEmailVerificationTokens'
          AND c.relkind = 'r' AND pg_get_userbyid(c.relowner) = 'app') THEN
        RAISE EXCEPTION 'Expected existing app-owned verification-token table';
    END IF;
    IF EXISTS (SELECT 1 FROM unnest(ARRAY['SELECT','INSERT','UPDATE']) privilege
        WHERE NOT has_table_privilege('shopixy_api_runtime', 'public."AffiliateEmailVerificationTokens"', privilege)) THEN
        RAISE EXCEPTION 'Expected existing token DML grants';
    END IF;
END $authority$;

-- The existing native registration/resend ceremony replaces outstanding tokens
-- using ExecuteDeleteAsync before inserting the new hash, even on first enrollment
-- when there are no matching rows. PostgreSQL requires DELETE for that zero-row SQL.
-- This adds ONLY the missing permission on ephemeral verification capabilities.
-- No business-data write, role creation, membership/default grants, or other table.
GRANT DELETE ON TABLE public."AffiliateEmailVerificationTokens"
    TO shopixy_api_runtime_dml_v1;
COMMIT;
