# General Ledger final running-balance correction — sanitized recovery edition

Original acceptance compiled 2026-11-14T11:56:04.003Z. This edition intentionally omits Production tenant/account/journal identifiers, financial rows/amounts, screenshots and raw responses. The full original report/evidence remains unchanged locally at this path. Original report SHA-256 is recorded in the recovery provenance.

## Result and bounded correction

**General Ledger: PASS**, fixed and deployed API-only, accepted through the actual headed Production UI. **Stock Count: narrow error-message correction deployed**, with regression and exact shipped-image contract verification; no new Production stock-count mutation was attempted.

The General Ledger query previously interleaved journals on the same business date using a per-journal line number and displayed persisted posting-time snapshots in that different order. The accepted deterministic ascending display contract is:

`JournalDate → PostedAtUtc → JournalNumber → LineNumber → ledger-line Id`.

Presentation balances are calculated cumulatively from the opening balance in precisely that order using the existing debit/credit normal-balance convention and decimal precision. Posting time is construction-time metadata, not independently proven commit order. The final ID is a stable defensive tie-breaker, not an accounting posting sequence.

Only runtime sources `PointOfSale.Infrastructure/Accounting/FinancialReportService.cs` and `PointOfSale.API/Infrastructure/Mvc/ApiErrorResponseNormalizationFilter.cs` changed in that release. The latter special-cases exact HTTP409 `STOCK_COUNT_STALE` with reviewed static recount guidance; arbitrary diagnostic details and unrelated safety behavior remain excluded.

## Existing evidence and acceptance boundaries

- Focused baseline: 20 tests, 15 failures/5 passes; corrected final fixtures: **20 passes, no failures/skips**. No broad backend suite was claimed.
- Tests include ordering/ties/backdating, opening/closing/sign conventions/four-decimal precision, tenant/account/date eligibility, reversal continuity and safe409 normalization. Selected existing Stock Count UI no-auto-retry coverage also passed.
- The release stage used **2,642 certified current-baseline inputs**, plus focused test inputs; four unrelated existing API working-copy differences were retained and excluded. `source-index.json` and `stage-before.json` identify them.
- Actual headed UI checks showed all eight historical running-balance mismatches resolved, unchanged opening/closing/totals, appropriate account/date filters and empty ranges. Independent read-only reconciliation and before/after fingerprints showed underlying records unchanged. Detailed records are deliberately not published here.
- The report does **not** support independent sorting/pagination; no fictitious second-page browser acceptance was claimed.
- Stock Count's exact baseline/successor API assemblies were checked in isolated network-disabled, read-only-root containers. This is shipped error-contract verification, **not a fresh live stale-post HTTP or end-to-end Stock Count workflow PASS**.

## Accepted deployment

- Current API: `docker.io/shopixy/api@sha256:d3f10ae2cc93eb1fdddb7bd89c55726ca3f6d0ea1e4dbbf7825f47aaa19dbcb9`.
- Parent API: `docker.io/shopixy/api@sha256:fcdc623d757ae9f2ddf2314b8b6b81b0c993d85240c846ebffd6206fd2e468c0`.
- API generation/observed generation 44/44, replicas 1/1/1/1 in the accepted report; live reverified for recovery on 14 November.
- Successor replaces only API/Infrastructure DLL/PDB; other runtime/dependency/configuration hashes match its parent. Retained image scan had no HIGH/CRITICAL findings.
- Admin and Storefront did not change. Their current references and all other live workload identities are in `docs/PRODUCTION-BASELINE.md`.

Existing packaging/deployment scripts, exact source manifest, focused runner and regression sources are retained. Historical raw browser/SQL data, screenshots, TRX/logs and full image archives remain local rather than being bulk-uploaded.

No functionality change, new QA campaign, build, redeployment, infrastructure modification or business-data action was performed by the GitHub recovery/cleanup task.
