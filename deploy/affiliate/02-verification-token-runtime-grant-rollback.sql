\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';
-- Restore the independently captured pre-release ACL. Does not delete any data.
-- Rolling this back reintroduces the registration/resend permission failure.
REVOKE DELETE ON TABLE public."AffiliateEmailVerificationTokens"
    FROM shopixy_api_runtime_dml_v1;
COMMIT;
