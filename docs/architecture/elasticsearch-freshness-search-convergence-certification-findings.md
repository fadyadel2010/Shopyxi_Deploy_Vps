# Elasticsearch freshness/search convergence — historical extract and current boundary

## Provenance and status

The historical title was **POS-ADMIN-CERTIFICATION-01 PASS 10.4** (2026-05-29).
This is a provenance-labelled extract, NOT a renewed certification of this candidate.
The original is retained in staging evidence under
`app1.7-20261030/opensearch-implementation-20261030-r1/test-source-view/docs/architecture/elasticsearch-freshness-search-convergence-certification-findings.md`.
Its SHA-256 is `38cf176e094530ac92125d78f7c44cf0addfee8fb2b22371c511968483d94f08`,
matching that evidence directory's `evidence-manifest.json`.
The candidate omitted this documentation; a FileNotFoundException is not a missing indexed business document.

## Search Inventory Findings
The historical inventory names products, categories, CMS, CommerceHub, public affiliate discovery and legacy service marketplace indexes. Generation work here is restricted to service marketplace, not certification of every index.

## Search Consumer Findings
Current `ServiceDiscoveryProjectionConsumer` routes active-generation signals through `IMarketplaceGenerationMutationRouter`. Legacy paths retain SQL authority checks before a Generation pointer exists.

## Search Freshness Findings
Generation dispatch intent, transport response and searchable visibility are distinct. The new recovery protocol records Submitted before submission and requires exact refreshed verification before Acknowledged.

## Product Search Findings
Historical findings describe SQL overlays over derivative candidates. Product search was not modified or recertified by Generation gap closure.

## Category/CMS Search Findings
Historical findings require current SQL lineage/publication and stale suppression. No new category/CMS certification is claimed.

## Suggestion/Autocomplete Findings
Historical findings treat suggestions as candidates, not publication authority. Generation continuation pins do not certify suggestion freshness.

## Facet/Count Findings
Historical findings explicitly leave exact all-page count/facet convergence as debt. A Generation refresh or document count alone is not delivery or authority proof.

## Reindex Findings
Historical CommerceHub alias promotion is a separate path. Generation targets must be exact physical identities with no aliases/pipelines, as checked by `MarketplacePhysicalAdmission`.

## Repair Findings
Authorized service repair emits durable source-derived signals for active Generation. Repair cannot grant itself raw Generation write authority.

## Drift Detection Findings
`ElasticsearchDriftReconciliationJob` retains current-SQL service/provider/tenant recomposition. No provider scan was executed in this gap-closure task.

## Cross-System Convergence Findings
SQL remains authority. Broker messages wake durable recomposition; they are not frozen payload authority. Missing documentation cannot be repaired with invented SQL seeds or search documents.

## Startup/Recovery Findings
Generation coordinator registration remains fail-closed with no automatic recovery/cutover worker. A restart cannot turn Submitted/OutcomeUnknown into Acknowledged.

## Chaos Findings
Local failure injection covers response loss, processing timeout, persistence failure, duplicate identity, digest conflict and source-order overlap. A simulated atomic server is NOT OpenSearch certification.

## Runtime Hardening Performed
The dedicated production transport now refuses raw mutations without an enforced atomic write boundary. Recovery identities bind cluster, physical UUID, generation, operation, revision, epoch and payload digest. No provider adapter is installed.

## Monitoring Improvements
Append-only recovery state events preserve durable diagnostic distinctions instead of converting every timeout to retry success. Existing telemetry is unchanged.

## Governance Updates
Historical headings are recovered with provenance, not rewritten as current PASS. The whole-production SQL approval pin is still enforced, and provider checks are deferred to Runner.

## Honest Remaining Debt
Provider-global atomic epoch enforcement, production boundary isolation, deployed roles and real PostgreSQL/OpenSearch lifecycle proof remain required. External Generation writes and readers remain disabled. These extracts preserve historical documentation coverage without asserting current production readiness.
