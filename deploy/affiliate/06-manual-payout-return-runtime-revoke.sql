\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '5s';
-- Roll back the feature's runtime access only; retain tables, receipts and guards.
REVOKE SELECT, INSERT ON public."AffiliateManualPayoutReturns", public."AffiliateManualPayoutReturnAllocations"
    FROM shopixy_api_runtime_dml_v1;
COMMIT;
