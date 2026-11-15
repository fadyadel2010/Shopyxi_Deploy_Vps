# Affiliate production remediation — phase two

Phase-one artifacts in `validation/affiliate-production-acceptance` describe the already deployed ACL/profile corrections. They are not evidence that this phase is deployed.

## Safety boundary

- No new Production identities, tenants, orders, cash receipts or payouts are authorized by these tests.
- `run-isolated-tests.sh` accepts only pinned, already cached PostgreSQL 18.6 and MongoDB 8.0 images. Mongo starts with `--network=none`; the test container shares only that isolated loopback namespace. Neither has external network access, published ports or Production configuration.
- The fixture accepts only Unix socket `/tmp`, database `affiliate_return_fixture`, user `affiliate_fixture`, and its explicit isolation marker. No secret files are mounted. The database is owned by the disposable container.
- Test amounts, identities and destinations are local fictional fixtures, not Production payment evidence. No email, carrier or external payment implementation is registered.
- The financial fixture starts at an approved-commission boundary. It validates native withdrawal, approval, cash payout, return, replay and re-payment. It must **not** be described as complete referral/checkout/fulfillment E2E.
- Return publication uses the native transaction-bound SQL outbox; the test transport is never contacted. Fault injection after outbox creation must roll back the entire return.

## Build inputs

`sync-build.py` copies task-owned changes into a detached worktree based on `bd25235`, excluding all protected pre-existing differences. Frontend gates use that source and the existing dependency installations. No broad staging or Production deployment is performed by these scripts.

## Schema

`20261115112011_AffiliateManualPayoutReturn` adds only receipt/allocation tables, indexes, restrictive foreign keys, and append-only/no-truncate triggers. Down refuses to erase audit history. The fixture rehearses the actual migration operations against empty local tables and checks model/snapshot parity. Runtime grant scripts are separate, narrowly scoped, and require expected existing restricted roles and guards.

Deployment order is additive schema, runtime grants, exact operational `SystemSchemaVersions` metadata admission, then verified API/Web images. The startup guard remains read-only: the authorized schema installer must advance the operational metadata, not the API process. A rollback after any committed return must retain return-aware financial reads: rolling back to a pre-return API would hide compensation and misclassify repayments. Grant revocation is for a disabled/drained feature with no live dependent API; it is not a safe standalone rollback of a serving return-aware API. Never drop audit tables or downgrade live financial authority after returns exist.

Results are not accepted until the complete test commands pass. Logs are kept in the approved private staging directory; review/sanitize before committing any evidence.
