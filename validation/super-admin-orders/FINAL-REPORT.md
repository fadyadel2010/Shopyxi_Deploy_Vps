# Super Admin Orders forensic audit

**Overall: PASS — deployed, runtime-verified and accepted for the genuine empty-production scope.**
This is not a populated-production lifecycle certificate.
API and Web application commit `4f72d0060a9c4fa7fea4b0b9945cf1b7ea9c500e`
is deployed and runtime-hash verified.

- Branch: `super-admin/orders-forensic-audit-20261117`
- Preserved starting/final Finance delivery: `181b8a0f69ff36545acd09618b41b0dd0006900d`
- Production: existing Ubuntu/K3s VPS `207.231.111.200`
- Screen: `https://admin.shopyxi.com/super-admin/orders`

## 1. Executive summary — PASS (explicit empty-production scope)

The genuine empty CommerceHub population has been independently reverified.
The screen does not contain POS transactions. Important Orders-only defects
were confirmed in formulas/presentation, CSV download routing/security, filter
contracts, live-refresh truthfulness and inspection completeness.

Narrow remediation is deployed, with no Finance calculation/rule, schema,
migration or production business-data change. Backend **170/170** and frontend
**182/182** tests passed. A clean-commit non-incremental API Release build,
TypeScript check and Web production build passed. Running immutable images and
application artifact hashes match the sealed release. Actual headed-browser
page/API checks passed **44/44** and UI actions passed **42/42**. The final
production acceptance seal passed **17/17** gates at 20:04 UTC.

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

## 6. Payment-state reconciliation — PASS (isolated online/refund and final regression suite)

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

## 10. Live tracking — PASS (isolated frontend and actual visible/hidden browser observations)

List and stats now poll every 30 seconds while active; no streaming claim.
No stale server-side Orders Redis response is reused. Manual refresh keeps
filter/page state. Age/error/hidden/offline states cannot show green active.
Unavailable KPIs are not fabricated zeros. Multiple read queries use one
repeatable-read read-only snapshot; view/export operational audit writes are
distinguished from business-data mutation.

The first browser action candidate ran in a Windows-occluded hidden page.
`browser-runtime-diagnostics.json` proves that the UI correctly reported paused.
With native window-occlusion suppression on the same real headed profile,
both Orders and stats automatically refreshed in an actually visible, online tab,
the active indicator was correct, and leaving the page stopped polling.
No JavaScript/CDP document-visibility override was used; see
`browser-environment.json` and the retained candidate receipts.

## 11. CSV export — PASS (isolated SQL/controller/frontend and actual deployed download)

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

## 13. Security and authorization — PASS (isolated tests and deployed probes)

`anonymous-authorization-before.json`: **5/5 endpoints return 401**.
Inherited dedicated Super Admin scheme/policy plus live SQL platform authority
is unchanged. Tenant-wide access is intentional for Super Admin, not anonymous.
Merchant/customer isolation and identifier/enum validation are documented and
covered by the relevant isolated tests; they are not inferred solely from empty
production results. Orders responses are marked no-store.
`anonymous-authorization-after.json` independently reconfirms **5/5 denied**.

## 14. Localization, responsive UI and accessibility — PASS (deployed empty scope; isolated drawer tests)

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
| Authorization-only Paymob evidence has the same boundary omission | Adjacent evidence-only branch enters its own payment boundary; dedicated evidence-only and rejected-signature regressions passed in the final 170/170 suite |

## 16. Fixes implemented — PASS (tests, committed-source builds and deployed runtime)

Orders service/controller/DTOs/Web components and focused tests/evidence
are changed, plus the verified-pending and authorization-only Paymob callbacks'
missing evidence-write boundaries discovered through real-service testing and
adjacent-path review. No Finance calculation,
commission/price/stock policy, signature check or collection guard is changed.
Details: `ORDER-AUTHORITY.md` and Git diff.

## 17. Database/migration impact — PASS (no production change)

No migration added/run. No production order/payment/customer/refund/stock/
commission/financial mutation performed. Only the API/Web image fields changed;
the other four deployment specifications were verified unchanged.
`preservation-before.json` fingerprints 24 business/financial scopes without
exporting rows. Isolated test databases are loopback-only randomly owned
`finance_audit_*` databases; the migration-owned order-number sequence is
created only inside the owned fixture, without weakening application guards.
`preservation-predeploy.json` and `preservation-after.json` compare all 24 scopes,
including historical POS/accounting data, inventory, prices, listings, commission
and marketplace populations. All inspected fingerprints match.

## 18. Exact tests — PASS

| Gate | Result / receipt |
|---|---|
| Focused initial Orders frontend | PASS: 35/35, `frontend-orders.json` |
| First unrestricted Super Admin frontend | FAILED: 145/177 passed, 32 timing failures under concurrent compilation; retained in `frontend-super-admin.json` |
| Controlled final Super Admin frontend | PASS: **182/182**, 37 files, `frontend-super-admin-serial.json`, rerun after owned lockfile-exact dependency restoration; no unrelated test changes |
| Initial PostgreSQL run | FAILED: 23/28 passed; 2 initial transport timeouts, 2 ILIKE escaping failures, 1 missing fixture sequence; `test-results/backend-orders-first-run.trx` |
| COD lifecycle and six commission-snapshot scenarios | PASS: 7 tests in `test-results/backend-orders-fixture-retest-3.trx`; that historical run also retains 2 online fixture failures |
| Online payment/retry/refund scenarios | PASS: 2/2, `test-results/backend-orders-online-boundary-retest.trx`; actual GL outbox posting and reconciliation, including blocked-unposted refund and over-refund rejection |
| Historical backend candidate | 169/170 passed; stale incremental authorization-only assembly failure retained in `test-results/backend-orders-stale-candidate.trx` |
| Final backend/query/lifecycle/regression | PASS: **170/170**, zero skipped, `test-results/backend-orders-final.trx`, after non-incremental API rebuild and stable-source full rerun |
| Full Web TypeScript / production build | PASS: both exit 0 from clean application commit `4f72d00`; complete Web artifact manifest sealed |
| API Debug / non-incremental Release builds | PASS, 0 errors; three deployed assembly hashes sealed |
| First committed-source build (`2c21dd6`) | Historical FAILED gate: API passed; Web dependency junctions had disappeared. `build-gates-missing-dependencies.json` retained; lockfile-exact owned install, fresh frontend suite and complete sealed build then passed |
| Final source/build acceptance | PASS: `build-gates.json` and `test-gates.json`; clean before/after, unchanged source manifest, source commit and API/Web artifact hashes bound |
| Parent-runtime compatibility | PASS: 146 referenced assemblies; no missing references, app-local version raises or common runtime package metadata differences |

No passed InMemory/source-string unit test is mislabeled as a real PostgreSQL
mutation integration test. No skipped/early-return harness is treated as executed
transaction evidence.

The initial local API archive export exhausted disk and stopped without deployment.
Only the failed owned partial archive, disposable Debug outputs and audit-owned
verified SDK scratch were removed. `recover-package.py` reused the validated API
image/context under strict source/artifact/parent/label guards, exported gzip OCI
archives locally, and built the Web image from the same sealed artifacts.
Compression changed transport manifests, not application/config identity.
See `package-recovery.json`; no application rebuild or source change occurred.

## 19. Production deployment identities — PASS

Parent API:
`localhost/shopixy/api@sha256:5a3bab31b628f8a3dfebd88c9eb0d60083176f9fc5f0b8a4d7c43845844e67ba`

Parent Web:
`localhost/shopixy/web@sha256:207a1794c0411b5a895a9cb411b4b91836c779b3283bfea127cc7e888eb216bf`

Application commit: `4f72d0060a9c4fa7fea4b0b9945cf1b7ea9c500e`.
Image-only rollout: **2026-11-17 19:50:49–19:51:26 UTC**.

| Component | Deployed manifest SHA-256 |
|---|---|
| API | `75f49cb501bae5f3add2677367300bcdbb70a9799a257041fd7d38b01d084e04` |
| Web | `21f8562eaee261db6be807e480427eb826adde886c989020146d86a49c4f373d` |

`release-inputs.json` includes archive/config/artifact hashes and parent identities.
`deployment-receipt.json` verifies immutable running images, three API assembly
hashes, Web index/bundle hashes, readiness, image-field-only patches and the four
unchanged other deployment specifications. No registry publication is claimed:
delivery used SSH OCI archive transfer/import into existing K3s containerd.
The running bundle is **`index-BiaZNAT9.js`**.

`rollback-readiness.json` verifies both original image references, original
archive hashes and the exact rollback script are retained. To roll back, an
authorized operator runs `python3 rollback-release.py` from
`/var/tmp/shopixy-orders-superadmin-20261117` on the VPS. The script compares
deployment UID/current image/resourceVersion before changing image fields.
It performs no data/schema operation. Rollback was not executed or represented
as rehearsed production acceptance.

## 20. Real production browser acceptance — PASS (empty production only)

`browser-after.json`: **44/44** sanitized authenticated GET query checks passed,
all four KPIs reconcile to empty SQL, header-only CSV has UTF-8 BOM and row-count
header 0, missing detail is 404, and the exact deployed bundle loaded.
English/LTR and Arabic/RTL at 1440px/390px show no page horizontal overflow.
No console exception/error, Orders API transport failure or business-write
request occurred. Four final screenshots are retained.

Actual filter/sort/page-size/reset/reload/refresh/export/polling actions are
recorded separately. The hidden-window candidate and the subsequent download
filename-collision candidate are retained, not relabeled PASS. Each action run
now uses a fresh owned download folder. The final run passed **42/42** actions:
all filters, five stores, eight sort controls, three page sizes, native date
ranges/rejection, shareable reload, reset, manual refresh, actual downloaded CSV
BOM/header, visible/online automatic Orders+stats polling, truthful active status,
and cleanup after leaving the page. See `browser-actions-after.json`.

Cloudflare-injected `static.cloudflareinsights.com` telemetry is blocked by the
existing CSP. Those exact Script/CSP events remain explicit policy-blocked
resources, not Orders transport failures. CSP was not weakened; every other
network failure still fails the action gate. No credentials/cookies/tokens were
read, exported or injected.

No populated production detail/lifecycle can be exercised over an empty
marketplace. No live providers/refunds/orders were fabricated.
`production-acceptance.json` joins the receipts fail-closed: **17/17 gates PASS**.
The post-browser SQL/preservation check at 20:04 UTC reconfirms all business
fingerprints unchanged, all six workloads ready, zero new API/Web restarts,
the unchanged global 10% rule, and genuinely empty marketplace populations.

## 21. Git delivery — application and acceptance evidence committed/pushed

Local branch was created from exact Finance delivery; no history rewrite,
unrelated merge or discarded user work. Implementation commit
`2c21dd606bfc5aa1dc90d352203bc794cbaed068` and application/tooling commit
`4f72d0060a9c4fa7fea4b0b9945cf1b7ea9c500e` are pushed. Application/test source
is identical between those commits; the latter adds owned dependency recovery
and evidence/build-runner changes.

- Deployed application/build/image-label commit:
  **`4f72d0060a9c4fa7fea4b0b9945cf1b7ea9c500e`**.
- Acceptance evidence commit:
  **`e83d3083ea567e5148e058c6dea20bbcc2c01c99`**; pushed and independently
  verified against the remote branch tip before the report-only delivery commit.
- The final report/verification successor changes only validation evidence,
  not deployed application/test source. Its exact local HEAD/remote-tip identity
  and clean-tree result are emitted at handoff by
  `node validation/super-admin-orders/verify-delivery.cjs` and recorded in the
  delivery response. This avoids a self-referential commit hash inside the report.
- The verifier also checks all **6,080** sealed source-file hashes, unchanged
  application/test source since the deployed commit, live immutable image
  references and retained Finance/evidence ancestry.

All final evidence is secret-scanned before commit. Failed historical candidates
remain explicitly failed; neither partial receipts nor empty production are
promoted to populated-lifecycle evidence.

PR creation URL:
https://github.com/fadyadel2010/Shopyxi_Deploy_Vps/pull/new/super-admin/orders-forensic-audit-20261117

## 22. Remaining risks and limitations — explicit

- **NOT TESTED:** populated production marketplace lifecycle (population is zero).
- **PASS:** all 17 scoped production acceptance gates; no unresolved Orders
  release blocker remains within the tested scope.
- **PASS (policy mapped, not changed):** partial-cancellation parent aggregation
  and cancellation-vs-refund distinction remain existing workflow semantics.
- **NOT TESTED:** external provider networks/live money; deterministic fixture
  provider outcomes are not a live-provider certification.
- **PASS (disclosed limitation):** offset pages can shift between separate
  snapshots when concurrent business data changes; within-query sorting is stable.
- **PASS (disclosed limitation):** lifecycle-history table lacks its own actor-ID
  column; surrounding authority/request/event audit is separate.
- **PASS (disclosed limitation):** main Web bundle retains existing size warning.
- **PASS (security policy preserved):** injected third-party Cloudflare telemetry
  remains explicitly CSP-blocked; it is not an Orders application dependency.

**Accepted for the verified empty-production scope, with populated lifecycles
proved only by isolated real-service PostgreSQL scenarios—not production.
No live-provider, live-money or populated-production certification is claimed.**
