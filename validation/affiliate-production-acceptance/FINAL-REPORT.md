# Affiliate Production acceptance — 2026-11-15

> **Phase-two follow-up:** The formerly missing manual-cash return capability has now been implemented, isolated-tested and deployed. See [the deployed remediation report](../affiliate-production-remediation/FINAL-REPORT.md) for 70 .NET tests, 67 frontend tests, immutable release/read-only evidence, and the documented temporary API outage/recovery. This file retains the historical phase-one observations; it is not a claim of full checkout/fulfillment or real-money Production acceptance.

## A. Verdict and scope

**Overall: BLOCKED — not full end-to-end acceptance.** Registration/session and the exercised read-only portal screens work after three focused corrections. No referred order, payment, delivery, commission approval, paid settlement, return or financial compensation was performed. Empty balances are not evidence that those workflows pass.

The required safe compensation gate failed before any product policy, stock, checkout or payout mutation. A paid Affiliate settlement requires external-payment evidence or an actual manual cash-payment attestation, and no supported complete paid-settlement reversal was identified. Production simulation guards were not relaxed.

Production review used headed Microsoft Edge: the existing Ahram Owner session, one newly registered normal Affiliate, and a distinct anonymous browser context. No new merchant, tenant, administrator or privileged Affiliate identity was created. Only the operator-approved registration verification email was permitted; no payment, cash collection, carrier operation or customer communication was initiated.

## B. Environment, identities and preservation

- Existing merchant: Ahram, slug `ahram`, EGP; existing Owner session confirmed by the native session API. Tenant: `cdbe20e0-f84a-4416-b20c-044e6bcf04d0`.
- QA name: `QA-AFFILIATE-E2E-20261115`; account `01a1dc7c-3cb5-7b17-a0f2-1d082c878a60`; user `01a1dc7c-3d0c-7ef6-9025-fcdc054ef8ca`; referral code `QAAFFI578568`.
- Registration committed at `2026-11-14T23:54:52Z`. The operator confirmed email verification. Native login/logout/login succeeded; the captured session has role `Affiliate`, no tenant claim, and zero POS User rows for this identity. It is Active, verified and retained, not deleted. No payout destination was saved.
- Baseline: `0dfde49d9b9442c5a77277b399df50774fb631a2`; branch `affiliate/production-acceptance-20261115`; existing private repository `https://github.com/fadyadel2010/Shopyxi_Deploy_Vps.git`.
- All 17 original working differences/absences are preserved by hash/absence checks against existing provenance. `docs/local-work/preserved-working-differences.patch` remains identical to HEAD, SHA-256 `f65a1fb038b4285e4ec7a7e41ee0271412933f70a4cd5b8816d8e67f7dd20468`. No broad staging/reset/cleanup was used.
- Builds used a detached baseline worktree plus only the focused profile source/test changes and existing pinned dependencies, not the unrelated working versions.

Evidence: `authentication-result.json`, `business-state-{before,after}.json`, `merchant-review.json`, `preservation-verification.json`, `runtime-{before,after}.json`.

## C. Public and identity screens

| Screen/action | Observed result | Limit |
|---|---|---|
| Registration validation | Required fields, invalid email and weak-password feedback exercised without submitting another account | Not a full adversarial auth test |
| Real QA registration | Initially HTTP503; after exact ACL correction HTTP200, one persisted QA account | Two failed POSTs were observed before the fix; their duplication cause is not asserted |
| Verification | Operator verified email; native verification success screen and one verification audit record | No token or mailbox is included in Git |
| Login/logout/login | Native UI, successful responses and role boundary recorded | No fabricated browser session/token |
| Forgot password | Navigation/render inspected | No reset email requested |
| Verify/reset without token | Missing-token errors inspected | No real reset token exercised |
| Public landing/search | Real navigation and empty search response inspected | No eligible product/category detail links existed |
| Anonymous portal | Redirected to normal Affiliate login | Separate clean context, not the Owner session |

Evidence: `public-review.json`, `registration-before-fix.json`, `registration-result.json`, `authentication-result.json` and corresponding PNGs. Some initial screenshots were taken during entrance animation; the post-deployment portal screenshots are settled captures. JSON/network records, not an animated blank screenshot, establish the observed result.

## D. Authenticated Affiliate portal

All nine sidebar tabs were visited through the real UI, then revisited after rollout:

| Tab | Final exercised result |
|---|---|
| Dashboard | HTTP200; no activity, available/reserved EGP0; analytics explicitly separated from payable authority |
| Promote | HTTP200; genuine empty discovery; search and newest-sort controls send successful reads |
| My Links | HTTP200; no promotion links; no link created |
| My Landing Pages | HTTP200; no eligible pages; no design written |
| Orders | HTTP200; no orders; no detail row available |
| Earnings | HTTP200; no earnings/commission ledger events; immutable history explanation present |
| Withdrawals | HTTP200; all balances EGP0; minimum EGP100; request disabled/below threshold; no request submitted |
| Analytics | HTTP200 for summary/trend/top products/top stores; zero/empty projection; 7/90/30-day controls exercised |
| Settings | Active account, referral code, no payout wallet; corrected Member Since; hard refresh retains session/tab |

Empty-wallet save shows `Wallet number is required`; cancel restores the unconfigured view. No wallet PUT or other mutation request occurred in the post-deployment control run. Some per-range text capture precedes React's settled render (notably 7 days); endpoint success alone is not claimed as complete visual/chart acceptance for every range. Settled 30-day analytics and all-tab captures are available.

The QA account's Affiliate profile read succeeds; POS session read is HTTP401. An admin-list probe on the Affiliate origin returned HTTP400, not an asserted role-specific HTTP403. This limited probe is **not** a comprehensive cross-tenant authorization test. Existing Cloudflare Insights script CSP errors persist; CSP was not broadened. No application API503 remains in the recorded final tab review.

Evidence: `portal-review-before-classification-fix.json`, `portal-review.json`, `portal-review-after-deploy.json`, `post-deploy-review.json`, `portal-*.png`, `profile-date-after-deploy.png`.

## E. Existing Owner and product readiness

The existing Owner opened Affiliate Products, the native commission settings dialog for the existing tracked QA product, Affiliate management and readiness. No settings were submitted. The merchant list intentionally includes Affiliates with commission history, not all registered global Affiliates; the new account's absence there is expected.

Existing QA product `904b05b1-767b-59d8-98b0-40461b7667d6` (`QA-ERP-R10-20261113-TRACKED`) is inactive/archived, has no Affiliate policy and no available stock. This explains the empty eligible catalog; it was not published, enabled or stocked solely to produce a pass. Product/category detail, generated-link preview/copy, landing design/save, populated pagination and real order details remain **BLOCKED/not exercised**.

Evidence: `merchant-review.json`, `merchant-products.png`, `merchant-product-commission-review.png`, `merchant-affiliates-empty.png`, `inventory-before.json`.

## F. Screen → API → service → entity → effects map

This is a **source review**, not proof of Production transaction execution. Paths below are in the existing source tree.

| Surface | API/controller | Service/authority → entities | Financial/stock effect |
|---|---|---|---|
| Register, verify, login | `/api/affiliate/auth/*`, `AffiliateAuthController` | Native identity transaction/token ceremony → `AffiliateUsers`, `AffiliateAccounts`, hashed verification tokens, auth/session/audit records | Identity/session only; no POS user or financial credit |
| Owner product settings | Admin product Affiliate APIs, `AdminProductAffiliateController` | Product policy/category and SQL admission authorities → `AffiliateProductPolicies`, product/category lineage | Policy only; does not itself receipt stock or create commission |
| Promote/discovery | `/api/affiliate/discover/*`, `AffiliatePortalController` / `AffiliateDiscoveryController` | Search projection plus current SQL authority overlay → products, variants, active category/policy/merchant and inventory | Read-only admission; search projection is not payable/stock authority |
| Generate link | `/api/affiliate/promotion-links`, `AffiliatePromotionLinksController` | Current eligibility checks + `StorefrontUrlResolver.BuildPromotionLink` → `AffiliatePromotionLinks` | Durable marketing link only; URL uses merchant `/products/{slug}?ref={code}` |
| Anonymous referred visit | `AffiliateReferralMiddleware` | Validates referral/link and protected attribution evidence, existing shopping journey/cart precedence | Tracking/session only; cookie is not final order/payment authority |
| Checkout | `POST /api/storefront/checkout`, `CheckoutController` | `Services/Checkout/CheckoutService` and inventory/attribution authority → orders/items, immutable commission snapshots, `AffiliateAttributions`, inventory reservations | Creates obligations/reservations as admitted; order creation alone is not payable commission |
| Fulfillment | `/api/orders/{id}/fulfillment/*`, `OrderFulfillmentController` | `OrderFulfillmentService`, `ManualOrderCompletionAuthority` → order/shipment/reservation/movement/audit state | Native reserve/commit/release/return path depends on actual state; delivery does not manufacture collection evidence |
| Earn/approve/reverse commission | Existing Affiliate event/service flow | `AffiliateService`, `AffiliateService.CommissionAuthority` → immutable `AffiliateCommissionLedger` + original item/order/attribution lineage | Requires payment/accounting and delivery authority; reversal is not a payout cashbox undo |
| Dashboard/orders/earnings | `/api/affiliate/dashboard`, `/orders`, `/earnings-summary`, `/commission-activity` | `AffiliatePortalReadStore`, `AffiliateCommissionEligibility`, payout balance reads → canonical orders + commission history + QA classification | Read-only; synthetic history excluded from payable calculations |
| Request/cancel withdrawal | `/api/affiliate/payouts/request`, `/payouts/{id}/cancel` | `AffiliatePayoutService` → `AffiliatePayoutRequests`, audit; current destination snapshot | Pending reservation is derived, not a stored-money credit; only untouched Pending request cancellable |
| Owner settlement/manual cash | Admin Affiliate pay/payout APIs; `AffiliateManualPayoutController` | `AffiliateSettlementOrchestrator` + `.Manual` and financial posting → payout tuple, `AffiliateBalanceLedger.Withdrawal`, manual confirmation, ledger journal/lines | Decreases Affiliate receivable; manual settlement debits merchant Affiliate payable and credits selected cashbox ledger |
| Analytics/settings | `/api/affiliate/analytics/*`, `/profile`, `/profile/payout-wallet` | Mongo analytics projections / profile controller → projection/profile destination | Analytics not financial authority; destination update would be persistent but was not submitted |

## G. Referral and commission contract

`PointOfSale.API/Middleware/AffiliateReferralMiddleware.cs` reviews query/header/route referral sources, first-touch/protected-cookie behavior, existing journey/cart precedence, and 30-day cookie expiry. Checkout revalidates SQL authority before stamping the order. No referral was forged into a cart/order; browser first-touch retention, competing-referral behavior and final authoritative attribution remain untested because no eligible native link was generated.

`PointOfSale.Infrastructure/Services/Affiliate/AffiliateService.CommissionAuthority.cs` reproduces immutable snapshots: commission base is line total (unit price × quantity − discount + tax), excluding shipping; percentage 0.01%–50%, optional per-unit cap × quantity; 4-decimal `MidpointRounding.ToZero`. Original product/policy/merchant/link/user lineage must match. Current policy is not substituted for historical terms.

`AffiliateService.cs` requires accepted payment authority and accounting Posted/NotRequired to earn; physical/manual completion authority is required to approve. A manual order with shipping method needs its original delivered manual shipment. **No transactional expected/actual commission calculation was exercised**, so these are reviewed rules only, not a monetary PASS.

## H. Reversibility gate and blocker

Reviewed existing Affiliate controllers, service contracts, settlement implementations and domain transitions before any stock/order/financial mutation:

- Native `OrderFulfillmentController` exposes cancel and returned actions; payment refund authority is separately routed through payment lifecycle. Their combined notification suppression, exact order-state compensation and financial effects were not fully established for this proposed run.
- `IAffiliateService.ReverseCommissionForOrderItemAsync` and immutable reversal events address commission, not the entire already-paid settlement/cashbox/request tuple.
- `AffiliatePayoutRequest.IsCancellationCandidate` permits only untouched Pending reservations. Approved authorizes external execution; Paid is terminal in the reviewed model. Cancel is not a paid withdrawal undo.
- `AdminAffiliateController.RecordPayment` requires an external payment reference and external confirmation path. `AffiliateSettlementOrchestrator.Manual.ValidateManualCommand` requires explicit attestation of an **actually performed manual payout**, method Cash, and valid same-store cashbox/funding/destination authority. Labeling fictional cash as QA does not satisfy it.
- No canonical paid-settlement reversal was identified in the reviewed Affiliate API/service/domain surfaces. Reversing a journal or commission alone would not prove restoration of the balance-ledger withdrawal and payout tuple; no such workaround was attempted.
- Synthetic certification/payment/delivery is Development/Test-only; payout simulation is Development/QA-only. These guards remained intact, and no unsupported Production simulation was enabled.

Therefore stop before policy/stock/order mutation as required. To resume, provide an explicitly supported reversible synthetic settlement/fulfillment path that is permitted in the target environment, with notification suppression and complete stock/financial compensation demonstrated outside Production first. This task did not add a new financial reversal feature or weaken evidence admission.

## I. Financial and inventory reconciliation

Read-only repeatable-read snapshots use actual merchant `LedgerLines`/`LedgerJournalEntries`, ledger accounts and cashbox/treasury mappings. Exact non-QA amounts remain private; only unchanged comparisons are published. They are identical between the captured before/after snapshots. These snapshots were taken after identity/ACL work and bracket the remaining read-only/frontend review; they are not a claim of historical full-database equality.

| QA authority | Before | After |
|---|---:|---:|
| Orders / attribution / promotion links | 0 / 0 / 0 | 0 / 0 / 0 |
| Commission entries / balance entries / net movement | 0 / 0 / EGP0 | 0 / 0 / EGP0 |
| Withdrawal requests / manual payouts | 0 / 0 | 0 / 0 |
| Actual variant stock, warehouse 1 (quantity/reserved/damaged) | 0 / 0 / 0 | 0 / 0 / 0 |
| Actual variant stock, warehouse 2 (quantity/reserved/damaged) | 0 / 0 / 0 | 0 / 0 / 0 |
| Remaining reserved quantity | 0 | 0 |
| Existing product movement history count / net quantity | 16 / 0 | 16 / 0 |

Stock authority is **ProductVariantStocks**, not empty compatibility `WarehouseStocks` rows. Default variant: `4c0faed0-ed21-49ee-a823-85e9bb2fffed`. Both warehouses and historical movements were retained. No restoration was necessary because none of these business mutations was started; this is not a tested reversal PASS.

Evidence: `business-state-{before,after}.json`, `inventory-{before,after}.json` and their read-only SQL/capture scripts.

## J. Confirmed defects and corrections

1. **Registration/resend HTTP503, SQLSTATE42501.** Native replacement executes DELETE on `AffiliateEmailVerificationTokens` even with zero rows. Existing runtime already had SELECT/INSERT/UPDATE. Added only DELETE on that ephemeral token table to existing restricted `shopixy_api_runtime_dml_v1`; no account deletion/default grants/role changes. Failed registration rolled back; account absence was verified before retry. First deployment transaction rejected a wrong owner precondition before GRANT; the observed owner `app` was guarded, rehearsed again and a separately recorded bounded attempt succeeded.
2. **Dashboard/Orders/Earnings/Withdrawals HTTP503.** Canonical financial reads require synthetic-history classification. Added only SELECT(`OrderId`) on `SyntheticQaEvidence` and `SyntheticQaCommissionPostings`; preserved existing `SyntheticQaOrders.OrderId` grant. No table-wide evidence reads, synthetic writes, new role membership or QA enablement. Real zero-history portal reads now succeed.
3. **Profile Member Since dash.** `ProfileTab.jsx` read `createdAt`, but the API returns `createdAtUtc`. Changed that single field; corrected fixture and added API-contract/missing-date regressions. Live UTC creation timestamp formats as `11/15/2026` in this browser's local timezone; the expected date was computed from the actual API timestamp, not hard-coded as UTC day.

Exact apply/rollback SQL: `deploy/affiliate/01` through `04`. The ACL rollback scripts restore the independently observed pre-release grants but would reintroduce the corresponding failures; they were rehearsed locally, not applied after successful Production correction. All deployment receipts explicitly exclude business-data SQL writes.

## K. Tests and deployment

- Isolated PostgreSQL18.6, no network/Production data: pre-fix zero-row DELETE denial, exact idempotent grant, scoped outstanding-token replacement, retained other-user/consumed tokens, unrelated permission denial, classification union, sensitive-column/write denial and exact ACL rollback all passed. `grant-regression.log`.
- Profile regression before fix: 1 failed / 2 passed. After fix: **32/32 focused frontend tests**, TypeScript and Vite build passed with existing dependencies. Initial TypeScript attempt lacked a Storefront dependency junction; after correcting only the isolated environment link, all gates passed. No dependency upgrade or unrelated source repair. `profile-tests-before.json`, `frontend-gates.json`.
- Image: exact dist parity, UID101, nginx configuration and offline cached-DB Trivy HIGH/CRITICAL gate passed. Offline scan is not an assertion of up-to-the-minute advisory coverage. `frontend-image-gates.json`, `frontend-vulnerability-summary.json`.
- Docker Hub push was **rejected** with `insufficient_scope: authorization failed`. A local RepoDigests value does not prove a remote registry push.
- Used the established OCI archive-import path into existing single-node k3s. Archive SHA-256 `10096bf675b64af495ebab19925c1422973f693c72cc25529833c951cedaeeaf`; source-index SHA-256 `0de9f025e0825ad1a9b726d80dbaca487a6e2f0658ef938a95e212a9b95dc11b`.
- Web image `docker.io/shopixy/web@sha256:1a594262000458743141e30bc416a51d05d7717aa565a33621b961507a3670d9`; Web generation13→14. CAS patch tested Deployment UID/resourceVersion/old image; only the `web` image changed. Existing Recreate strategy/one replica preserved; final Ready1/1. Manifest alias and actual CRI config/image ID were reconciled rather than treated as interchangeable.
- API generation44 and Storefront generation10/images remained unchanged; no API rebuild was needed. `frontend-deployment.json` and `runtime-after.json` verify these limits. Rollback Web image is recorded in the receipt.
- Post-rollout headed checks confirmed all tabs, corrected date, retained login and safe validation/search/range controls; no post-deploy financial/profile mutation request was observed.

## L. Privacy and evidence handling

No real mailbox, generated password, verification/reset token, cookie/JWT, browser state, Secret resource, private backup or non-QA merchant financial row is included. Screenshots mask identity fields; raw CLI echoes and token-bearing `.playwright-cli` artifacts are excluded. Initial UTF-16 receipts were normalized to UTF-8 before staging/scanning. Exact private financial snapshots and generated credentials remain outside Git in the approved private temporary location.

Only focused source/test files, `deploy/affiliate/` and this sanitized evidence directory are staged. The actual staged diff is scanned with existing Gitleaks plus exact private-value checks and staged source-index verification. See `staged-secret-scan.json`; no broad baseline false-positive allowlist is imported. Do not automatically rerun the enrollment/deployment scripts: their safeguards deliberately require inspection of prior attempts.

All 1,455 compiled Web input hashes match the prospective Git index. The scanner's 109 generic-key findings are reviewed integrity digests in that exact independently verified source manifest, not credentials; zero real/unreviewed findings remain.

## M. Repository delivery

The existing private origin and push authority are checked using the already configured Git credential in process memory only. No credential is printed, persisted in the repository, replaced or requested from the operator. Focused changes are delivered on `affiliate/production-acceptance-20261115`, then `main` is advanced only after confirming it is still the reviewed baseline and the update is a fast-forward. No force push or acceptance tag is used. The local original working differences remain uncommitted.

`github-prerequisite.json` records repository privacy/access. Final branch/main equality and complete remote-tree verification are checked after push; the exact resulting commit is reported in the delivery response. A feature-fix release does **not** assert complete Affiliate acceptance.

## N. Remaining acceptance matrix

| Requirement | Status |
|---|---|
| Normal account registration, operator email verification, native login/logout/login | PASS for the one exercised QA identity |
| Nine portal read/empty states and corrected date after deployment | PASS for exercised states only |
| Owner Affiliate/product policy/readiness inspection | PASS read-only; configuration writes untested |
| Eligible public product/category detail and native generated link | BLOCKED: no eligible catalog and safe-journey gate |
| Anonymous referral persistence → checkout → canonical attribution | BLOCKED / not submitted |
| Stock receipt/reserve/dispatch/delivery/return | BLOCKED / not submitted |
| Actual earned/approved commission and expected-vs-actual amount | BLOCKED / no transaction |
| Withdrawal request/approval/payment, cashbox posting, full compensation | BLOCKED: evidence/paid-reversal gate |
| Final balances, inventory and history unchanged in snapshot scope | PASS no-mutation invariant, not reversal acceptance |
| Focused tests and existing-k3s frontend deployment | PASS; registry push separately FAILED |
| Full Production Affiliate journey | **BLOCKED — no full PASS claim** |

Retained artifacts: one Active verified normal QA Affiliate, existing unchanged QA product/history, exact ACL corrections, deployed frontend timestamp correction, sanitized evidence. No order/payment/shipment/commission/payout record from this journey needs cancellation or compensation because none was created.
