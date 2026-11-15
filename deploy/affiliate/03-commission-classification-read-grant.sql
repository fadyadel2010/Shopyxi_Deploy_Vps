\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';
DO $authority$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='shopixy_api_runtime_dml_v1'
        AND NOT rolsuper AND NOT rolcreaterole AND NOT rolcreatedb AND NOT rolbypassrls)
        OR NOT pg_has_role('shopixy_api_runtime','shopixy_api_runtime_dml_v1','MEMBER') THEN
        RAISE EXCEPTION 'Expected existing restricted API DML role';
    END IF;
    IF (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public' AND c.relname IN
        ('SyntheticQaOrders','SyntheticQaEvidence','SyntheticQaCommissionPostings')
        AND c.relkind='r' AND pg_get_userbyid(c.relowner)='app') <> 3 THEN
        RAISE EXCEPTION 'Expected existing app-owned classification tables';
    END IF;
END $authority$;
-- AffiliateCommissionEligibility and AffiliatePortalReadStore must exclude
-- synthetic history from real payable balances even in Production. They select
-- ONLY OrderId from these three sources. SyntheticQaOrders already has its
-- required column grant; add only the two independently verified missing ones.
-- Do not grant table-wide reads of
-- evidence/signatures, any writes, or access to SyntheticQaRuns.
GRANT SELECT ("OrderId") ON public."SyntheticQaEvidence" TO shopixy_api_runtime_dml_v1;
GRANT SELECT ("OrderId") ON public."SyntheticQaCommissionPostings" TO shopixy_api_runtime_dml_v1;
COMMIT;
