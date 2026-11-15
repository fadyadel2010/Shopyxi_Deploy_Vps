# Affiliate phase-two remediation

## Outcome so far

**Production fixes deployed and read-only retested.** Additive schema, restricted runtime grants and operational schema-version metadata are applied. API generation 47 and Web generation 15 are Ready; Storefront generation 10 is unchanged. **Not accepted as complete Production financial E2E.**

- Full manual cash return requires a current same-store Owner/Admin, an unchanged reviewed original payout/cashbox, an actual returned-cash attestation, receipt reference and reason.
- The original payout, withdrawal, Paid events and journals remain. One atomic transaction appends the return receipt, exact Paid-entry allocations, positive compensating balance entry, inverse cashbox/payable journal, audit log and native durable outbox event. The request becomes terminal `Returned`; its old approval cannot be reused.
- Exact replays return the original receipt. Changed confirmation details conflict. Financial retries use a fresh scoped unit of work. A new independently approved withdrawal can repay the restored commission without reusing an old Paid idempotency key.
- Ordinary positive adjustments/bonuses cannot create payout authority. Return posting is independently checked against the complete original accounting tuple, including attempted non-cash destination substitution.
- Effective balance, SQL order/status/totals, chronological Paid history and Mongo Paid projections distinguish historical payment from current outstanding payment. Mongo paid-history writes have a monotonic CAS revision rather than relying solely on millisecond timestamps.
- Durable synthetic-QA markers exclude order-confirmation and payment WhatsApp dispatch regardless of simulator configuration. Production provider/payment guards remain enabled; no electronic refund is initiated by this cash-return command.
- The additive migration creates only two new audit tables/indexes/restrictive FKs and database UPDATE/DELETE/TRUNCATE guards. Down refuses to delete history. Runtime grants allow SELECT/INSERT only on the two new tables, with a retention-preserving access revoke script.

## Verified gates

See `gate-summary.json` and `source-index.json`.

- **70 .NET tests passed, zero failed/skipped**, including native financial transactions on disposable PostgreSQL 18.6, real MongoDB projection convergence, concurrent returns, replay, posting/outbox rollback, invalid attestation/review/actor/tenant/source, arbitrary-adjustment denial, posting-admission denial, immutable-history enforcement, and schema metadata admission/install replay.
- Actual additive migration operations and model/snapshot parity passed. Narrow runtime grant, revoke, and re-grant rehearsals passed against disposable PostgreSQL roles/tables.
- **67 frontend tests passed**, with TypeScript and production Vite build. English/Arabic JSON parses. Returned historical Paid activity is explicitly explained in both languages.
- All 17 protected original differences/absences and the original preserved patch remain unchanged. Unchanged original C# line endings were restored; the focused diff whitespace check passes.
- Release source was indexed from the isolated accepted-baseline worktree, not the unrelated original working versions.

The test containers have no external network interface or published ports. PostgreSQL uses a local Unix socket; MongoDB uses only the shared network-none loopback namespace. No Production configuration, secret files, customer messaging, real carrier, or real payment implementation is used.

## Exact acceptance boundary

The financial fixture begins with an explicitly local **approved commission**. It then uses native withdrawal, approval, manual payout, full return, replay and re-payment operations. This is not proof of a complete referral → checkout → stock reservation → collection → fulfillment → commission journey, nor proof that any Production cash was paid or returned.

API/Web release candidates passed immutable file/dependency/dist parity, non-root/nginx checks and offline HIGH/CRITICAL vulnerability gates; see `release-candidates.json`. Runtime wrapper-index graphs, selected config/layers and source labels were independently verified in `production-identity-verification.json`. All nine Affiliate tabs returned HTTP 200; the original Owner session retained Ahram authority and financial reads, and anonymous/tenant-less access remains rejected. The existing normal QA login was restored without a new identity or verification email. Financial/business snapshots match phase one exactly, with no QA orders, commissions or payouts. No real-money or customer-message operation was performed.

## Release incident and recovery

The first attempts stopped on a containerd column assumption and a BOM-prefixed SQL concatenation, without workload changes. A subsequent rollout exposed a missed schema-installation prerequisite: `__EFMigrationsHistory` advanced but the read-only `SystemSchemaVersions` contract still named the previous migration. The API startup guard correctly failed closed. **The API experienced a temporary outage, including the attempted old-image rollback; zero-downtime deployment is not claimed.**

The authorized operational schema metadata was advanced with a guarded, idempotent installer, without business-data writes or weakened startup checks. The verified API/Web candidates then became Ready at `2026-11-15T13:04:47.847222Z`, with zero restarts in the successful rollout. A new isolated regression proves mismatch rejection, metadata installation, read-only startup admission and timestamp-preserving installer replay. Deployment/read-only evidence is retained.

## GitHub delivery

Implementation commit `13bcb260535dc9f7b5ace4d91fbb4e429b3e675f` and phase-one commit `bd25235` were pushed to the existing private repository. Remote `main` was fast-forwarded from the reviewed `0dfde49` baseline without force. Both `main` and `affiliate/production-acceptance-20261115`, plus all 6,636 remote blobs, were independently verified against that implementation commit. See `github-delivery.json`; the following documentation-only commit retains this delivery receipt without changing release inputs.

Browser diagnostics also show the existing CSP refusing an injected Cloudflare analytics beacon. The policy was not weakened to enable third-party telemetry. This did not affect the nine tab reads.

## Operational limits

- Provider/bank returns remain fail-closed without trusted returned-transfer evidence.
- Legacy split-journal manual payouts require explicit reconciliation; they are not guessed into a return.
- After a real return exists, retain return-aware financial reads on rollback. A pre-return API would hide compensation; never silently revert that authority or drop audit tables.
- No full Production financial PASS is claimed.
