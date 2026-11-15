\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';
DO $authority$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'shopixy_api_runtime_dml_v1'
        AND NOT rolsuper AND NOT rolcreaterole AND NOT rolcreatedb AND NOT rolbypassrls)
        OR NOT pg_has_role('shopixy_api_runtime', 'shopixy_api_runtime_dml_v1', 'MEMBER') THEN
        RAISE EXCEPTION 'Expected existing restricted API DML role';
    END IF;
    IF (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public' AND c.relname IN
        ('AffiliateManualPayoutReturns', 'AffiliateManualPayoutReturnAllocations')
        AND c.relkind = 'r' AND pg_get_userbyid(c.relowner) = 'app') <> 2 THEN
        RAISE EXCEPTION 'Expected migrated app-owned manual return audit tables';
    END IF;
    IF (SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal AND tgenabled = 'O' AND tgname IN
        ('affiliate_manual_return_immutable', 'affiliate_manual_return_no_truncate',
         'affiliate_manual_return_allocation_immutable', 'affiliate_manual_return_allocation_no_truncate')) <> 4 THEN
        RAISE EXCEPTION 'Expected enabled append-only and no-truncate audit guards';
    END IF;
END $authority$;
-- New audit records are readable/appendable only. Never grant mutation of history.
REVOKE UPDATE, DELETE, TRUNCATE ON public."AffiliateManualPayoutReturns", public."AffiliateManualPayoutReturnAllocations"
    FROM shopixy_api_runtime_dml_v1, shopixy_api_runtime, PUBLIC;
GRANT SELECT, INSERT ON public."AffiliateManualPayoutReturns", public."AffiliateManualPayoutReturnAllocations"
    TO shopixy_api_runtime_dml_v1;
COMMIT;
