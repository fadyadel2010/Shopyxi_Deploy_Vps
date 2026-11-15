# Affiliate remediation — deployed result

## Acceptance

| Scope | Result |
|---|---|
| Audited full manual-cash payout return and safe re-payment | Implemented; deployed |
| Durable-QA customer-communication exclusion | Implemented; deployed |
| Isolated financial transaction/projection/rollback/authority tests | **70 passed**, zero failed/skipped |
| Focused frontend regressions | **67 passed**; TypeScript and production build passed |
| Image parity, dependency parity, non-root, nginx, offline HIGH/CRITICAL scan | Passed |
| Production schema/grants/read-only metadata admission | Verified |
| Original Owner / normal QA Affiliate / anonymous boundaries | Retested without financial mutation |
| Full referral → checkout → collection → fulfillment → commission E2E | **Not verified by this financial fixture** |
| Real Production money execution or cash-return evidence | Not fabricated or exercised |

## Deployed identity

- API: `docker.io/shopixy/api@sha256:880c33e45a4e4a8cf236744b328706cac229e7b12284a7054ae9828b99aa34f3`, generation **47**, Ready.
- Web: `docker.io/shopixy/web@sha256:f224346ab401bfa77a59f0716f6f555499b994a22fd7980bb8423ee29735c991`, generation **15**, Ready.
- Storefront: unchanged, generation **10**.
- Source index: `a909b4464ff8c1917ba2d0381cc888a5a9e68a5330569527e9964950ca30d942`.
- Transport: verified archive import into the existing k3s node; **not a successful registry push**. Runtime wrapper-index graphs and selected config/layers match the pinned release identities.

## Production-safe behavior

Only an active same-store Owner/Admin may attest a full, actually received manual cash return to the original cashbox. Original withdrawal, Paid events, receipt, allocation and journal history remain. Compensation, terminal `Returned` state, audit and durable event are atomic. Old approvals are not reopened; a new approval is required for re-payment. Arbitrary positive adjustments are not payout authority. Provider/bank returns remain fail-closed without trusted returned-transfer evidence.

The migration adds two immutable audit tables; UPDATE/DELETE/TRUNCATE are blocked. Runtime access is SELECT/INSERT only. Production schema admission remains read-only. No new Production merchant/tenant/privileged identity was created, no stock/payment/payout evidence was fabricated, and no carrier or customer communication was invoked.

## Post-release evidence

All nine normal Affiliate tabs returned HTTP 200 and the profile date still matches its API timestamp. The preserved Ahram Owner session retained its role/tenant and financial-read authority. Anonymous and tenant-less Affiliate access to the administrative return route is rejected before any receipt is disclosed; the existing tenant guard reports `400 TENANT_REQUIRED`, while anonymous profile reads report 401. The Owner receives 404 for a nonexistent request.

Read-only snapshots match phase one, including private merchant-ledger/cashbox comparisons, QA product stock/history, and zero QA orders/commissions/payouts. Original 17 working differences/absences and the preserved patch are unchanged.

## Release incident

**A temporary API outage occurred; zero-downtime deployment is not claimed.** The first schema installer missed the separate operational schema-version admission record. The API correctly failed closed after migration, and the old-image rollback also saw that metadata mismatch. A guarded metadata installer corrected the operational record; no business data was changed and the startup guard was not bypassed. Successful API/Web recovery completed at `2026-11-15T13:04:47.847222Z`. The new metadata-admission regression covers rejection, installer correction, read-only validation and idempotent timestamp preservation.

Earlier parser/BOM failures and the failed/resumed rollout receipts are retained. An existing CSP block on an injected Cloudflare beacon remains intentional; the policy was not weakened to enable external telemetry.

See `STATUS.md`, `gate-summary.json`, `production-deployment.json`, `production-release-attempts.json`, `production-identity-verification.json`, `production-schema-verification.json`, the three post-release browser reports, and `business-state-post-release.json`.
