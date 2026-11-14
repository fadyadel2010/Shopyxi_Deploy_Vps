# Independent audit and production rollout — final r3 acceptance

Date: 2026-11-11 UTC. Status: **PASS — automatic wildcard Storefront production
rollout and post-retirement Ahram acceptance completed**. The timestamped
receipts remain authoritative for every intermediate failure and final outcome.

## Candidate and provenance

- Isolated source: `D:\Shopixy-Automatic-Subdomains-Audit-R2-20261111\source`.
- Original authoritative r1 package and original sealed archive remain unchanged
  at `D:\Shopixy-APP82-20261109\successor-assurance-r11-cart-http-contract\source\deploy\automatic-subdomains`.
- r1 stopped because its inventory listed 82 DLLs but only 64 were in the image.
  Additional independent review found routing, lease concurrency, outbox and
  monitoring defects. r1 is **not** an approved fallback candidate.
- New immutable tag: `shopixy/api:automatic-subdomains-ce7fa5ea422e-r2`.
- New immutable digest:
  `sha256:1185031a83553260e30f4acc63726469446341eb6c9e00452e102cd8817444aa`.
- Archive SHA-256:
  `8d787512f680759954f811a4e0f78d5f051efb01633df158c6f10a6b0512eb52`.
- Source-input manifest SHA-256:
  `f6439e9fb92bcd1521a4ea1b3aab10fc78f180917a873bceea9b1cc5114339c5`;
  all 2,603 explicitly selected source/build inputs independently matched and
  the image carries the matching source-input label.
- Every OCI blob and all **64 actual published DLLs** match the r2 inventory.
  SDK is `10.0.404`, runtime is `10.0.15`, UID/GID remains `1654:1654`.
- Fresh Trivy HIGH/CRITICAL findings: **0**; NuGet vulnerability gate passed.
- The accepted emergency rollback image `5efeb704...` is retained locally and
  remains disclosed as having the four previously recorded HIGH findings. It is
  not represented as a clean steady-state release.
- SQL is unchanged, SHA-256
  `b6083cefea5b6e5257dc020a9762d6a20ccc8fab5f3cd3adc8f649b0023489b7`.
- Production ultimately advanced to the independently sealed r3 successor
  `sha256:860d691bb8c7a4dc463d4dfbd99dfe595e963cce3e32e6392d9e3ce8cb3dbc5a`
  after the pre-DNS unknown-host probe found r2's remaining `400` contract.
  r2 is retained as immediate application rollback; r1 remains disallowed.

## Defects corrected and independently retested

| Area | Classification | Evidence / correction |
|---|---|---|
| Original inventory mismatch | Resolved by new candidate | Exact r2 publication inventory and source seal; original failure retained |
| Assurance route priority override | Fixed / PASS | All 89 reserved labels excluded in four wildcard rules; 12 actual Traefik probes |
| NGINX reserved-label authority | Fixed / PASS | Canonical registry synchronized; exact assurance behavior preserved; both `nginx -t` checks |
| DNS multi-label assumption | Fixed | DNS synthesis is not tenant admission; explicit TLS/routing negative probes |
| Stale consumer lease checkpoint | Fixed / PASS | State, LeaseId, AttemptCount concurrency fencing; stale tracker reload and real PostgreSQL tests |
| Stale admin retry | Fixed / PASS | Guarded 409 rather than overwriting successor authority |
| Consume-scoped outbox atomicity | Fixed / PASS | Provisioning/request/ready/StoreConfig events use SQL outbox inside explicit transactions |
| Queue-specific monitoring with no labels | Fixed | PostgreSQL pending/overdue metrics plus recovery heartbeat; no-data alerts fail closed |
| Token installer RBAC error handling | Fixed | Discover actual SAs, impersonate all groups; test get/list/watch; abort query errors before Secret install |
| New table inaccessible to restricted runtime | Fixed before migration | Production has no default table grants; separate guarded SELECT/INSERT/UPDATE grant only on the new table, with no DELETE/TRUNCATE or schema/default privilege changes |
| Schema metadata not advanced | Fixed after fail-closed startup | Guarded one-row `SystemSchemaVersions.postgres` advancement from exact predecessor to exact applied migration; no business data changed |
| Prometheus blocked from API edge target | Fixed after live post-rollout probe | Additive ingress/egress NetworkPolicies permit only the exact Prometheus and API pod labels on sidecar port 18080; Service/public authority unchanged |
| Unknown automatic hostname returned `400 TENANT_REQUIRED` | Fixed in immutable r3 successor after direct-origin probe | StoreConfig bootstrap alone is marked anonymous-tenant and returns opaque `404 STOREFRONT_NOT_FOUND`; general tenant admission remains fail-closed |
| Malformed-host acceptance used a libcurl-rejected URL | Fixed before DNS cutover | Probe now sends the malformed Host header to the origin IP over intentionally insecure TLS; Traefik grammar, not libcurl parsing, is tested |
| Assurance exact-route 403 omitted no-store | Fixed after first public acceptance | Existing no-store middleware is added ahead of existing internal-only middleware on only both assurance routes; hosts/services/TLS/denial unchanged |
| Registration edge-closed but internally enabled | Fixed during final defense-in-depth check | Explicit production environment override sets `PlatformSecurity__RegistrationEnabled=false`; public edge denial remains unchanged |
| Ahram sign-in | Resolved / PASS | Incorrect stored-credential match diagnosed; explicitly authorized single-owner recovery; normal application reset and ordinary owner login |
| Generic login 401 wording | Existing privacy contract | No arbitrary exception/credential messages exposed; no authentication weakening |

The direct-origin pre-DNS gate therefore stopped before any wildcard DNS mutation
and produced immutable successor r3 rather than weakening acceptance. r3 is
`docker.io/shopixy/api@sha256:860d691bb8c7a4dc463d4dfbd99dfe595e963cce3e32e6392d9e3ce8cb3dbc5a`
(`automatic-subdomains-5c2cc1226a8e-r3`). Its gzip archive SHA-256 is
`2318a80aee932ef50bf3900407d1f3b0384b097b8e230bedd030a7205ba847a0`.
Independent archive/blob verification covers 64 DLLs and 2,603 source inputs;
runtime UID/GID remain 1654, .NET remains 10.0.15, focused unknown-host tests
pass, local dependency-backed runtime smoke passes, and Trivy reports zero
HIGH/CRITICAL findings. r2 remains available as immediate reachability rollback.

The prior 70 focused registration/provisioning tests plus both r3 unknown-host
tests passed again with no skipped cases,
including PostgreSQL fencing/retry/outbox and EF pending-model checks. Initial
failures and build-lock remediation logs remain retained, not overwritten as PASS.
Portable test locks and `linux-musl-x64` production locks are now separate.

## Live preflight and recovery

- API and Storefront are singleton `Recreate`. Frontend digests, authentication,
  resource requests and accepted no-application-memory-limit policy are preserved
  by exact guarded comparisons. Registration is now closed both at the edge and
  by explicit API configuration.
- Read-only SQL preflight asserts the exact release lineage and canonical
  tenant/owner/StoreConfig identities; all assertions passed.
- Disposable schema-only production clone: first migration apply PASS; second
  apply PASS with unchanged schema; duplicate-slug failure atomically rolls back.
  No production tenant rows or financial data were copied into the fixture.
- Prometheus replaces unavailable metrics-server: approximately 30.5 GiB node
  memory available, 81% CPU idle, MemoryPressure zero at the recorded baseline.
- Fresh pre-release CNPG backup `automatic-subdomains-preflight-r2-20261111`
  completed at `2026-11-11T19:49:27Z`; the Barman object-store catalog independently
  confirms that recovery point and a window beginning `2026-10-27T17:12:44Z`.
  WAL archiver failure count is zero. No new full disaster-restore drill is claimed.
- Manifest server dry-runs, forward patch dry-runs, exact rollback spec round
  trips and unchanged resource policy all passed. Actual app SAs cannot get,
  list or watch cert-manager Secrets; cert-manager can.
- Exact Cloudflare token policy: active, only Zone Read + DNS Write for
  `shopyxi.com`. Cloudflare Full (strict), active Universal SSL, all non-Ahram
  baseline records, nameservers, and the pre-existing disabled DNSSEC state remain
  unchanged. No token value is in the evidence.
- Canonical owner ordinary browser session returned 200, exact owner and tenant
  UUIDs, role Owner, secure Strict cookies and no-store/DYNAMIC responses.
- Owner dashboard, bounded POS product read, and exact existing StoreConfig read
  all returned 200 with no-store; no business records were modified for acceptance.
- Exact r3 image local dependency-backed smoke passed as non-root with liveness,
  inherited Docker healthcheck and all three actual provisioning metric names.
  The fixture uses Development with isolated local dependencies; it is not a claim
  to have already accepted production TLS, storage or routing. Earlier fixture
  startup/network-namespace failures remain in the receipts.
- Grafana 13.3 independently loaded all four exact r2 alert rules, unpaused, with
  the expected no-data policies. All expressions parse on production Prometheus.
- Installer negative tests proved RBAC query errors and unexpected application
  authority abort before any Secret mutation. Production-lock comparison found
  no changed resolved direct/transitive package versions from the original input.

## Final production state

1. API is 1/1 Ready on immutable r3; Storefront is 1/1 Ready on its unchanged
   digest. Both remain singleton `Recreate`; resource requests and accepted
   application memory-limit policy are unchanged.
2. The additive migration, restricted table grant, and guarded schema metadata
   advancement pass postflight. There are zero provisioning-operation rows.
3. The production certificate for `shopyxi.com` and `*.shopyxi.com` is Ready
   through `2027-02-09`. Both wildcard routes are active.
4. Cloudflare contains exactly one proxied wildcard A record. Ahram's exact A
   record, exact routes, Certificate, and legacy TLS Secret are retired. All 14
   other baseline DNS records are byte-for-byte preserved by the guarded check.
5. Public and direct-origin acceptance pass after retirement: canonical Ahram
   `200`, unknown tenant `404`, private health/registration `403`, malformed and
   multi-label `404`, platform routes preserved, and no unexpected Cloudflare HIT.
6. Ordinary Ahram owner session, dashboard, products, admin StoreConfig, and
   wildcard bootstrap return `200` with expected canonical identities and
   no-store. Tenant/user/StoreConfig counts and all recorded financial aggregates
   are unchanged; no tenant was created.
7. Prometheus reports API up, pending/overdue provisioning zero, and a fresh
   recovery heartbeat. Grafana retains 58 existing plus 4 new rules and its
   existing contact point; no Prometheus alert is firing.
8. The fresh CNPG recovery point remains completed. Retained failed search-backup
   pods are historical; the latest scheduled search backup Job completed and no
   current backup alert is firing.

## Primary independent receipts

All paths below are relative to this package:

- `audit-results/artifact-identity-r2.json`
- `audit-results/registration-provisioning-r2-native-c.trx`
- `audit-results/edge-validation-r2b.json`
- `audit-results/migration-upgrade-idempotency-r2.json`
- `audit-results/predeployment-gates-r2.json`
- `audit-results/live-preflight-r2.txt`
- `audit-results/prometheus-preflight.json`
- `audit-results/grafana-live-baseline.json`
- `audit-results/cloudflare-token-policy-hardening.json`
- `audit-results/cloudflare-dashboard-baseline.json`
- `audit-results/authorized-owner-recovery.json`
- `audit-results/owner-session-before-rollout.json`
- `audit-results/owner-pos-before-rollout.json`
- `audit-results/runtime-smoke-r2-h.json`
- `audit-results/grafana-provisioning-r2.json`
- `audit-results/recovery-point-r2.json`
- `audit-results/monitoring-expression-recovery-catalog-r2.json`
- `audit-results/token-installer-failclosed-r2.json`
- `audit-results/production-lock-package-comparison-r2.json`
- `audit-results/source-delta-from-original-r2.json`
- `audit-results/database-runtime-default-authority-r2.txt`
- `audit-results/runtime-table-grants-proof-r2.json`
- `audit-results/artifact-identity-r3.json`
- `audit-results/runtime-smoke-r3.json`
- `audit-results/registration-provisioning-r3.log`
- `audit-results/direct-origin-pre-dns-r3.json`
- `audit-results/public-acceptance-before-retirement-r2.json`
- `audit-results/public-acceptance-after-retirement-r2.json`
- `audit-results/postrollout-live-r2.json`
- `audit-results/final-cluster-r3.json`
- `audit-results/cloudflare-final-r3.json`
- `audit-results/owner-pos-final-r3.json`
- `audit-results/recovery-monitoring-final-r3.json`
- `audit-results/closed-registration-final-r3.json`
- `audit-results/grafana-final-r3.json`

Follow-through inspection before migration confirmed the existing runtime role is
neither superuser nor CREATE ROLE/CREATE DATABASE/BYPASSRLS. The exact supplemental
grant SQL passed twice on a disposable database; effective runtime SELECT, INSERT
and UPDATE succeeded, while DELETE/TRUNCATE remained denied. This correction
changes deployment tooling only, not the immutable image or sealed migration SQL.
The VPS Python checksum helper was likewise made compatible with its installed
Python version using bounded streaming SHA-256; transferred artifacts were then
independently reverified before import.

The first Grafana production patch used a new subPath file mount beneath the
existing ConfigMap directory mount. Kubernetes accepted the object but the OCI
runtime correctly rejected the nested bind mount. The stage timed out and stopped;
API/Storefront were untouched. The guarded rollback restored Grafana 1/1. The
replacement now composes the exact unchanged three-file `phase4j-alerting` data
(base UID `97c58728-d862-463a-bcea-f67ec02578c8`, data SHA-256
`b30923d53ec085727e21f2a4da9305a1e534c6ae16c997df0ed8f476579ff68c`)
with one added file in a new immutable ConfigMap, then swaps only the existing
directory volume's ConfigMap name. Forward dry-run and exact rollback round-trip
pass; the failed receipt and safe Kubernetes events remain preserved.

The Docker archive import also exposed only its reviewed tag plus a synthetic
containerd import repoDigest. Before workload rollout, the reviewed manifest
digest was explicitly bound to that same local image, and CRI resolution verified
the exact deployment digest and UID 1654. No registry pull or image substitution
was permitted.

The evidence seal must be regenerated after each evidence/document change and
verified again immediately before transfer. Never include protected credentials
or recursively seal the original audit directory.
