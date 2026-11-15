\set ON_ERROR_STOP on
BEGIN;
REVOKE SELECT ("OrderId") ON public."SyntheticQaEvidence" FROM shopixy_api_runtime_dml_v1;
REVOKE SELECT ("OrderId") ON public."SyntheticQaCommissionPostings" FROM shopixy_api_runtime_dml_v1;
COMMIT;
