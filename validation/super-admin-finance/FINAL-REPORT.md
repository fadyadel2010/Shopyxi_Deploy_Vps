# Super Admin Finance forensic audit

**Status: IN PROGRESS — NOT ACCEPTED.** Started 2026-11-17 local time
(production clock was 2026-11-16 UTC). No end-to-end PASS is claimed.

## 1. Executive summary

Read-only SQL independently establishes that production currently has **no
CommerceHub marketplace orders, sub-orders, payments, refunds, or operational
ledger entries at all**, including soft-deleted records. The observed zero
Finance totals are therefore not, by themselves, a defect. Ordinary POS and
Storefront records are a separate accounting population.

## 2. Initial production findings

- Fresh repository fetch; clean starting tree at `3a124e6`.
- Dedicated branch: `finance/super-admin-forensic-audit-20261117`.
- Production SSH and Kubernetes access verified.
- API: `docker.io/shopixy/api@sha256:cb900c183069f94311142ad9c5a16fa23266611d3c29f674bae64040189ac307`.
- Admin Web: `docker.io/shopixy/web@sha256:627af0b79e1a8bfa4be76691df0f43c938c9b586727d11c2f223e43580c21568`.
- Storefront: `docker.io/shopixy/storefront@sha256:e9333f609aa664d6de713d4db00b18deba054e3a8acbb559099d9ab245f9f3df`.
- Existing global commission rule independently verified: supplied ID,
  Global, null scope ID, 10%, active, priority 0, not deleted.
- No financial or business-data mutation authorized/executed as an audit shortcut.

Subsequent findings, implementation, test receipts, deployment, and browser
acceptance will be recorded below. Unfinished acceptance requirements remain
**NOT TESTED** or **BLOCKED**, never implied PASS.

## 3. Defects reproduced and remediated

| Finding | Correction | Financial-history impact |
|---|---|---|
| Promo-funded full refunds left residual platform revenue | Debit gross commission; separately recover promo expense; align decision/reconciliation expectations | Append-only future posting correction; no historical rewrite |
| Fully funded zero net commission fell back to gross | Preserve valid zero using the frozen gross/promo split | No current-rule repricing |
| Final partial refund could reverse the original full commission again | Cumulative before/after allocation, including item quantities | No edits to completed production refunds |
| Independently rounded partial allocations lost cents | Allocate promo from rounded gross entitlement; net is gross minus promo | Conserves original frozen totals |
| Zero-rate recognized orders disappeared from count/AOV | Canonical payment-credit recognition for frozen zero commissions | No fabricated commission entries |
| Multiple promo usages multiplied reporting joins | Deterministic single non-deleted usage join; duplicate diagnostics remain | No deletion of duplicate evidence |
| Refund-only daily promo recovery was clamped away | Keep signed daily promo cost | Derived projection only |
| Partial-day windows could read whole-day caches | Restrict cache to exact complete UTC days | Read-path correction only |
| Daily distinct refunds were summed across dates | Source-backed, window-distinct recognized cohort | No money values recomputed in browser |
| Refund rate mixed unrelated populations | Numerator limited to recognized-in-window orders refunded in-window | Explicit bounded cohort definition |
| Global integrity double-counted selected diagnostics | Count each diagnostic category once | Diagnostics only |
| Empty/limited scans looked like comprehensive integrity approval | `NO_RECORDS`, explicit coverage, localized labels and “Not checked” | No ledger mutation |
| Older orders recognized later escaped scoped source checks | Scope existing ledger recognition time, creation fallback for missing-ledger checks | Read-only |
| Frontend absent/error data appeared as financial zero or stale PASS | Unavailable placeholders, retry state, stale-result warning, unavailable integrity badge | Presentation only |

## 4. Independent production reconciliation

`production-before.json` establishes an all-time empty CommerceHub ledger,
not merely an empty selected date window. Supplied 10% global rule verified;
no active scoped overrides found. This audit did not change the rule.

`production-pos-before.json` additionally shows:

- Three POS invoices, all **fully returned**; three revenue journals and
  three COGS journals.
- Three corresponding sales returns, three revenue-reversal journals and
  three COGS-reversal journals.
- Zero invoice arithmetic, full-return principal, refund allocation,
  journal-balance, missing-source, cross-tenant-line, or missing-return-journal
  mismatches in the inspected checks.
- Zero pending or permanently failed POS-sale accounting outbox messages.

These aggregate-only checks are not a claim that POS revenue belongs in
CommerceHub commission KPIs. No production orders, refunds, settlement,
payout, price, stock, publication, or balances were changed for testing.

## 5. Validation progress

- Initial focused backend reproduction: 7 failures / 8 passes against the
  original service with configured test paths.
- Corrected focused backend: **100/100 passed**, including actual PostgreSQL
  writer transactions, real commission resolver, online/COD, promo/full funding,
  zero rate, post-payout refund flag, retry idempotency and daily projection.
- Finance frontend: **13/13 passed** (metric authority, missing/zero/error,
  Arabic labels, limited coverage, loading/retry/stale refresh, scope wiring).
- All Super Admin frontend: **148/148 passed** in 33 files.
- Full frontend TypeScript check: **passed**.
- Production Web build: **passed**; existing bundle-size warning remains.
- Expanded backend and GL-bridge checks: **176/176 passed**, none skipped.
  Includes 5,150 frozen cent splits across three partial refunds, invalid
  commission snapshots, repeated-refund distinct counting, cohort boundaries,
  scoped late recognition, outbox one-to-one/tenant coupling, GL gross/promo
  cancellation, authorization and refund-scope architecture checks.

One pre-existing source-string certification expected an obsolete Owner/Admin
attribute on authenticated legacy refund tombstones. The test now checks the
actual authenticated, HTTP-410-disabled mutation contract; production
authorization and disabled mutation handlers were not changed.

The damaged initial SDK and WSL idle-network forwarding failures were local
harness failures, not application or production defects. A fresh SDK and a
test process kept inside the active WSL session resolved those obstacles.
Production accounting-period guards were not weakened.

## 6. Browser baseline and release status

The user personally authenticated the dedicated headed Chrome session.
`browser-before.json` and `browser-baseline-expanded.json` contain sanitized
network/DOM receipts; eight clipped Finance screenshots cover English/LTR and
Arabic/RTL at 1440px/390px, both the default window and the requested historical
**2026-10-18 through 2026-11-16** window. Baseline APIs returned HTTP 200 with
zero required metrics and no console errors, exceptions, or business writes.
Raw integrity enums and the misleading empty-data approval were reproduced.

Exact running production parents were exported for a narrow API/Web overlay;
parent references and archive hashes are recorded in `production-parents.json`.
No registry publication, deployment, or post-release acceptance is claimed yet.
