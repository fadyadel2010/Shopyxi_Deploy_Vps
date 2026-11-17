# Super Admin Orders forensic audit

**Overall: BLOCKED — validation and deployment acceptance are in progress.**
This is not a completed production acceptance or populated-lifecycle certificate.
No Orders application release has been deployed by this audit yet.

- Branch: `super-admin/orders-forensic-audit-20261117`
- Preserved starting/final Finance delivery: `181b8a0f69ff36545acd09618b41b0dd0006900d`
- Production: existing Ubuntu/K3s VPS `207.231.111.200`
- Screen: `https://admin.shopyxi.com/super-admin/orders`

## 1. Executive summary — BLOCKED

The genuine empty CommerceHub population has been independently reverified.
The screen does not contain POS transactions. Important Orders-only defects
were confirmed in formulas/presentation, CSV download routing/security, filter
contracts, live-refresh truthfulness and inspection completeness.

Narrow remediation is implemented locally, with no Finance calculation/rule,
schema, migration or production business-data change. Release acceptance is
withheld until the remaining test/deployment/browser gates are sealed.

## 2. Initial production observations — PASS (baseline evidence)

`production-before.json` captures a read-only repeatable-read SQL audit at
2026-11-17 15:51 UTC, after an earlier independent 13:39 UTC check.
All six application deployments were fully ready, running the Finance API/Web
images. The other four application workloads were not redeployed.

`browser-before.json` is actual headed Chrome, with existing user-authenticated
profile; no cookie/token was read or injected. Four sanitized main-content
captures cover English/LTR and Arabic/RTL at 1440px/390px. No console error,
exception or business-write request occurred during this baseline capture.

The page had zero matching rows, an unconditional green “Live monitoring” badge,
and artificial page 1 of 1 despite backend totalPages=0.
`export-before-complete.json` reproduces the actual export-button defect:
GET **`/orders/export` returned HTTP 200 `text/html`**, not the authenticated CSV
API. The initial short export observation captured no completed response and is
retained separately; the completed observation is the defect evidence.

## 3. Current population and classification — PASS (empty scope only)

At the recorded SQL snapshot, all-time parent orders, deleted parent orders,
sub-orders, items, marketplace customers, payments, payment attempts, refund
requests, refunds and operational financial transactions were **zero**.
`Orders` contains 3 separate non-marketplace records; `SalesInvoices` contains
3 POS invoices. These populations are never merged into CommerceHub counts.

The existing global rule is verified at 10%, with no active overrides. It was
not recreated or edited. Zero orphan/duplicate diagnostics are vacuous over this
empty population and are **not** transaction-integrity certification.

## 4. Architecture and data-flow map — PASS (source investigation)

See `ORDER-AUTHORITY.md`: checkout SQL -> marketplace parent -> tenant/store
splits -> frozen line snapshots; payment/attempt/refund/status evidence;
read-only Super Admin SQL service -> API -> Web. Mongo/search are derivative
and are not this screen's authority. Store membership uses parent `Any`, not
a multiplying join; details retain all live splits of a matching parent.

## 5. Lifecycle state machine — PASS (mapped and isolated COD/online scenarios)

The complete supported graph and effects are recorded in `ORDER-AUTHORITY.md`.
Only actual `OrderStatus` enum members are offered. Seller acceptance/rejection,
Packed/Returned/ReadyForShipment and partial-cancellation statuses are not
invented. Merchant command admission, SQL locks, optimistic version, history,
inventory idempotency, financial/event effects and no-resurrection behavior
are mapped.

Partial cancellation remains expressed by persisted child states; the existing
parent aggregation policy is not rewritten. A delivered child plus cancelled
child can aggregate to Shipped. Cancellation does not by itself prove a refund.
Those policies are disclosed rather than hidden by the Orders UI.

## 6. Payment-state reconciliation — PASS (isolated online/refund scenarios); final regression gate pending

Persisted parent/child lifecycle is displayed separately from payment attempts.
Final failed-order metrics do not count individual failed attempts or overwrite
a successfully collected order because an earlier attempt failed.
Signature/provider identity, amount/currency, processed guards, uniqueness,
unknown external outcomes, compensation and reservation admission are mapped.

The details DTO omits tokens, raw payloads, checkout URLs and order access
credentials. Test providers must remain deterministic isolated boundaries, not
live provider sessions.

## 7. Seller/store/sub-order reconciliation — PASS (mapped and isolated split/snapshot tests)

`StoreId` identifies the vendor/tenant; `StoreName` and purchase financial fields
are snapshots. One live child per parent/store is constrained by SQL. Listing
override/source-price distinction and vendor > category > global precedence are
preserved. No historical repricing is performed.

## 8. Summary formulas and sources — PASS (real PostgreSQL query tests)

Full formulas, currency, UTC boundary and selected-store rules are in
`ORDER-AUTHORITY.md`.

| Display | Formula |
|---|---|
| Total orders today | Non-deleted parents created in current UTC calendar day |
| Pending orders | Non-deleted parents with persisted Pending status, all time |
| Orders with failed payments | Unique parents whose persisted payment state is Failed/Rejected/Expired/AmountMismatch, all time |
| Captured sales today | Sum of live child totals by child PaidAt in current UTC day and Collected/Settled/Refunded/PartiallyRefunded state; selected-store allocation when selected |

Captured sales is **gross collected order value**, including shipping and before
refunds, not platform commission/net revenue or treasury settlement.
Only store selection scopes cards; other filters scope the table/export.
This distinction is explicitly explained in both UI languages.

## 9. Search/filter/date/sort/pagination — PASS (real PostgreSQL and frontend tests)

Backend tests use real isolated PostgreSQL, not InMemory substitutes for ILIKE,
constraints or transactions. Coverage targets exact Guid/literal substring/name,
SQL metacharacters, case, all state/payment filters, store membership, UTC
midnights, from/to-only/both, all eight stable sorts and page boundaries.

The first run exposed an explicit-escape requirement in Npgsql ILIKE; the
correction passed real PostgreSQL tests. Unknown URL values fail closed in Web; unknown
server enum values are rejected, not mapped to Unpaid/Pending.

Existing global MVC pagination normalization clamps page=0 and excessive
pageSize before controller validation. That established shared contract is
preserved and documented, not misreported as an Orders validator bypass.

## 10. Live tracking — PASS (isolated frontend); deployed browser gate pending

List and stats now poll every 30 seconds while active; no streaming claim.
No stale server-side Orders Redis response is reused. Manual refresh keeps
filter/page state. Age/error/hidden/offline states cannot show green active.
Unavailable KPIs are not fabricated zeros. Multiple read queries use one
repeatable-read read-only snapshot; view/export operational audit writes are
distinguished from business-data mutation.

## 11. CSV export — PASS (isolated SQL/controller/frontend); deployed download gate pending

Actual old UI route downloaded HTML; authenticated `/api/super-admin/orders/export`
is now used and non-CSV content rejected. Export covers the whole filtered
population, not the page, up to 10,000 rows; excessive scope receives explicit 422.
Counts/materialization are bounded and snapshot-consistent. UTF-8 BOM supports
Arabic; dates/amounts are invariant; formula prefixes are neutralized and CSV
escaping preserved. Phones/addresses/tokens are omitted, customer names remain
authorized export data.

## 12. Details and administrative actions — PASS (read-only source boundary)

Only GET list/stats/export/details exist on the Orders controller. No cancel,
capture, refund, fulfillment or stock mutation button/API has been added.
Existing persisted payment attempts, refund executions/requests and lifecycle
history are exposed for inspection. No live production mutation was tested.
Carrier-level shipment timelines are not implemented in this drawer.

## 13. Security and authorization — PASS (anonymous baseline); further gates pending

`anonymous-authorization-before.json`: **5/5 endpoints return 401**.
Inherited dedicated Super Admin scheme/policy plus live SQL platform authority
is unchanged. Tenant-wide access is intentional for Super Admin, not anonymous.
Merchant/customer isolation and identifier/enum validation are documented and
covered by the relevant isolated tests; they are not inferred solely from empty
production results. Orders responses are marked no-store.

## 14. Localization, responsive UI and accessibility — baseline PASS; release pending

Both languages/directions and desktop/mobile showed no page horizontal overflow
in headed baseline. Date `mm/dd/yyyy` is native browser locale presentation,
not an application-owned placeholder defect; ISO native date semantics are
preserved, with language/accessible labels and explicit UTC-day guidance.
Payment/refund labels, multi-vendor summaries, stale/unavailable/export feedback
and zero-page wording were corrected. Drawer modal focus/Escape/restore and
hidden-focus behavior have dedicated frontend tests.

## 15. Defects and root causes

| Defect | Root cause / change |
|---|---|
| CSV button downloads HTML | Blob transport used origin-relative `/orders/export` rather than authenticated API path |
| Gross creation totals labeled revenue | Unconditional Sum(parent TotalAmount), regardless of payment/cancellation |
| Unusable status choices | UI offered strings absent from backend enum |
| Failure population mismatch | Backend omitted Expired/AmountMismatch while UI highlighted them |
| Misleading green live badge | Static indicator; only stats polled; cache timestamps could be mistaken for current authority |
| Potential page-boundary instability | Sorts lacked final unique-ID tie-breaker |
| Empty one-page wording | UI used totalPages-or-1 despite zero rows |
| Unsafe/unbounded export | No formula neutralization, BOM or row bound |
| Incomplete investigation evidence | Drawer omitted attempts, persisted refund executions and status history |
| Hidden focusable drawer | Off-screen closed panel remained in DOM; no focus trap/restore |
| Query errors could look empty/zero | Fallback metrics/empty state did not distinguish unavailable from valid empty |
| Literal search escaping required explicit provider escape | Npgsql ILIKE translation requires an explicit escape character for escaped `%`/`_` |
| Verified pending Paymob callback fails its evidence write | Pending branch omitted the required payment-write boundary; boundary added after signature verification and payment resolution, without recognizing collection |
| Authorization-only Paymob evidence has the same boundary omission | Adjacent evidence-only branch now enters its own payment boundary; final dedicated regression rerun pending |

## 16. Fixes implemented — final regression/build/deployment gates pending

Orders service/controller/DTOs/Web components and focused tests/evidence
are changed, plus the verified-pending and authorization-only Paymob callbacks'
missing evidence-write boundaries discovered through real-service testing and
adjacent-path review. No Finance calculation,
commission/price/stock policy, signature check or collection guard is changed.
Details: `ORDER-AUTHORITY.md` and Git diff.

## 17. Database/migration impact — PASS (no production change)

No migration added/run. No production order/payment/customer/refund/stock/
commission/financial mutation performed. Current parent images were exported
for rollback/package preparation; deployments are still unchanged.
`preservation-before.json` fingerprints 24 business/financial scopes without
exporting rows. Isolated test databases are loopback-only randomly owned
`finance_audit_*` databases; the migration-owned order-number sequence is
created only inside the owned fixture, without weakening application guards.

## 18. Exact tests — PASS (backend/frontend); committed-source build pending

| Gate | Result / receipt |
|---|---|
| Focused initial Orders frontend | PASS: 35/35, `frontend-orders.json` |
| First unrestricted Super Admin frontend | FAILED: 145/177 passed, 32 timing failures under concurrent compilation; retained in `frontend-super-admin.json` |
| Controlled final Super Admin frontend | PASS: **182/182**, 37 files, `frontend-super-admin-serial.json`; no unrelated test changes |
| Initial PostgreSQL run | FAILED: 23/28 passed; 2 initial transport timeouts, 2 ILIKE escaping failures, 1 missing fixture sequence; `test-results/backend-orders-first-run.trx` |
| COD lifecycle and six commission-snapshot scenarios | PASS: 7 tests in `test-results/backend-orders-fixture-retest-3.trx`; that historical run also retains 2 online fixture failures |
| Online payment/retry/refund scenarios | PASS: 2/2, `test-results/backend-orders-online-boundary-retest.trx`; actual GL outbox posting and reconciliation, including blocked-unposted refund and over-refund rejection |
| Historical backend candidate | 169/170 passed; stale incremental authorization-only assembly failure retained in `test-results/backend-orders-stale-candidate.trx` |
| Final backend/query/lifecycle/regression | PASS: **170/170**, zero skipped, `test-results/backend-orders-final.trx`, after non-incremental API rebuild and stable-source full rerun |
| Full Web TypeScript / production build | PASS for first candidate; final source/artifact binding pending |
| API Debug / Release builds | PASS, 0 errors; final source/artifact binding pending |

No passed InMemory/source-string unit test is mislabeled as a real PostgreSQL
mutation integration test. No skipped/early-return harness is treated as executed
transaction evidence.

## 19. Production deployment identities — NOT TESTED (not deployed yet)

Parent API:
`localhost/shopixy/api@sha256:5a3bab31b628f8a3dfebd88c9eb0d60083176f9fc5f0b8a4d7c43845844e67ba`

Parent Web:
`localhost/shopixy/web@sha256:207a1794c0411b5a895a9cb411b4b91836c779b3283bfea127cc7e888eb216bf`

`production-parents.json` records current exported archive hashes.
No registry publication is claimed. Planned delivery is containerd archive import,
with immutable identities, runtime hashes, guarded image-only patch, readiness,
restart and unrelated-deployment verification. Release is forbidden if test or
runtime-parent guards fail.

## 20. Real production browser acceptance — NOT TESTED for the release

Baseline is captured; final acceptance must exercise the deployed bundle and
actual UI controls/export/polling in both languages and viewports. No populated
production detail/lifecycle can be exercised over an empty marketplace.
No live providers/refunds/orders will be fabricated.

## 21. Git delivery — BLOCKED pending final commits/push

Local branch created from exact Finance delivery; no history rewrite or discard.
Final application/evidence commits, remote tip and PR creation link will be
recorded after validated release/evidence.

## 22. Remaining risks and limitations — explicit

- **NOT TESTED:** populated production marketplace lifecycle (population is zero).
- **BLOCKED:** committed-source release build, deployment and release browser acceptance pending.
- **PASS (policy mapped, not changed):** partial-cancellation parent aggregation
  and cancellation-vs-refund distinction remain existing workflow semantics.
- **NOT TESTED:** external provider networks/live money; deterministic fixture
  provider outcomes are not a live-provider certification.
- **PASS (disclosed limitation):** offset pages can shift between separate
  snapshots when concurrent business data changes; within-query sorting is stable.
- **PASS (disclosed limitation):** lifecycle-history table lacks its own actor-ID
  column; surrounding authority/request/event audit is separate.
- **PASS (disclosed limitation):** main Web bundle retains existing size warning.

**Acceptance remains withheld. Empty production does not prove populated
transaction lifecycles or waive the remaining gates.**
