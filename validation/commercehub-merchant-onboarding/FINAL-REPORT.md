# CommerceHub Merchant Onboarding — Final Report

**Date:** 2026-11-16
**Feature branch:** `commercehub/merchant-onboarding-20261116`
**Overall result:** **DEPLOYED AND ACCEPTED, with Production merchant-session acceptance blocked**

## 1. Outcome

Shopixy now supports a durable CommerceHub seller onboarding workflow:

1. An Owner/Admin submits a seller application.
2. The merchant sees durable Not Applied, Pending, Rejected, and Accepted states.
3. A Super Admin reviews a searchable inbox and approves or rejects with an audited decision.
4. Rejection requires a meaningful reason; the merchant sees that exact reason and can submit a new revision without losing history.
5. Approval activates the existing `UserGovernanceProfile.IsMarketplaceApproved` authority.
6. CommerceHub catalog management is enforced server-side and remains unavailable until tenant lifecycle, KYC, and marketplace approval all pass.
7. Approved merchants receive the redesigned bilingual CommerceHub Products UI.

Finance Dashboard, Vendor Earnings, Debt Repayment, Affiliate, Storefront, ordinary POS, inventory, and financial-record authorities were not replaced or repurposed.

## 2. Implementation

### Backend and persistence

- Added revisioned `CommerceHubSellerApplication` records with Submitted, Approved, and Rejected states.
- Added one-pending-application-per-tenant and tenant/revision uniqueness constraints.
- Added merchant-owned status/submission endpoints and Super Admin search/detail/review endpoints.
- Reused existing tenant, POS role, Super Admin platform authority, governance, rate limiting, audit, and response-envelope infrastructure.
- Approval is transactional with the existing governance activation path and preserves the KYC prerequisite.
- Added optimistic concurrency and PostgreSQL serialization-conflict handling for competing decisions.
- Added `CommerceHubApprovedSellerFilter` to all existing Admin CommerceHub catalog actions, in addition to existing publish-handler checks and navigation capabilities.
- Direct submission now verifies an active tenant, an active non-deleted actor belonging to that tenant, and absence of existing marketplace approval.

### Merchant and Super Admin UI

- Added `/commercehub/seller-application` with prefill, correction/resubmission, exact rejection reason, revision history, and durable status rendering.
- Added `/super-admin/commercehub/seller-applications` behind `super-admin.marketplace.govern`.
- Added searchable Pending/Approved/Rejected/All views and KYC-aware approve/reject controls.
- Redesigned `/admin/commercehub` as **CommerceHub Products** with listing KPIs, product/listing search, filters, responsive hierarchy, and approved-seller status.
- Added English and Arabic copy and verified the Arabic publishing presentation in an actual headed browser.

## 3. Automated gates

| Gate | Result |
|---|---|
| API Release build, .NET SDK 10.0.404 | **PASS** — 0 errors; existing unrelated warnings retained |
| Web Vite production build | **PASS** |
| Focused backend tests | **7/7 PASS** |
| Focused merchant/route/Super Admin frontend tests | **7/7 PASS** |
| EF pending-model check | **PASS** — no pending model changes |
| `git diff --check` | **PASS** |
| Package lockfile scope | **PASS** — no lockfile changes |
| API candidate Trivy HIGH/CRITICAL | **0 findings** |
| Web candidate Trivy HIGH/CRITICAL | **0 findings** |
| Candidate runtime identity | **PASS** — non-root API and Web; nginx config valid |

The focused backend suite covers submission, duplicate denial, actor/tenant bypass denial, already-approved governance denial, rejection, retained history, resubmission, approval authority, replay denial, domain transitions, and catalog action filtering. Frontend coverage includes pending/rejected merchant states, route authority, mandatory rejection reason, and KYC-disabled approval.

## 4. Isolated browser acceptance

The acceptance stack used dedicated PostgreSQL, MongoDB, Redis, RabbitMQ, OpenSearch, and communication sinks on an internal-only local container network. It imported no Production configuration or data. Browser requests outside the allowlist were aborted before dispatch.

The state-transition journey was executed through browser-native Chrome sessions. Those transition sessions were **headless**; they are not mislabeled as headed. A separate final-state acceptance pass used an **actual headed Chrome session**, confirmed by the browser harness, against the same accepted API/Web candidates.

| Scenario | Result |
|---|---|
| A — Initial merchant application | **PASS** — native form submission, HTTP 200, durable Pending state |
| B — Pending enforcement | **PASS** — duplicate UI absent, catalog HTTP 403, publishing route denied |
| C — Super Admin review | **PASS** — inbox/detail rendered; empty rejection reason produced no decision request |
| D — Rejection visibility | **PASS** — exact mandatory reason persisted and displayed to merchant |
| E — Correction/resubmission | **PASS** — revision 2 Pending; rejected revision 1 and reason retained |
| F — Existing KYC prerequisite | **PASS** — approval disabled before verification; existing governance UI used to verify isolated fixture |
| G — Approval | **PASS** — HTTP 200, application Approved, marketplace governance active, two revisions retained |
| H — Actual headed final state | **PASS** — durable Accepted state, session refresh, publishing route and catalog HTTP 200 |
| I — Publishing UI and language | **PASS** — English/Arabic approved-seller UI; zero CommerceHub product mutation requests |

Evidence is summarized in `isolated-acceptance-summary.json`; screenshots intentionally contain only isolated synthetic fixture data.

## 5. Super Admin account

- Production contains **exactly one** `IsSystem` Super Admin.
- The supported bootstrap secret contains one configured email and an **86-character** password, exceeding the 32-character minimum.
- A fresh actual-headed Production login returned HTTP 200 and reached the isolated Super Admin console.
- The system root is intentionally immutable in application code; no duplicate account was created and no unsupported direct identity mutation was performed.
- Credential values are excluded from repository files and all evidence. They are delivered only in the final direct response.

## 6. Production release

Changed deployable components: **API and Admin Web only**. Storefront remained unchanged.

| Component | Final immutable image | Generation | State |
|---|---|---:|---|
| API | `docker.io/shopixy/api@sha256:c384ee860fcbc9aa1522cd7ffa6d76da320009772fa016c615ddb9037b65187e` | 62 | 1/1 Ready, 0 restarts |
| Admin Web | `docker.io/shopixy/web@sha256:cadd2cb3a44fa6bff972652b0d94c1677c6238fbcae5240e69afcd2fed84452a` | 21 | 1/1 Ready, 0 restarts |
| Storefront | `docker.io/shopixy/storefront@sha256:e9333f609aa664d6de713d4db00b18deba054e3a8acbb559099d9ab245f9f3df` | 10 | Unchanged, Ready |

The additive `20261116100000_CommerceHubSellerApplications` migration is installed and the table remained empty during release verification.

### Release incident and recovery

The first guarded API rollout failed closed and rolled the image back because the Production installer initially omitted the `SystemSchemaVersions` startup-admission update. API availability was affected during that failed attempt; no zero-downtime claim is made. The schema authority was advanced only after asserting the migration/table and prior version, and the accepted old API returned Ready.

After the candidate retry succeeded, the first Production seller-inbox GET exposed two additional installer-authority omissions:

1. the new table was owned by `postgres` rather than `app`;
2. the inherited `shopixy_api_runtime_dml_v1` role lacked the standard SELECT/INSERT/UPDATE ACL.

Both corrections were guarded by an empty-table assertion. DELETE was not granted. No business row changed. The final fresh headed request returned HTTP 200 with zero business mutation requests. The corrected production installer in this report now includes the application role, startup-admission metadata, and runtime ACL.

See `production-deployment*.json`, `production-schema-recovery.json`, and the corrected `production-schema*.sql`.

## 7. Production acceptance and data safety

- **PASS:** actual-headed Super Admin login and seller-application inbox, HTTP 200.
- **PASS:** exactly one system Super Admin; credentials omitted from evidence.
- **PASS:** API/Web final readiness and immutable image identity.
- **PASS:** seller-application table empty; no Production seller application, governance decision, product, inventory, finance, order, Affiliate, Storefront, or checkout business mutation.
- **BLOCKED:** Production merchant-side UI acceptance requires an authorized Ahram Owner session. No credential was invented, exported, or reset to manufacture this check.
- Four console errors were observed in the fresh allowlisted Production browser context, including blocked external resources. No clean global-console claim is made; the feature API itself returned 200.

## 8. Git delivery

Implementation commit and remote-head verification are recorded in the closure update below after delivery. No force-push or history rewrite is permitted.

## 9. Final verdict

| Area | Verdict |
|---|---|
| Merchant application lifecycle | **PASS** |
| Mandatory rejection reason and durable resubmission | **PASS** |
| Super Admin authority and audit integration | **PASS** |
| Approval-gated server-side product publishing | **PASS** |
| Bilingual publishing redesign | **PASS** |
| Isolated actual-headed final acceptance | **PASS** |
| Production API/Admin Web deployment | **PASS after documented recovery** |
| Production Super Admin read-only acceptance | **PASS** |
| Production merchant UI acceptance | **BLOCKED — authorized Owner session unavailable** |
| Production business-data safety | **PASS** |

The feature is live and server-enforced. The only open acceptance item is a read-only Production merchant UI confirmation using the user-authorized Ahram Owner session.
