# Global search/reindex/recovery — historical extract and current boundary

## Provenance and status
Historical title: **POS-ADMIN-CERTIFICATION-01 PASS 11.4**, dated 2026-05-30.
This is a documented historical extract, not recertification of the current candidate.
Original retained in staging evidence:
`app1.7-20261030/opensearch-implementation-20261030-r1/test-source-view/docs/architecture/global-search-reindex-recovery-certification-findings.md`.
SHA-256 `16be6875e24fa324a82cca49e57bac5cc5cfd3877ace7ad75e7ecc719cac3631` matches its historical evidence manifest.

## 1. Operational Inventory Findings
Historical inventory covers global/tenant reindex, repair, replay, recovery, startup and diagnostics. Current Generation changes do not authorize separate financial, CommerceHub or other workstreams.

## 2. Reindex Findings
Generation physical admission requires independently pinned cluster/index UUID and mapping fingerprint. Aliases and pipelines do not confer private Generation mutation rights.

## 3. Search Repair Findings
Current service repair routes active Generation work through durable signals and current SQL recomposition. No synthetic seed data or indexed documents were added to fix Markdown omissions.

## 4. Recovery Findings
The new durable operation lifecycle distinguishes Pending, Submitted, OutcomeUnknown, Applied, VisibleAfterRefresh, Acknowledged and ConfirmedRejected. It remains unregistered without an actual atomic provider boundary.

## 5. Replay Findings
Stable operation identity and immutable digest are required for retries. An unknown legacy attempt remains a blocker; a new retry receipt does not cancel an independently delayed original.

## 6. Auto-Healing Findings
No new automatic Generation recovery or cutover is enabled. `MarketplaceGenerationRegistration` rejects activation options at startup.

## 7. Startup Findings
After restart, Submitted is treated as unknown. Provider result reconciliation precedes retry, and refreshed exact content precedes durable acknowledgement.

## 8. Governance-Preservation Findings
Current SQL approval boundaries are not silently repinned. Package manifests/lockfiles and historical SQL Governance evidence remain unchanged.

## 9. Auditability Findings
Immutable operation identities and append-only transition events preserve crash/retry history. Production migration/grants have not been executed or certified.

## 10. Tenant-Boundary Findings
Reader continuations remain authenticated actor/filter/page-size bound. A pinned derivative index is not a transaction-wide snapshot of live SQL eligibility.

## 11. Chaos Findings
Deterministic tests model processing timeout, delayed requests, duplicate requests, digest conflicts, newer revisions, tombstones and acknowledgement persistence failure. Their simulated atomic boundary is explicitly not stock OpenSearch.

## 12. Runtime Hardening Performed
Production raw mutation transport is blocked where a true global atomic write boundary is absent. No SQL lease, local lock or per-document epoch is represented as provider-global fencing.

## 13. Monitoring Improvements
Recovery states distinguish transport uncertainty from confirmed rejection and applied-but-not-yet-visible content. Existing operation telemetry is preserved.

## 14. Governance Updates
Historical PASS headings are recoverable from verified archived source evidence. These extracts do not issue new deployment or certification authority.

## 15. Honest Remaining Debt
The atomic write-boundary provider implementation is absent. Independent-client role/network restrictions, source-to-provider lifecycle, crash recovery, migration and searchable visibility must receive real provider proof in Runner. Generation remains not production-ready.
