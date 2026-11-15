# Affiliate full lifecycle — 2026-11-15

## Result

**PASS — isolated forward lifecycle, payout-return compensation, full post-settlement refund, inventory and financial reconciliation.** This is not evidence of physical delivery, physical cash, electronic transfer, or Production business execution.

The journey reproduced and fixed one API defect: provisioning a dedicated Affiliate payout cashbox moved Treasury `IsDefault` away from canonical Main Cashbox `1010`, stranding Manual COD remittance with `COD_MANUAL_CASHBOX_REQUIRED`. The authority now resolves the single active, non-deleted Cashbox Treasury mapping to ledger code exactly `1010`; tenant/account/activity/deletion/currency checks remain fail-closed. No schema, Web, or Storefront change is required.

## Safety and accepted baseline

- Source baseline: `8365a92416463136418065f040e8b7f84bcff9fe`; branch `affiliate/full-e2e-20261115`.
- The original working directory, its 17 protected differences, preserved patch and prior Affiliate reports remain untouched. See `baseline-verification.json`.
- Production baseline was freshly verified read-only: API `880c33e45a4e4a8cf236744b328706cac229e7b12284a7054ae9828b99aa34f3`, generation 47; Web `f224346ab401bfa77a59f0716f6f555499b994a22fd7980bb8423ee29735c991`, generation 15; Storefront `e9333f609aa664d6de713d4db00b18deba054e3a8acbb559099d9ab245f9f3df`, generation 10.
- Accepted images were exercised using separate headed Owner, normal Affiliate, and anonymous customer sessions, on internal-only networking with dedicated PostgreSQL/MongoDB/Redis/RabbitMQ/OpenSearch and local communication sinks. No Production database or provider credentials were imported.
- Local external-infrastructure readiness checks were intentionally unhealthy/degraded where R2/DNS/certificate/edge services were absent. This report certifies the exercised lifecycle, not full external infrastructure readiness.
- Credentials, authentication state, raw private logs and runtime configuration are excluded from delivery.

## Forward state — persisted, not inferred from clicks

Order `8f2866d0-cac0-4c0b-bd91-fe6f3c4c5e12` / `ORD-1515-2026-01000`; Affiliate `01a1df98-5257-7282-9e19-f9f8ae5b6b74`.

1. Owner created the isolated product/commission policy; Affiliate generated a referral; anonymous customer followed it, added the item and submitted checkout exactly once. SQL attribution and checkout journey retain the Affiliate/order relationship.
2. Native confirmation, packing, dispatch, delivery, COD collection and completion persisted their lifecycle and inventory history. Checkout itself did not claim collection or remittance.
3. Amount: EGP1,000 goods + EGP100 tax + EGP10 shipping = **EGP1,110**. Commission: `(1000 + 100 - 0) × 10% = EGP110`; shipping excluded.
4. COD collection posted `JE-2026-000001`; Affiliate accrual `000002`; COGS EGP100 `000003`.
5. After the fix, native COD remittance posted `000004`: Dr `1010` / Cr `1130`, EGP1,110. Exact replay returned `wasIdempotent: true`; journal/line counts stayed 4/8.
6. Withdrawal `3df56374-eaea-4caf-9c99-0471843062ca` was approved separately from payment. Native Treasury funding posted `000005`, moving EGP110 from `1010` to `1010-BA6E7A8B`.
7. Attested manual payout `9ed77774-b08d-4507-bdfa-4e8892eb52ea` posted `000006` and persisted Paid. This records synthetic manual cash only, not an electronic transfer.

Evidence: `checkout-submitted.json`, fulfillment receipts, `state-completed.json`, `state-payout-paid.json`, COD/withdrawal/funding/payout receipts.

## Reversal state — append-only compensation

1. Missing returned-cash attestation failed with `AFFILIATE_PAYOUT_RETURN_ATTESTATION_REQUIRED`; stale review failed with `AFFILIATE_MANUAL_REQUEST_CHANGED`.
2. Native cash-return receipt `ba48a6a9-2f03-4ea3-a90a-deb6466a60e7` posted `000007`, restoring EGP110 to the original dedicated cashbox. Original payout and approval history were retained, not reopened.
3. Exact return replay was idempotent; conflicting receipt returned `AFFILIATE_PAYOUT_IDEMPOTENCY_CONFLICT`; journal/line counts stayed 7/14.
4. Returned funds required new withdrawal `e60f39f7-6e97-4974-b40c-628c8cc839ce`, status Pending, reserving EGP110. Native cancellation released the reservation. A post-success ambiguous locator failed; SQL and headed UI confirmed Cancelled, so the mutation was not repeated.
5. Native Treasury restoration posted `000008`, returning EGP110 to Main Cashbox.
6. Full post-settlement refund `refund-6376ac21-9046-411c-99ff-e0f32049367e`, EGP1,110, posted refund `000009`, Affiliate reversal `000010` and COGS reversal `000011`.

Completed orders expose no fulfillment-return action. The supported full refund restored stock through the inventory reservation authority without relabeling physical fulfillment: order/fulfillment remain Completed and shipment Delivered; payment is Refunded. No physical return or delivery is fabricated.

## Final compensated state

| Authority | Final persisted result |
|---|---|
| Inventory | Opening 3, final 3, reserved 0, damaged 0; committed 1 / returned 1 / net consumed 0 |
| Affiliate | Historical earned 110; Approved 0; effective Paid 0; Reversed 110; Net/Available/Pending reserved all 0 |
| Money | Main Cashbox, dedicated cashbox, COD clearing, Affiliate payable and all 33 ledger balances zero |
| Accounting | 11 posted balanced journals, 22 lines; five processed, non-failed accounting outbox rows |
| History | One settlement, payout, cash-return receipt, return allocation and full refund retained; two withdrawal requests retained |
| Synthetic QA authority | Zero `SyntheticQaOrders`, `SyntheticQaEvidence` and `SyntheticQaCommissionPostings`; normal lifecycle authority used |
| Views | Headed Owner and Affiliate financial totals agree with persisted SQL |

Run `python validation/affiliate-full-e2e/verify-final.py`. It asserts the read-only snapshot `state-final-reconciliation-v2.json` and writes `final-reconciliation-summary.json`.

**Analytics observation, not financial authority:** the persisted Mongo referral click retains `orderId: null` / `convertedAtUtc: null`. SQL attribution and checkout journey link the order correctly. Dashboard analytics (including 109 tracked clicks) are projections, not earnings or proof of 109 customer sessions. No analytics conversion-bridge PASS is claimed; the financial lifecycle PASS is explicitly independent of that projection.

## Regression and build gates

- Before fix: 1/1 reproduced failure, `COD_MANUAL_CASHBOX_REQUIRED` (`results/journey-before.trx`).
- After fix: 4/4 passed, zero skipped/failed (`results/journey-after.trx`): dedicated payout cashbox does not displace canonical `1010`; inactive/deleted mapping or inactive account never falls back.
- Normal Release test assembly: the same 4/4 passed through standard `TestDatabaseScope` (`results/journey-standard-owned-fixture.trx`), in a disposable network-none container. Initial attempts failed before test bodies because temporary-path settings and the journal-protected fixture hostname were missing; supplying the existing harness contract resolved both without changing product code or bypassing journal protections.
- Regression uses standard disposable `TestDatabaseScope` in normal CI, with a separate network-none Unix-socket harness for the focused evidence.
- Release API build and normal `PointOfSale.Tests` Release build completed with zero errors; existing repository/offline vulnerability-audit warnings are not represented as a clean security gate.
- Isolated successor image `1844b03361d6219b58dfe92dcbff7de2134688a7bfc149cb5548d47bb0ce6a66` exercised the fix. Production requires a separately verified full immutable build and security gate.
- Initial production packaging stopped on a missing existing embedded theme-authority resource. The resource was added to the isolated candidate package; failed logs are retained privately, and earlier release evidence was not overwritten.
- Full immutable successor: `docker.io/shopixy/api@sha256:8bd79c212714d258cdffb167617501cab8408b2645cef8dbffab9849e8249f02`. All 2,649 sealed build inputs and the embedded theme resource match staged Git bytes. NuGet reported no vulnerable packages; Trivy updated its database and reported zero HIGH/CRITICAL findings for this exact image. Runtime checks verified .NET 10.0.15 and UID 1654. See `production-api-candidate.json` and `production-security-gate.json`.
- The builder's digest-form base lookup attempted an unavailable private registry despite a local import. A local tag alias was used only after its image identity matched the exact original pinned base digest `5efeb704…`; base content and gate requirements were not changed.

## Production and Git delivery

**Production API deployment is verified; Git delivery is pending at this report revision.** No Production business mutations were performed. The first release attempt imported the verified archive but stopped before any deployment patch because the canonical image alias was absent. Fresh runtime inspection confirmed the original API generation 47 and unchanged, Ready Web/Storefront. The original attempt is retained in `production-release-attempts.json`. An explicit one-shot recovery verified the imported index/config/source identity and added the missing canonical alias; it did not blindly rerun or overwrite the first attempt.

That recovery reached generation 48 but CRI image creation failed before the candidate API container started: the runtime additionally required the `docker.io/library/`-normalized import alias. The 480-second readiness guard automatically restored the accepted image at generation 49, Ready with zero current restarts. Web/Storefront remained unchanged. **API availability was affected during this failed rollout; no zero-downtime claim is made.** `production-alias-rollback.json` and `production-rollout-diagnosis.json` retain the evidence. A separately guarded correction added the diagnosed normalized alias and passed a harmless SHA-256-only image-instantiation check before the successful image patch; no application, schema, or configuration fix was introduced during recovery.

Final fresh acceptance at **2026-11-15 17:59 UTC**:

- API: `docker.io/shopixy/api@sha256:8bd79c212714d258cdffb167617501cab8408b2645cef8dbffab9849e8249f02`, generation **50**, 1/1 Ready/Available, zero current restarts.
- Live API and Infrastructure assembly SHA-256 values match the full candidate. Fresh CRI graph inspection confirms the pinned image index and exact selected config/ordered layers.
- `/health/ready`: **HTTP 200**. Web generation **15** and Storefront generation **10**, including pod identities/specifications, unchanged and Ready.
- No schema, grants, configuration, real money, synthetic business identities, products, orders, payout or journal rows were created in Production. Read-only before/after boundary checks return zero rows for every isolated fixture identity.
- All 17 protected differences, the preserved patch and preceding reports were reverified unchanged before commit.

Production delivery used verified archive import into the existing node, **not a successful registry push**. See `production-deployment.json`, `production-runtime-after-api-release.json`, and `production-boundary-after-api-release.json`.
